#nullable enable
using System.Diagnostics;
using Valve.VR;

namespace XiloOVR;

internal static class Program
{
    private static volatile bool _running = true;
    private static volatile bool _configChanged;
    private static long _suppressConfigReloadUntilTicks;

    /// <summary>Hand given on the command line; survives config hot-reloads for this run.</summary>
    private static string? _handOverride;

    private static int Main(string[] args)
    {
        // Windowless app: refuse to run twice (two overlays with one key would fight).
        // Checked before anything else so the second instance never touches the first
        // one's log file.
        using var instanceLock = new Mutex(true, @"Local\XiloOVR-single-instance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            ShowError("XiloOVR is already running - look for the XO icon in the system tray.",
                System.Windows.Forms.MessageBoxIcon.Information);
            return 3;
        }

        SetupLogging();
        Console.WriteLine($"XiloOVR v{Version()} - SteamVR overlay, started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This application targets the Windows SteamVR runtime and cannot run on this OS.");
            return 2;
        }

        // A WinExe has no console: an unhandled exception would kill the process without
        // a trace. Background threads (chat clients, file watchers) report here ...
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Console.Error.WriteLine($"fatal: unhandled exception: {e.ExceptionObject}");

        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            // ... and the main loop reports here, with a dialog the user can actually see.
            Console.Error.WriteLine($"fatal: {ex}");
            ShowError($"XiloOVR stopped because of an unexpected error:\n{ex.Message}\n\nDetails are in xiloovr.log next to the exe.");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        AppConfig config;
        try
        {
            config = ConfigLoader.LoadOrCreate(configPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load config '{configPath}': {ex.Message}");
            ShowError($"Failed to load config.json:\n{ex.Message}");
            return 2;
        }

        // One-off override without touching the file: XiloOVR.exe --hand left
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--hand")
                _handOverride = args[i + 1];
        }
        if (_handOverride != null)
            config.Hand = _handOverride;

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // let the main loop shut OpenVR down cleanly
            _running = false;
        };

        using var tray = new TrayIcon($"XiloOVR v{Version()}", Theme.Accent(config), () => _running = false);

        using var overlay = new OverlayManager();
        try
        {
            overlay.Initialize(config);
        }
        catch (VRInitException ex)
        {
            Console.Error.WriteLine(ex.Message);
            ShowError(ex.Message);
            return 1;
        }

        using var checklist = ChecklistData.LoadOrCreate(
            Path.Combine(AppContext.BaseDirectory, "checklist.json"),
            Path.Combine(AppContext.BaseDirectory, "data", "items_database.json"));

        var wrist = new WristAttachment(overlay, config);
        var input = new InputManager();
        input.Initialize();
        AutostartManager.Apply(config.AutostartWithSteamVR);

        using var chat = new TwitchChatClient();
        chat.Start(config);
        using var youtube = new YouTubeChatClient();
        youtube.Start(config.YouTubeChannel);
        using var follows = new TwitchFollowPoller();
        follows.Start(config);
        if (!config.IsChatEnabled)
            Console.WriteLine("Chat disabled (set a Twitch or YouTube channel in the dashboard settings tab or config.json).");

        using var laser = new LaserBeam();
        var chatSources = new IChatSource[] { chat, youtube, follows };
        var ui = new ChecklistUI(overlay, config, checklist, chat, chatSources, laser);

        // Everything that has to happen when settings change, whether the dashboard tab
        // edited the shared AppConfig instance or config.json was reloaded from disk.
        void ApplyConfig()
        {
            OpenVR.Overlay.SetOverlayWidthInMeters(overlay.Handle, config.WidthMeters);
            wrist.Reconfigure();
            ui.MarkDirty();
            chat.Configure(config);
            youtube.SetChannel(config.YouTubeChannel);
            follows.Configure(config);
            AutostartManager.Apply(config.AutostartWithSteamVR);
        }

        void ApplyAndSave()
        {
            ApplyConfig();
            // Our own write must not bounce back through the file watcher (it would blink the panel).
            Volatile.Write(ref _suppressConfigReloadUntilTicks, DateTime.UtcNow.AddSeconds(1.5).Ticks);
            ConfigLoader.Save(config, configPath);
        }

        using var settings = new SettingsUI(config, chat, youtube, follows, ApplyAndSave);

        using var configWatcher = WatchConfig(configPath);

        Console.WriteLine($"Overlay ready. Watch hand: {config.HandNormalized}, pointer hand: {(config.IsLeftHand ? "right" : "left")}. (Ctrl+C to quit)");

        var clock = Stopwatch.StartNew();
        var lastMs = clock.Elapsed.TotalMilliseconds;
        while (_running)
        {
            if (!overlay.PumpEvents(out var devicesChanged, out var sceneAppChanged))
                break; // SteamVR is shutting down

            if (_configChanged)
            {
                _configChanged = false;
                if (ReloadConfig(configPath, config))
                {
                    ApplyConfig();
                    settings.MarkDirty();
                }
            }

            var now = clock.Elapsed.TotalMilliseconds;
            var deltaMs = now - lastMs;
            lastMs = now;

            wrist.Update(devicesChanged);
            if (sceneAppChanged)
            {
                wrist.ReassertTransform();
                ui.MarkDirty();
            }
            input.Update();

            if (input.PollToggleLongPress(config.ToggleHoldMs))
                ui.ToggleVisibility();

            // The free hand points at the panel; the watch hand carries it.
            var pointerRole = config.IsLeftHand ? ETrackedControllerRole.RightHand : ETrackedControllerRole.LeftHand;
            var pointerDevice = overlay.System.GetTrackedDeviceIndexForControllerRole(pointerRole);
            var incrementClicked = input.PollInteractClick(leftHand: !config.IsLeftHand);
            var decrementClicked = input.PollDecrementClick(leftHand: !config.IsLeftHand);

            ui.Update(deltaMs, wrist.DeviceIndex, wrist.Offset, pointerDevice, incrementClicked, decrementClicked);
            settings.Update();

            // Full rate while something interactive is on screen (the wrist panel, or the
            // dashboard tab with its laser hover); a quicker idle tick while the wrist
            // gesture is armed so a glance registers promptly; otherwise mostly asleep.
            Thread.Sleep(ui.PanelShown || settings.Visible ? 20 : config.ShowOnWristTurn ? 50 : 100);
        }

        Console.WriteLine("Clean shutdown.");
        return 0;
    }

    private static void ShowError(string message,
        System.Windows.Forms.MessageBoxIcon icon = System.Windows.Forms.MessageBoxIcon.Error)
    {
        System.Windows.Forms.MessageBox.Show(message, "XiloOVR", System.Windows.Forms.MessageBoxButtons.OK, icon);
    }

    private static FileSystemWatcher? WatchConfig(string configPath)
    {
        var directory = Path.GetDirectoryName(configPath);
        if (string.IsNullOrEmpty(directory))
            return null;

        var watcher = new FileSystemWatcher(directory, Path.GetFileName(configPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        watcher.Changed += (_, _) => OnConfigFileChanged();
        watcher.Created += (_, _) => OnConfigFileChanged();
        watcher.Renamed += (_, _) => OnConfigFileChanged();
        return watcher;
    }

    private static void OnConfigFileChanged()
    {
        // Saves made by the settings tab are already applied; reloading them would blink the panel.
        if (DateTime.UtcNow.Ticks < Volatile.Read(ref _suppressConfigReloadUntilTicks))
            return;
        _configChanged = true;
    }

    private static string Version()
    {
        var version = typeof(Program).Assembly.GetName().Version;
        return $"{version?.Major}.{version?.Minor}.{version?.Build}";
    }

    /// <summary>
    /// The app builds as WinExe (no console), so all Console output is mirrored into a
    /// per-session log file next to the exe - reachable from the tray menu.
    /// </summary>
    private static void SetupLogging()
    {
        try
        {
            var writer = new StreamWriter(Path.Combine(AppContext.BaseDirectory, "xiloovr.log"), append: false)
            {
                AutoFlush = true,
            };
            // Console.SetOut/SetError each synchronize their own writer, but both tees end
            // in this one StreamWriter, and chat threads log to Error while the main thread
            // logs to Out - so the shared sink needs its own lock.
            var log = TextWriter.Synchronized(writer);
            Console.SetOut(new TeeWriter(Console.Out, log));
            Console.SetError(new TeeWriter(Console.Error, log));
        }
        catch
        {
            // read-only folder: keep whatever output channel exists
        }
    }

    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _original;
        private readonly TextWriter _log;

        public TeeWriter(TextWriter original, TextWriter log)
        {
            _original = original;
            _log = log;
        }

        public override System.Text.Encoding Encoding => _log.Encoding;

        public override void Write(char value)
        {
            _original.Write(value);
            _log.Write(value);
        }

        public override void Write(string? value)
        {
            _original.Write(value);
            _log.Write(value);
        }

        public override void WriteLine(string? value)
        {
            _original.WriteLine(value);
            _log.WriteLine($"[{DateTime.Now:HH:mm:ss}] {value}");
        }
    }

    /// <summary>Re-reads config.json onto the shared instance; false when the file could not be parsed.</summary>
    private static bool ReloadConfig(string path, AppConfig config)
    {
        try
        {
            var fresh = ConfigLoader.LoadOrCreate(path);
            config.CopyFrom(fresh);
            if (_handOverride != null)
                config.Hand = _handOverride;
            Console.WriteLine("config.json reloaded, panel offset/size applied live.");
            return true;
        }
        catch (Exception ex)
        {
            // Likely caught the editor mid-write; keep current settings, the next event retries.
            Console.Error.WriteLine($"warning: config reload failed, keeping previous settings: {ex.Message}");
            return false;
        }
    }
}
