#nullable enable
using System.Net.Http.Headers;
using System.Text.Json;

namespace XiloOVR;

/// <summary>
/// Polls the Twitch Helix "channel followers" endpoint and turns new followers into
/// alert messages. Twitch only exposes followers through the API (not IRC), and the
/// endpoint needs an app client id plus a user token with the moderator:read:followers
/// scope, so this runs only when TwitchClientId and TwitchOAuthToken are both set.
/// The first successful poll is the baseline; alerts fire for followers after that.
/// </summary>
public sealed class TwitchFollowPoller : ChatSourceBase
{
    private const int PollIntervalMs = 20_000;

    private volatile string _channel = "";
    private volatile string _clientId = "";
    private volatile string _token = "";
    private volatile bool _credentialsRejected;

    public override string Name => "Follows";

    protected override string ThreadName => "twitch-follows";

    protected override bool ApplyConfig(AppConfig config)
    {
        if (config.TwitchChannelNormalized == _channel
            && config.TwitchClientId.Trim() == _clientId
            && config.TwitchTokenNormalized == _token)
            return false;
        _channel = config.TwitchChannelNormalized;
        _clientId = config.TwitchClientId.Trim();
        _token = config.TwitchTokenNormalized;
        return true;
    }

    protected override void OnReconfigured() => _credentialsRejected = false;

    protected override void RunLoop()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        string? broadcasterId = null;
        var configuredFor = "";
        // New follower = followed_at newer than this watermark. A plain id-set diff
        // over the newest-20 page would mis-fire when an unfollow shifts an old
        // follower back into the window, and would grow without bound.
        DateTimeOffset watermark = default;
        var atWatermark = new HashSet<string>(); // ids sharing the watermark second, for tie-breaks
        var baselined = false;

        while (Running)
        {
            var channel = _channel;
            var clientId = _clientId;
            var token = _token;
            var key = $"{channel}\n{clientId}\n{token}";
            bool SettingsUnchanged() => channel == _channel && clientId == _clientId && token == _token;

            if (channel.Length == 0 || clientId.Length == 0 || token.Length == 0)
            {
                StatusLine = clientId.Length == 0 && channel.Length > 0
                    ? "follows off (set TwitchClientId + token)"
                    : "off";
                SleepInterruptible(1000, SettingsUnchanged);
                continue;
            }
            if (_credentialsRejected)
            {
                StatusLine = "follows: token rejected (needs moderator:read:followers)";
                SleepInterruptible(5000, SettingsUnchanged);
                continue;
            }

            if (key != configuredFor)
            {
                configuredFor = key;
                broadcasterId = null;
                baselined = false;
                atWatermark.Clear();
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (http.DefaultRequestHeaders.Contains("Client-Id"))
                    http.DefaultRequestHeaders.Remove("Client-Id");
                http.DefaultRequestHeaders.Add("Client-Id", clientId);
            }

            try
            {
                broadcasterId ??= LookUpUserId(http, channel);
                if (broadcasterId == null)
                {
                    StatusLine = $"follows: channel '{channel}' not found";
                    SleepInterruptible(60_000, SettingsUnchanged);
                    continue;
                }

                var followers = FetchLatestFollowers(http, broadcasterId);
                if (!baselined)
                {
                    baselined = true; // baseline poll: record where "new" starts, no retro-alerts
                    watermark = followers.Count > 0 ? followers.Max(f => f.FollowedAt) : DateTimeOffset.MinValue;
                    atWatermark = new HashSet<string>(
                        followers.Where(f => f.FollowedAt == watermark).Select(f => f.Id));
                }
                else
                {
                    var fresh = followers
                        .Where(f => f.FollowedAt > watermark
                                    || (f.FollowedAt == watermark && !atWatermark.Contains(f.Id)))
                        .OrderBy(f => f.FollowedAt)
                        .ToList();
                    foreach (var follower in fresh)
                        Enqueue(new ChatMessage("tw", follower.Name, "just followed!", null, IsAlert: true));
                    if (fresh.Count > 0)
                    {
                        var newest = fresh[^1].FollowedAt;
                        if (newest > watermark)
                        {
                            watermark = newest;
                            atWatermark.Clear();
                        }
                        foreach (var follower in fresh.Where(f => f.FollowedAt == watermark))
                            atWatermark.Add(follower.Id);
                    }
                }
                StatusLine = "follow alerts on";
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                _credentialsRejected = true;
                Console.Error.WriteLine(
                    "Twitch follows: API rejected the credentials; the token needs the moderator:read:followers scope " +
                    "and must belong to the broadcaster or a moderator.");
                continue;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Twitch follows: poll failed ({ex.Message})");
                StatusLine = "follows: retrying ...";
            }

            SleepInterruptible(PollIntervalMs, SettingsUnchanged);
        }
    }

    private static string? LookUpUserId(HttpClient http, string login)
    {
        using var doc = GetJson(http, $"https://api.twitch.tv/helix/users?login={Uri.EscapeDataString(login)}");
        var data = doc.RootElement.GetProperty("data");
        return data.GetArrayLength() > 0 ? data[0].GetProperty("id").GetString() : null;
    }

    private static List<(string Id, string Name, DateTimeOffset FollowedAt)> FetchLatestFollowers(HttpClient http, string broadcasterId)
    {
        using var doc = GetJson(http, $"https://api.twitch.tv/helix/channels/followers?broadcaster_id={broadcasterId}&first=20");
        var result = new List<(string, string, DateTimeOffset)>();
        foreach (var entry in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var id = entry.GetProperty("user_id").GetString();
            var name = entry.TryGetProperty("user_name", out var display) ? display.GetString() : null;
            if (string.IsNullOrEmpty(id))
                continue;
            var followedAt = entry.TryGetProperty("followed_at", out var at)
                             && DateTimeOffset.TryParse(at.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;
            result.Add((id, string.IsNullOrEmpty(name) ? id : name!, followedAt));
        }
        return result;
    }

    private static JsonDocument GetJson(HttpClient http, string url)
    {
        using var response = http.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }

}
