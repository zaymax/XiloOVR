#nullable enable
using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace XiloOVR;

/// <summary>
/// One chat message; Source is a short platform tag ("tw", "yt", "sys").
/// IsAlert marks follow/sub/raid/superchat events that get banner treatment.
/// </summary>
public sealed record ChatMessage(string Source, string Author, string Text, string? ColorHex, bool IsAlert = false);

/// <summary>Anything that produces chat messages for the panel feed.</summary>
public interface IChatSource
{
    bool TryDrain(List<ChatMessage> into);
}

/// <summary>
/// Twitch chat client over IRC. Reads anonymously (justinfan login) when no credentials
/// are set; with TwitchUsername + TwitchOAuthToken it logs in properly and can send
/// messages. Requests the tags + commands capabilities, so besides PRIVMSG it also sees
/// USERNOTICE events (subs, resubs, gift subs, raids) which become alert messages.
/// Runs on a background thread, reconnects with backoff, and hands parsed messages to
/// the UI thread through a queue.
/// </summary>
public sealed class TwitchChatClient : IChatSource, IDisposable
{
    private const string Host = "irc.chat.twitch.tv";
    private const int Port = 6697;

    private readonly ConcurrentQueue<ChatMessage> _incoming = new();
    private readonly object _gate = new();

    private volatile bool _running = true;
    private volatile string _channel = "";
    private volatile string _username = "";
    private volatile string _token = "";
    private volatile bool _authFailed;
    private volatile bool _joined;
    private volatile bool _loggedIn;
    private TcpClient? _connection;
    private StreamWriter? _writer;
    private Thread? _thread;

    /// <summary>Human-readable connection state for the settings panel.</summary>
    public string StatusLine { get; private set; } = "off";

    /// <summary>True when logged in with a real account and joined, i.e. sending will work.</summary>
    public bool CanSend => _loggedIn && _joined;

