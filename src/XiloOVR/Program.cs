#nullable enable
using System.Diagnostics;
using Valve.VR;

namespace XiloOVR;

internal static class Program
{
    private static volatile bool _running = true;
    private static volatile bool _configChanged;

    private static int Main(string[] args)
    {
        Console.WriteLine("XiloOVR - SteamVR overlay");

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

        using var overlay = new OverlayManager();
        try
        {
            overlay.Initialize(config);
        }
        catch (VRInitException ex)
        {
            Console.Error.WriteLine(ex.Message);
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
            Console.WriteLine("Twitch chat disabled (set \"TwitchChannel\" in config.json to enable).");

        var ui = new ChecklistUI(overlay, config, checklist, chat);

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
        watcher.Changed += (_, _) => _configChanged = true;
        watcher.Created += (_, _) => _configChanged = true;
        watcher.Renamed += (_, _) => _configChanged = true;
        return watcher;
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
