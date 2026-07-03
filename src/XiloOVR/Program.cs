#nullable enable
using System.Diagnostics;
using Valve.VR;

namespace XiloOVR;

internal static class Program
{
    private static volatile bool _running = true;
    private static volatile bool _configChanged;
    private static long _suppressConfigReloadUntilTicks;

    private static int Main(string[] args)
    {
        SetupLogging();
        Console.WriteLine($"XiloOVR v{Version()} - SteamVR overlay, started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This application targets the Windows SteamVR runtime and cannot run on this OS.");
            return 2;
        }

        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        AppConfig config;
        try
        {
            config = ConfigLoader.LoadOrCreate(configPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load config '{configPath}': {ex.Message}");
            System.Windows.Forms.MessageBox.Show($"Failed to load config.json:\n{ex.Message}", "XiloOVR",
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            return 2;
        }

        // One-off override without touching the file: XiloOVR.exe --hand left
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--hand")
                config.Hand = args[i + 1];
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // let the main loop shut OpenVR down cleanly
            _running = false;
        };

        // Windowless app: refuse to run twice (two overlays with one key would fight).
        using var instanceLock = new Mutex(true, @"Local\XiloOVR-single-instance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            System.Windows.Forms.MessageBox.Show("XiloOVR is already running - look for the XO icon in the system tray.",
                "XiloOVR", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
            return 3;
        }

        using var tray = new TrayIcon($"XiloOVR v{Version()}", () => _running = false);

        using var overlay = new OverlayManager();
        try
        {
            overlay.Initialize(config);
        }
        catch (VRInitException ex)
        {
            Console.Error.WriteLine(ex.Message);
            System.Windows.Forms.MessageBox.Show(ex.Message, "XiloOVR",
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            return 1;
        }

        using var checklist = ChecklistData.LoadOrCreate(
            Path.Combine(AppContext.BaseDirectory, "checklist.json"),
            Path.Combine(AppContext.BaseDirectory, "data", "items_database.json"));

        var wrist = new WristAttachment(overlay, config);
        var input = new InputManager();
        input.Initialize();

        using var chat = new TwitchChatClient();
        chat.Start(config.TwitchChannel);
        if (!config.IsChatEnabled)
            Console.WriteLine("Twitch chat disabled (set the channel in the dashboard settings tab or config.json).");

        var ui = new ChecklistUI(overlay, config, checklist, chat);

        // The dashboard settings tab edits the same AppConfig instance and calls back here.
        void ApplyAndSave()
        {
            OpenVR.Overlay.SetOverlayWidthInMeters(overlay.Handle, config.WidthMeters);
            wrist.Reconfigure();
            ui.MarkDirty();
            chat.SetChannel(config.TwitchChannel);
            // Our own write must not bounce back through the file watcher (it would blink the panel).
            Volatile.Write(ref _suppressConfigReloadUntilTicks, DateTime.UtcNow.AddSeconds(1.5).Ticks);
            ConfigLoader.Save(config, configPath);
        }

        using var settings = new SettingsUI(config, chat, ApplyAndSave);

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
                ReloadConfig(configPath, config, overlay, wrist, ui);
                chat.SetChannel(config.TwitchChannel);
                settings.MarkDirty();
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

            ui.Update(deltaMs, wrist.Present, pointerDevice, incrementClicked, decrementClicked);
            settings.Update();

            Thread.Sleep(ui.PanelShown ? 20 : 100);
        }

        Console.WriteLine("Clean shutdown.");
        return 0;
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
            Console.SetOut(new TeeWriter(Console.Out, writer));
            Console.SetError(new TeeWriter(Console.Error, writer));
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

    private static void ReloadConfig(string path, AppConfig config, OverlayManager overlay, WristAttachment wrist, ChecklistUI ui)
    {
        try
        {
            var fresh = ConfigLoader.LoadOrCreate(path);
            config.CopyFrom(fresh);
            OpenVR.Overlay.SetOverlayWidthInMeters(overlay.Handle, config.WidthMeters);
            wrist.Reconfigure();
            ui.MarkDirty();
            Console.WriteLine("config.json reloaded, panel offset/size applied live.");
        }
        catch (Exception ex)
        {
            // Likely caught the editor mid-write; keep current settings, the next event retries.
            Console.Error.WriteLine($"warning: config reload failed, keeping previous settings: {ex.Message}");
        }
    }
}
