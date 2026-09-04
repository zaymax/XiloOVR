#nullable enable
using System.Collections.Concurrent;
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
public sealed class TwitchFollowPoller : IChatSource, IDisposable
{
    private const int PollIntervalMs = 20_000;

    private readonly ConcurrentQueue<ChatMessage> _incoming = new();

    private volatile bool _running = true;
    private volatile string _channel = "";
    private volatile string _clientId = "";
    private volatile string _token = "";
    private volatile bool _credentialsRejected;
    private Thread? _thread;

    /// <summary>Human-readable state for the settings panel.</summary>
    public string StatusLine { get; private set; } = "off";

    public void Start(AppConfig config)
    {
        Apply(config);
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "twitch-follows" };
        _thread.Start();
    }

    public void Configure(AppConfig config)
    {
        if (config.TwitchChannelNormalized == _channel
            && config.TwitchClientId.Trim() == _clientId
            && config.TwitchTokenNormalized == _token)
            return;
        Apply(config);
        _credentialsRejected = false;
    }

    private void Apply(AppConfig config)
    {
        _channel = config.TwitchChannelNormalized;
        _clientId = config.TwitchClientId.Trim();
        _token = config.TwitchTokenNormalized;
    }

    public bool TryDrain(List<ChatMessage> into)
    {
        var any = false;
        while (_incoming.TryDequeue(out var message))
        {
            into.Add(message);
            any = true;
        }
        return any;
    }

    private void RunLoop()
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

        while (_running)
        {
            var channel = _channel;
            var clientId = _clientId;
            var token = _token;
            var key = $"{channel}\n{clientId}\n{token}";

            if (channel.Length == 0 || clientId.Length == 0 || token.Length == 0)
            {
                StatusLine = clientId.Length == 0 && channel.Length > 0
                    ? "follows off (set TwitchClientId + token)"
                    : "off";
                Sleep(1000);
                continue;
            }
            if (_credentialsRejected)
            {
                StatusLine = "follows: token rejected (needs moderator:read:followers)";
                Sleep(5000);
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
                    Sleep(60_000);
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
                        _incoming.Enqueue(new ChatMessage("tw", follower.Name, "just followed!", null, IsAlert: true));
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

            Sleep(PollIntervalMs);
        }
    }

    /// <summary>Interruptible sleep so config changes and shutdown apply quickly.</summary>
    private void Sleep(int totalMs)
    {
        var channel = _channel;
        var clientId = _clientId;
        var token = _token;
        for (var waited = 0; waited < totalMs && _running; waited += 250)
        {
            if (channel != _channel || clientId != _clientId || token != _token)
                return;
            Thread.Sleep(250);
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

    public void Dispose()
    {
        _running = false;
        _thread?.Join(1000);
    }
}
