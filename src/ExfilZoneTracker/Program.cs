#nullable enable

namespace ExfilZoneTracker;

internal static class Program
{
    private static volatile bool _running = true;

    private static int Main(string[] args)
    {
        Console.WriteLine("ExfilZone Wrist Tracker - SteamVR overlay prototype");

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

        // One-off override without touching the file: ExfilZoneTracker.exe --hand left
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

        var wrist = new WristAttachment(overlay, config);
        Console.WriteLine($"Overlay ready. Looking for the {config.HandNormalized} controller... (Ctrl+C to quit)");

        while (_running)
        {
            if (!overlay.PumpEvents(out var devicesChanged))
                break; // SteamVR is shutting down

            wrist.Update(devicesChanged);

            // The overlay transform is device-relative: the compositor keeps it glued to the
            // controller with no per-frame work on our side, so a slow poll loop is enough.
            Thread.Sleep(50);
        }

        Console.WriteLine("Clean shutdown.");
        return 0;
    }
}
