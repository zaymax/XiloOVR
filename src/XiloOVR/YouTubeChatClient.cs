#nullable enable
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XiloOVR;

/// <summary>
/// Read-only YouTube live chat client. Works the same way the youtube.com live chat
/// page does: loads /live_chat for the stream, extracts the innertube API key and the
/// chat continuation token from the embedded page data, then polls the public
/// live_chat/get_live_chat endpoint. No API key or login required from the user; the
/// channel setting accepts an @handle, a channel/watch URL, or a bare video id.
/// Messages are tagged "yt" and merge into the same feed as Twitch; Super Chats
/// become alert messages.
/// </summary>
public sealed partial class YouTubeChatClient : IChatSource, IDisposable
{
    private const int ResolveRetryMs = 60_000; // channel set but no live stream found
    private const int DefaultPollMs = 4000;

    private readonly ConcurrentQueue<ChatMessage> _incoming = new();

    private volatile bool _running = true;
    private volatile string _channel = "";
    private Thread? _thread;

    /// <summary>Human-readable connection state for the settings panel.</summary>
    public string StatusLine { get; private set; } = "off";

    public void Start(string channel)
    {
        _channel = channel.Trim();
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "youtube-chat" };
        _thread.Start();
    }

    /// <summary>Applies a config change; reconnects when the channel differs.</summary>
    public void SetChannel(string channel)
    {
        channel = channel.Trim();
        if (channel != _channel)
            _channel = channel; // the poll loop notices and re-resolves
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
        using var http = CreateHttpClient();
        while (_running)
        {
            var channel = _channel;
            if (channel.Length == 0)
            {
                StatusLine = "off";
                Sleep(500, channel);
                continue;
            }

            try
            {
                StatusLine = "looking for a live stream ...";
                var videoId = ResolveVideoId(http, channel);
                if (videoId == null)
                {
                    StatusLine = "no live stream found, retrying ...";
                    Sleep(ResolveRetryMs, channel);
                    continue;
                }

                StatusLine = $"connecting to live chat of {videoId} ...";
                var session = OpenChatSession(http, videoId);
                if (session == null)
                {
                    StatusLine = "live chat unavailable, retrying ...";
                    Sleep(ResolveRetryMs, channel);
                    continue;
                }

                Console.WriteLine($"YouTube chat: joined live chat of video {videoId}");
                StatusLine = $"joined live chat ({videoId})";
                _incoming.Enqueue(new ChatMessage("sys", "XiloOVR", $"joined YouTube live chat", null));
                PollChat(http, session, channel);
            }
            catch (Exception ex)
            {
                if (_running && channel == _channel)
                {
                    Console.Error.WriteLine($"YouTube chat: {ex.Message}");
                    StatusLine = "disconnected, retrying ...";
                    Sleep(15_000, channel);
                }
            }
        }
    }

    private sealed record ChatSession(string ApiKey, string ClientVersion, string Continuation);

    /// <summary>Keeps requesting chat pages until the stream ends or the channel changes.</summary>
    private void PollChat(HttpClient http, ChatSession session, string channel)
    {
        var continuation = session.Continuation;
        while (_running && channel == _channel)
        {
            var url = $"https://www.youtube.com/youtubei/v1/live_chat/get_live_chat?key={session.ApiKey}&prettyPrint=false";
            var body = JsonSerializer.Serialize(new
            {
                context = new { client = new { clientName = "WEB", clientVersion = session.ClientVersion } },
                continuation,
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var response = http.Send(request);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());

            if (!doc.RootElement.TryGetProperty("continuationContents", out var contents))
            {
                Console.WriteLine("YouTube chat: stream ended.");
                StatusLine = "stream ended";
                return; // outer loop re-resolves (maybe a new stream started)
            }

            var liveChat = contents.GetProperty("liveChatContinuation");
            if (liveChat.TryGetProperty("actions", out var actions))
            {
                foreach (var action in actions.EnumerateArray())
                {
                    var message = ParseAction(action);
                    if (message != null)
                        _incoming.Enqueue(message);
                }
            }

            var timeoutMs = DefaultPollMs;
            if (liveChat.TryGetProperty("continuations", out var continuations) && continuations.GetArrayLength() > 0)
            {
                var next = continuations[0];
                foreach (var kind in new[] { "invalidationContinuationData", "timedContinuationData", "reloadContinuationData" })
                {
                    if (!next.TryGetProperty(kind, out var data))
                        continue;
                    continuation = data.GetProperty("continuation").GetString() ?? continuation;
                    if (data.TryGetProperty("timeoutMs", out var timeout))
                        timeoutMs = Math.Clamp(timeout.GetInt32(), 1500, 15_000);
                    break;
                }
            }
            Sleep(timeoutMs, channel);
        }
    }

    private static ChatMessage? ParseAction(JsonElement action)
    {
        if (!action.TryGetProperty("addChatItemAction", out var add) ||
            !add.TryGetProperty("item", out var item))
            return null;

        if (item.TryGetProperty("liveChatTextMessageRenderer", out var text))
            return new ChatMessage("yt", AuthorOf(text), RunsToText(text), null);

        // Super Chats carry money; treat them as alerts with the amount up front.
        if (item.TryGetProperty("liveChatPaidMessageRenderer", out var paid))
        {
            var amount = paid.TryGetProperty("purchaseAmountText", out var amountText) &&
                         amountText.TryGetProperty("simpleText", out var simple)
                ? simple.GetString() ?? ""
                : "";
            var body = RunsToText(paid);
            var message = amount.Length > 0 ? $"Super Chat {amount}" : "Super Chat";
            if (body.Length > 0)
                message += $": {body}";
            return new ChatMessage("yt", AuthorOf(paid), message, null, IsAlert: true);
        }

        return null;
    }

    private static string AuthorOf(JsonElement renderer) =>
        renderer.TryGetProperty("authorName", out var author) &&
        author.TryGetProperty("simpleText", out var name)
            ? name.GetString() ?? "?"
            : "?";

    /// <summary>Message text arrives as runs of plain text and emoji; emoji become their :shortcut:.</summary>
    private static string RunsToText(JsonElement renderer)
    {
        if (!renderer.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("runs", out var runs))
            return "";

        var sb = new StringBuilder();
        foreach (var run in runs.EnumerateArray())
        {
            if (run.TryGetProperty("text", out var text))
            {
                sb.Append(text.GetString());
            }
            else if (run.TryGetProperty("emoji", out var emoji))
            {
                if (emoji.TryGetProperty("shortcuts", out var shortcuts) && shortcuts.GetArrayLength() > 0)
                    sb.Append(shortcuts[0].GetString());
                else if (emoji.TryGetProperty("emojiId", out var id))
                    sb.Append(id.GetString());
            }
        }
        return sb.ToString().Trim();
    }

    // ---- stream discovery --------------------------------------------------------------

    /// <summary>Turns the channel setting (@handle, URL, or video id) into a live video id.</summary>
    private static string? ResolveVideoId(HttpClient http, string channel)
    {
        // A bare video id (11 url-safe chars) or a watch/youtu.be URL points at the video directly.
        var direct = VideoIdRegex().Match(channel);
        if (direct.Success)
            return direct.Groups[1].Value;
        if (VideoIdOnlyRegex().IsMatch(channel))
            return channel;

        // Everything else is a channel reference; its /live page redirects to the current stream.
        var channelPath = channel switch
        {
            _ when channel.StartsWith("http", StringComparison.OrdinalIgnoreCase) =>
                new Uri(channel).AbsolutePath.TrimEnd('/'),
            _ when channel.StartsWith('@') => "/" + channel,
            _ => "/@" + channel,
        };
        var html = http.GetStringAsync($"https://www.youtube.com{channelPath}/live").GetAwaiter().GetResult();

        var canonical = CanonicalWatchRegex().Match(html);
        if (canonical.Success)
            return canonical.Groups[1].Value;

        // No canonical watch link means the channel exists but is not live right now.
        return null;
    }

    /// <summary>Loads the live_chat page and pulls out the API key, client version and continuation.</summary>
    private static ChatSession? OpenChatSession(HttpClient http, string videoId)
    {
        var html = http.GetStringAsync($"https://www.youtube.com/live_chat?is_popout=1&v={videoId}").GetAwaiter().GetResult();

        var apiKey = ApiKeyRegex().Match(html);
        var clientVersion = ClientVersionRegex().Match(html);
        if (!apiKey.Success || !clientVersion.Success)
            return null;

        var initialData = ExtractJsonObject(html, "ytInitialData");
        if (initialData == null)
            return null;

        using var doc = JsonDocument.Parse(initialData);
        var continuation = FindInitialContinuation(doc.RootElement);
        return continuation == null
            ? null
            : new ChatSession(apiKey.Groups[1].Value, clientVersion.Groups[1].Value, continuation);
    }

    /// <summary>
    /// The chat page offers "Top chat" and "Live chat" (everything) views; take the last
    /// entry, which is the unfiltered one, and fall back to the page's own continuation.
    /// </summary>
    private static string? FindInitialContinuation(JsonElement root)
    {
        try
        {
            var liveChat = root.GetProperty("contents").GetProperty("liveChatRenderer");

            if (liveChat.TryGetProperty("header", out var header) &&
                header.TryGetProperty("liveChatHeaderRenderer", out var headerRenderer) &&
                headerRenderer.TryGetProperty("viewSelector", out var selector) &&
                selector.TryGetProperty("sortFilterSubMenuRenderer", out var menu) &&
                menu.TryGetProperty("subMenuItems", out var items) && items.GetArrayLength() > 0)
            {
                var last = items[items.GetArrayLength() - 1];
                if (last.TryGetProperty("continuation", out var cont) &&
                    cont.TryGetProperty("reloadContinuationData", out var reload))
                    return reload.GetProperty("continuation").GetString();
            }

            if (liveChat.TryGetProperty("continuations", out var continuations) && continuations.GetArrayLength() > 0)
            {
                foreach (var property in continuations[0].EnumerateObject())
                {
                    if (property.Value.TryGetProperty("continuation", out var value))
                        return value.GetString();
                }
            }
        }
        catch (KeyNotFoundException)
        {
            // fall through: page layout changed or chat is disabled
        }
        return null;
    }

    /// <summary>Extracts the JSON object assigned to a variable in the page ("name = {...};").</summary>
    private static string? ExtractJsonObject(string html, string variableName)
    {
        var index = html.IndexOf(variableName, StringComparison.Ordinal);
        if (index < 0)
            return null;
        var start = html.IndexOf('{', index);
        if (start < 0)
            return null;

        // Walk to the matching closing brace, skipping strings and escapes.
        var depth = 0;
        var inString = false;
        for (var i = start; i < html.Length; i++)
        {
            var c = html[i];
            if (inString)
            {
                if (c == '\\')
                    i++;
                else if (c == '"')
                    inString = false;
                continue;
            }
            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return html[start..(i + 1)];
                    break;
            }
        }
        return null;
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        // Skips the EU consent interstitial that would otherwise replace every page.
        http.DefaultRequestHeaders.Add("Cookie", "SOCS=CAI; CONSENT=YES+");
        return http;
    }

    /// <summary>Interruptible sleep so channel changes and shutdown apply quickly.</summary>
    private void Sleep(int totalMs, string channel)
    {
        for (var waited = 0; waited < totalMs && _running && channel == _channel; waited += 250)
            Thread.Sleep(250);
    }

    [GeneratedRegex(@"(?:youtube\.com/watch\?v=|youtu\.be/|youtube\.com/live/)([A-Za-z0-9_-]{11})")]
    private static partial Regex VideoIdRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdOnlyRegex();

    [GeneratedRegex("""<link rel="canonical" href="https://www\.youtube\.com/watch\?v=([A-Za-z0-9_-]{11})""")]
    private static partial Regex CanonicalWatchRegex();

    [GeneratedRegex(""""INNERTUBE_API_KEY":"([^"]+)"""")]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex(""""INNERTUBE_CONTEXT_CLIENT_VERSION":"([^"]+)"""")]
    private static partial Regex ClientVersionRegex();

    public void Dispose()
    {
        _running = false;
        _thread?.Join(1000);
    }
}
