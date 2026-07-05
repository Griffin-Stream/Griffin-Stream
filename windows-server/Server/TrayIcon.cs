using System.Drawing;
using System.Windows.Forms;
using PCRemote.Server.Security;

namespace PCRemote.Server;

/// <summary>
/// Optional system-tray presence so the server can run minimized with a quick Exit action, an
/// at-a-glance view of the pairing PIN/port, and a "Paired devices" submenu for reviewing and
/// revoking (unpairing) enrolled devices. Enabled via the <c>--tray</c> CLI flag. Runs its own
/// single-threaded-apartment (STA) message loop on a background thread so it never blocks the main
/// server loop, and fails silently if a tray isn't available (e.g. a service session).
/// </summary>
public static class TrayIcon
{
    private static Thread? _thread;
    private static NotifyIcon? _icon;

    public static void Start(SecurityManager security, int port, Action onExit)
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
                // Rebuild the menu each time it opens so the paired-devices list is always current.
                menu.Opening += (_, _) => BuildMenu(menu, security, port, onExit);
                BuildMenu(menu, security, port, onExit);
                _icon.ContextMenuStrip = menu;

                _icon.ShowBalloonTip(3000, "Griffin Stream",
                    $"Server running. Pairing PIN: {security.PairingPin}", ToolTipIcon.Info);

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

    private static void BuildMenu(ContextMenuStrip menu, SecurityManager security, int port, Action onExit)
    {
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem($"Pairing PIN: {security.PairingPin}") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem($"Port: {port}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        var devices = security.ListDevices();
        var pairedHeader = new ToolStripMenuItem(
            devices.Count == 0 ? "Paired devices: none" : "Paired devices") { Enabled = false };
        menu.Items.Add(pairedHeader);

        for (int i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            var index = i;
            var item = new ToolStripMenuItem($"{device.Label}  (last seen {device.LastSeenUtc.ToLocalTime():g})");
            var remove = new ToolStripMenuItem("Unpair / remove");
            remove.Click += (_, _) =>
            {
                var confirm = MessageBox.Show(
                    $"Remove paired device \"{device.Label}\"?\nIt will need to pair again to reconnect.",
                    "Griffin Stream - Unpair device",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (confirm == DialogResult.Yes)
                {
                    security.RemoveDeviceByIndex(index);
                    _icon?.ShowBalloonTip(2000, "Griffin Stream",
                        $"Removed \"{device.Label}\".", ToolTipIcon.Info);
                }
            };
            item.DropDownItems.Add(remove);
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => onExit();
        menu.Items.Add(exit);
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
