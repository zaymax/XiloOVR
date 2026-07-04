#nullable enable
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace XiloOVR;

/// <summary>
/// System tray icon with a small menu. The app is windowless (no console, no windows),
/// so this is its only desktop-side handle: open the data folder or the log, or quit.
/// Runs its own STA message-loop thread; Quit reports back through the callback.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Thread _thread;
    private NotifyIcon? _icon;

    public TrayIcon(string title, Color accent, Action onQuit)
    {
        _thread = new Thread(() => Run(title, accent, onQuit)) { IsBackground = true, Name = "tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run(string title, Color accent, Action onQuit)
    {
        Icon trayIcon;
        using (var bitmap = new Bitmap(32, 32))
        {
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.FromArgb(255, 16, 20, 28));
                using var pen = new Pen(accent, 3);
                g.DrawRectangle(pen, 1, 1, 29, 29);
                using var font = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(accent);
                g.DrawString("XO", font, brush, 4, 9);
            }
            trayIcon = Icon.FromHandle(bitmap.GetHicon());
        }

        var menu = new ContextMenuStrip();
        menu.Items.Add(title).Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open data folder", null, (_, _) => OpenPath(AppContext.BaseDirectory));
        menu.Items.Add("Open log", null, (_, _) => OpenPath(Path.Combine(AppContext.BaseDirectory, "xiloovr.log")));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => onQuit());

        _icon = new NotifyIcon
        {
            Icon = trayIcon,
            Text = title,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenPath(AppContext.BaseDirectory);

        Application.Run();

        _icon.Visible = false;
        _icon.Dispose();
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: could not open '{path}': {ex.Message}");
        }
    }

    public void Dispose()
    {
        Application.Exit(); // ends the tray thread's message loop from any thread
        _thread.Join(1500);
    }
}
