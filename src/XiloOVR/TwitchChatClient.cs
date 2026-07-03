#nullable enable
using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace XiloOVR;

/// <summary>One chat message; Source is a short platform tag ("tw", later "yt", "sys").</summary>
public sealed record ChatMessage(string Source, string Author, string Text, string? ColorHex);

/// <summary>
/// Read-only Twitch chat client over anonymous IRC (justinfan login, no OAuth needed).
/// Runs on a background thread, reconnects with backoff, and hands parsed messages to
/// the UI thread through a queue. Messages carry a source tag so other platforms
/// (YouTube) can merge into the same feed later.
/// </summary>
public sealed class TwitchChatClient : IDisposable
{
    private const string Host = "irc.chat.twitch.tv";
    private const int Port = 6697;

    private readonly ConcurrentQueue<ChatMessage> _incoming = new();
    private readonly object _gate = new();

    private volatile bool _running = true;
    private volatile string _channel = "";
    private TcpClient? _connection;
    private Thread? _thread;

    /// <summary>Human-readable connection state for the settings panel.</summary>
    public string StatusLine { get; private set; } = "off";

    public void Start(string channel)
    {
        _channel = Normalize(channel);
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "twitch-chat" };
        _thread.Start();
    }

    /// <summary>Applies a config change; reconnects when the channel differs.</summary>
    public void SetChannel(string channel)
    {
        var normalized = Normalize(channel);
        if (normalized == _channel)
            return;
        _channel = normalized;
        CloseConnection(); // wakes the thread out of its blocking read
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

    private void RunLoop()
    {
        var attempt = 0;
        while (_running)
        {
            var channel = _channel;
            if (channel.Length == 0)
            {
                StatusLine = "off";
                Thread.Sleep(500); // chat disabled; wait for a config change
                continue;
            }

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

                writer.WriteLine("CAP REQ :twitch.tv/tags"); // gives us display-name + user color
                writer.WriteLine($"NICK justinfan{Random.Shared.Next(10000, 99999)}"); // anonymous read-only login

                string? line;
                while (_running && channel == _channel && (line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("PING", StringComparison.Ordinal))
                    {
                        writer.WriteLine("PONG :tmi.twitch.tv");
                        continue;
                    }
                    if (line.Contains(" 001 ", StringComparison.Ordinal)) // welcome → safe to join
                    {
                        writer.WriteLine($"JOIN #{channel}");
                        Console.WriteLine($"Twitch chat: joined #{channel}");
                        StatusLine = $"joined #{channel}";
                        _incoming.Enqueue(new ChatMessage("sys", "XiloOVR", $"joined #{channel}", null));
                        attempt = 0;
                        continue;
                    }
                    if (line.Contains(" RECONNECT", StringComparison.Ordinal) && !line.Contains("PRIVMSG", StringComparison.Ordinal))
                        break; // server-initiated reconnect

                    var message = ParsePrivMsg(line);
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
                lock (_gate)
                {
                    _connection = null;
                }
            }

            if (!_running)
                break;
            if (channel != _channel)
            {
                attempt = 0; // channel switch requested, reconnect right away
                continue;
            }
            attempt = Math.Min(attempt + 1, 6);
            Thread.Sleep(TimeSpan.FromSeconds(5 * attempt)); // 5 s .. 30 s backoff
        }
    }

    /// <summary>Parses an IRCv3-tagged PRIVMSG line into a chat message, or null.</summary>
    private static ChatMessage? ParsePrivMsg(string line)
    {
        string? displayName = null;
        string? colorHex = null;
        var rest = line;

        if (rest.StartsWith('@')) // leading message tags
        {
            var space = rest.IndexOf(' ');
            if (space < 0)
                return null;
            foreach (var tag in rest[1..space].Split(';'))
            {
                if (tag.StartsWith("display-name=", StringComparison.Ordinal))
                    displayName = tag["display-name=".Length..].Replace(@"\s", " ");
                else if (tag.StartsWith("color=", StringComparison.Ordinal) && tag.Length > "color=".Length)
                    colorHex = tag["color=".Length..];
            }
            rest = rest[(space + 1)..];
        }

        if (!rest.StartsWith(':') || !rest.Contains(" PRIVMSG ", StringComparison.Ordinal))
            return null;

        var author = displayName;
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
