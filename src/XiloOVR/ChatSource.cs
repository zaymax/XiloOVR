#nullable enable
using System.Collections.Concurrent;

namespace XiloOVR;

/// <summary>
/// One chat message; Source is a short platform tag ("tw", "yt", "sys").
/// IsAlert marks follow/sub/raid/superchat events that get banner treatment.
/// </summary>
public sealed record ChatMessage(string Source, string Author, string Text, string? ColorHex, bool IsAlert = false);

/// <summary>Anything that produces chat messages for the panel feed.</summary>
public interface IChatSource
{
    /// <summary>Short display name for status lines ("Twitch", "YouTube", "Follows").</summary>
    string Name { get; }

    /// <summary>Human-readable connection state; "off" while the source is unconfigured.</summary>
    string StatusLine { get; }

    void Start(AppConfig config);

    /// <summary>Applies a config change; the source reconnects when its settings differ.</summary>
    void Configure(AppConfig config);

    /// <summary>Moves queued messages into the list; returns true when anything arrived.</summary>
    bool TryDrain(List<ChatMessage> into);
}

/// <summary>
/// Shared scaffolding of every chat source: the background thread, the message queue
/// drained by the UI thread, config-change plumbing and an interruptible sleep. A
/// subclass implements RunLoop and stores its own normalized settings in ApplyConfig.
/// </summary>
public abstract class ChatSourceBase : IChatSource, IDisposable
{
    private readonly ConcurrentQueue<ChatMessage> _incoming = new();
    private volatile bool _running = true;
    private Thread? _thread;

    public abstract string Name { get; }

    public string StatusLine { get; protected set; } = "off";

    /// <summary>False once Dispose ran; every wait and read loop must watch it.</summary>
    protected bool Running => _running;

    protected abstract string ThreadName { get; }

    public void Start(AppConfig config)
    {
        ApplyConfig(config);
        _thread = new Thread(RunLoop) { IsBackground = true, Name = ThreadName };
        _thread.Start();
    }

    public void Configure(AppConfig config)
    {
        if (ApplyConfig(config))
            OnReconfigured();
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

    protected void Enqueue(ChatMessage message) => _incoming.Enqueue(message);

    /// <summary>
    /// Sleeps in small slices so shutdown and config changes apply quickly; an optional
    /// condition (usually "my settings snapshot still matches") ends the wait early.
    /// </summary>
    protected void SleepInterruptible(int totalMs, Func<bool>? keepWaiting = null)
    {
        for (var waited = 0; waited < totalMs && _running && (keepWaiting?.Invoke() ?? true); waited += 250)
            Thread.Sleep(250);
    }

    /// <summary>Stores the source's settings from config; returns true when they changed.</summary>
    protected abstract bool ApplyConfig(AppConfig config);

    /// <summary>Runs on the source's background thread until Running turns false.</summary>
    protected abstract void RunLoop();

    /// <summary>Called after ApplyConfig reported a change (e.g. drop the connection).</summary>
    protected virtual void OnReconfigured()
    {
    }

    /// <summary>Called at the start of Dispose, before joining the thread.</summary>
    protected virtual void OnDisposing()
    {
    }

    public void Dispose()
    {
        _running = false;
        OnDisposing();
        _thread?.Join(1000);
    }
}