    public void Start(AppConfig config)
    {
        ApplyCredentials(config);
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "twitch-chat" };
        _thread.Start();
    }

    /// <summary>Applies a config change; reconnects when the channel or login differs.</summary>
    public void Configure(AppConfig config)
    {
        var channel = Normalize(config.TwitchChannel);
        var username = config.TwitchUsername.Trim().ToLowerInvariant();
        var token = NormalizeToken(config.TwitchOAuthToken);
        if (channel == _channel && username == _username && token == _token)
            return;
        ApplyCredentials(config);
        _authFailed = false; // fresh credentials deserve a fresh attempt
        CloseConnection(); // wakes the thread out of its blocking read
    }

    private void ApplyCredentials(AppConfig config)
    {
        _channel = Normalize(config.TwitchChannel);
        _username = config.TwitchUsername.Trim().ToLowerInvariant();
        _token = NormalizeToken(config.TwitchOAuthToken);
    }

    /// <summary>Sends a chat message to the joined channel; returns false when not logged in.</summary>
    public bool SendMessage(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return true;
        if (!CanSend)
            return false;

        lock (_gate)
        {
            if (_writer == null)
                return false;
            try
            {
                _writer.WriteLine($"PRIVMSG #{_channel} :{text}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Twitch chat: send failed ({ex.Message})");
                return false;
            }
        }
        // Twitch does not echo our own messages back; show it in the feed ourselves.
        _incoming.Enqueue(new ChatMessage("tw", _username, text, null));
        return true;
    }

    /// <summary>Moves queued messages into the list; returns true when anything arrived.</summary>
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

    private static string Normalize(string channel) =>
        channel.Trim().TrimStart('#').ToLowerInvariant();

    private static string NormalizeToken(string token)
    {
        token = token.Trim();
        return token.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase) ? token["oauth:".Length..] : token;
    }

    private void RunLoop()
    {
        var attempt = 0;
        while (_running)
        {
            var channel = _channel;
            var username = _username;
            var token = _token;
            if (channel.Length == 0)
            {
                StatusLine = "off";
                _joined = false;
                Thread.Sleep(500); // chat disabled; wait for a config change
                continue;
            }

            var useLogin = username.Length > 0 && token.Length > 0 && !_authFailed;
            StatusLine = $"connecting to #{channel} ...";
            try
            {
                using var connection = new TcpClient();
                lock (_gate)
                {
                    _connection = connection;
                }
                connection.Connect(Host, Port);
                using var tls = new SslStream(connection.GetStream());
                tls.AuthenticateAsClient(Host);
                using var reader = new StreamReader(tls, Encoding.UTF8);
                using var writer = new StreamWriter(tls, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
                lock (_gate)
                {
                    _writer = writer;
                }

                writer.WriteLine("CAP REQ :twitch.tv/tags twitch.tv/commands"); // display-name/color + USERNOTICE
                if (useLogin)
                {
                    writer.WriteLine($"PASS oauth:{token}");
                    writer.WriteLine($"NICK {username}");
                }
                else
                {
                    writer.WriteLine($"NICK justinfan{Random.Shared.Next(10000, 99999)}"); // anonymous read-only login
                }

                string? line;
                while (_running && channel == _channel && username == _username && token == _token
                       && (line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("PING", StringComparison.Ordinal))
                    {
                        writer.WriteLine("PONG :tmi.twitch.tv");
                        continue;
                    }
                    if (line.Contains(" 001 ", StringComparison.Ordinal)) // welcome → safe to join
                    {
                        _loggedIn = useLogin;
                        writer.WriteLine($"JOIN #{channel}");
                        var who = useLogin ? $" as {username}" : " (read-only)";
                        Console.WriteLine($"Twitch chat: joined #{channel}{who}");
                        StatusLine = $"joined #{channel}{who}";
                        _joined = true;
                        _incoming.Enqueue(new ChatMessage("sys", "XiloOVR", $"joined #{channel}{who}", null));
                        attempt = 0;
                        continue;
                    }
                    if (IsLoginFailure(line))
                    {
                        // Bad token: remember it so the retry loop does not hammer the login
                        // endpoint; fall back to anonymous reading until credentials change.
                        _authFailed = true;
                        Console.Error.WriteLine("Twitch chat: login failed, check TwitchUsername/TwitchOAuthToken. Falling back to read-only.");
                        _incoming.Enqueue(new ChatMessage("sys", "XiloOVR", "Twitch login failed - reading anonymously", null));
                        break;
                    }
                    if (line.Contains(" RECONNECT", StringComparison.Ordinal) && !line.Contains("PRIVMSG", StringComparison.Ordinal))
                        break; // server-initiated reconnect

                    var message = ParseLine(line);
                    if (message != null)
                        _incoming.Enqueue(message);
                }
            }
            catch (Exception ex)
            {
                if (_running && channel == _channel)
                {
                    Console.Error.WriteLine($"Twitch chat: connection lost ({ex.Message})");
                    StatusLine = $"disconnected from #{channel}, retrying ...";
                }
            }
            finally
            {
                _joined = false;
                _loggedIn = false;
                lock (_gate)
                {
                    _connection = null;
                    _writer = null;
                }
            }

            if (!_running)
                break;
            if (channel != _channel || username != _username || token != _token)
            {
                attempt = 0; // settings changed, reconnect right away
                continue;
            }
            if (_authFailed)
            {
                StatusLine = $"login failed, retrying #{channel} read-only ...";
                continue; // reconnect immediately, this time anonymously
            }
            attempt = Math.Min(attempt + 1, 6);
            Thread.Sleep(TimeSpan.FromSeconds(5 * attempt)); // 5 s .. 30 s backoff
        }
    }

    private static bool IsLoginFailure(string line) =>
        line.Contains(" NOTICE ", StringComparison.Ordinal)
        && (line.Contains("Login authentication failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Improperly formatted auth", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Login unsuccessful", StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses an IRCv3-tagged PRIVMSG or USERNOTICE line into a chat message, or null.</summary>
    private static ChatMessage? ParseLine(string line)
    {
        Dictionary<string, string>? tags = null;
        var rest = line;

        if (rest.StartsWith('@')) // leading message tags
        {
            var space = rest.IndexOf(' ');
            if (space < 0)
                return null;
            tags = ParseTags(rest[1..space]);
            rest = rest[(space + 1)..];
        }

        if (!rest.StartsWith(':'))
            return null;

        if (rest.Contains(" PRIVMSG ", StringComparison.Ordinal))
            return ParsePrivMsg(rest, tags);
        if (rest.Contains(" USERNOTICE ", StringComparison.Ordinal))
            return ParseUserNotice(tags);
        return null;
    }

    private static Dictionary<string, string> ParseTags(string raw)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in raw.Split(';'))
        {
            var eq = tag.IndexOf('=');
            if (eq > 0)
                tags[tag[..eq]] = UnescapeTag(tag[(eq + 1)..]);
        }
        return tags;
    }

    /// <summary>IRCv3 tag values escape special characters: \s space, \: semicolon, \\ backslash.</summary>
    private static string UnescapeTag(string value)
    {
        if (!value.Contains('\\'))
            return value;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                sb.Append(value[i]);
                continue;
            }
            i++;
            sb.Append(value[i] switch
            {
                's' => ' ',
                ':' => ';',
                'r' => '\r',
                'n' => '\n',
                _ => value[i],
            });
        }
        return sb.ToString();
    }

    private static ChatMessage? ParsePrivMsg(string rest, Dictionary<string, string>? tags)
    {
        string? author = null;
        string? colorHex = null;
        if (tags != null)
        {
            tags.TryGetValue("display-name", out author);
            if (tags.TryGetValue("color", out var color) && color.Length > 0)
                colorHex = color;
        }

        if (string.IsNullOrEmpty(author))
        {
            var bang = rest.IndexOf('!');
            author = bang > 1 ? rest[1..bang] : "?";
        }

        var textStart = rest.IndexOf(" :", rest.IndexOf(" PRIVMSG ", StringComparison.Ordinal), StringComparison.Ordinal);
        if (textStart < 0)
            return null;
        var text = rest[(textStart + 2)..];

        // "/me" messages arrive CTCP-wrapped: \u0001ACTION does something\u0001
        const char ctcp = '\u0001';
        if (text.Length > 8 && text[0] == ctcp && text[1..].StartsWith("ACTION ", StringComparison.Ordinal))
            text = text[8..].TrimEnd(ctcp);

        return new ChatMessage("tw", author, text, colorHex);
    }

    /// <summary>Subs, resubs, gift subs and raids arrive as USERNOTICE with a ready-made system-msg.</summary>
    private static ChatMessage? ParseUserNotice(Dictionary<string, string>? tags)
    {
        if (tags == null || !tags.TryGetValue("msg-id", out var msgId))
            return null;
        if (msgId is not ("sub" or "resub" or "subgift" or "submysterygift" or "giftpaidupgrade" or "raid"))
            return null;
        if (!tags.TryGetValue("system-msg", out var systemMsg) || systemMsg.Length == 0)
            return null;

        tags.TryGetValue("display-name", out var author);
        tags.TryGetValue("color", out var color);

        // system-msg already contains the name ("Bob subscribed at Tier 1."); strip the
        // leading name so the feed's author column does not repeat it.
        var text = systemMsg;
        if (!string.IsNullOrEmpty(author) && text.StartsWith(author, StringComparison.OrdinalIgnoreCase))
            text = text[author.Length..].TrimStart();

        return new ChatMessage("tw", string.IsNullOrEmpty(author) ? "Twitch" : author, text,
            string.IsNullOrEmpty(color) ? null : color, IsAlert: true);
    }

    private void CloseConnection()
    {
        lock (_gate)
        {
            try
            {
                _connection?.Close();
            }
            catch
            {
                // closing a dead socket is fine
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        CloseConnection();
        _thread?.Join(1000);
    }
}
