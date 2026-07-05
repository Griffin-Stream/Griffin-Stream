using System.Drawing;
using System.Windows.Forms;

namespace PCRemote.Server;

/// <summary>
/// Optional system-tray presence so the server can run minimized with a quick Exit action and
/// an at-a-glance view of the pairing PIN/port. Enabled via the <c>--tray</c> CLI flag. Runs its
/// own single-threaded-apartment (STA) message loop on a background thread so it never blocks the
/// main server loop, and fails silently if a tray isn't available (e.g. a service session).
/// </summary>
public static class TrayIcon
{
    private static Thread? _thread;
    private static NotifyIcon? _icon;

    public static void Start(string pin, int port, Action onExit)
    {
        if (_thread != null) return;

        _thread = new Thread(() =>
        {
            try
            {
                _icon = new NotifyIcon
                {
                    Icon = SystemIcons.Application,
                    Visible = true,
                    Text = "Griffin Stream Server"
                };

                var menu = new ContextMenuStrip();
                menu.Items.Add(new ToolStripMenuItem($"Pairing PIN: {pin}") { Enabled = false });
                menu.Items.Add(new ToolStripMenuItem($"Port: {port}") { Enabled = false });
                menu.Items.Add(new ToolStripSeparator());
                var exit = new ToolStripMenuItem("Exit");
                exit.Click += (_, _) => onExit();
                menu.Items.Add(exit);
                _icon.ContextMenuStrip = menu;

                _icon.ShowBalloonTip(3000, "Griffin Stream", $"Server running. Pairing PIN: {pin}", ToolTipIcon.Info);

                Application.Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Tray] Unavailable: {ex.Message}");
            }
        });
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
    }

    public static void Stop()
    {
        try
        {
            if (_icon != null)
            {
                _icon.Visible = false;
                _icon.Dispose();
                _icon = null;
            }
        }
        catch { /* best effort */ }
    }
}
