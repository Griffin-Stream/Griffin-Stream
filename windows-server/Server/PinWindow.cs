using System.Drawing;
using System.Windows.Forms;
using PCRemote.Server.Security;

namespace PCRemote.Server;

/// <summary>
/// A small, always-on-top window that shows the connection address and pairing PIN in large,
/// unmistakable text the moment the server starts. This is the primary way a non-technical user
/// finds the PIN — far easier than hunting for it in the scrolling console. Closing the window does
/// NOT stop the server; it keeps running in the background (and the PIN is still available in the
/// console and, when enabled, the system tray). Runs its own single-threaded-apartment (STA)
/// message loop on a background thread and fails silently if no desktop is available.
/// </summary>
public static class PinWindow
{
    private static Thread? _thread;
    private static Form? _form;
    private static Label? _addressLabel;

    // Brand palette (kept in sync with the Android app's dark theme).
    private static readonly Color Background = Color.FromArgb(18, 20, 24);
    private static readonly Color PanelColor = Color.FromArgb(28, 31, 38);
    private static readonly Color Gold = Color.FromArgb(255, 199, 74);
    private static readonly Color Green = Color.FromArgb(74, 222, 128);
    private static readonly Color TextPrimary = Color.White;
    private static readonly Color TextSecondary = Color.FromArgb(160, 165, 175);

    public static void Start(SecurityManager security, int port)
    {
        if (_thread != null) return;

        _thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                _form = BuildForm(security.PairingPin, port);
                Application.Run(_form);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PinWindow] Unavailable: {ex.Message}");
            }
        });
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
    }

    /// <summary>Update the displayed connection address once the local IP has been resolved.</summary>
    public static void SetLocalIp(string? localIp, int port)
    {
        var form = _form;
        var label = _addressLabel;
        if (form == null || label == null || form.IsDisposed) return;
        try
        {
            void Apply() => label.Text = localIp != null ? $"{localIp} : {port}" : $"(this PC's IP) : {port}";
            if (form.InvokeRequired) form.BeginInvoke((Action)Apply);
            else Apply();
        }
        catch { /* window closed; ignore */ }
    }

    public static void Stop()
    {
        try
        {
            var form = _form;
            if (form != null && !form.IsDisposed)
            {
                if (form.InvokeRequired) form.BeginInvoke((Action)(() => form.Close()));
                else form.Close();
            }
        }
        catch { /* best effort */ }
    }

    private static Form BuildForm(string pin, int port)
    {
        var form = new Form
        {
            Text = "Griffin Stream Server",
            BackColor = Background,
            ForeColor = TextPrimary,
            FormBorderStyle = FormBorderStyle.FixedSingle,
            MaximizeBox = false,
            MinimizeBox = true,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(440, 440),
            TopMost = true,
            ShowInTaskbar = true,
            Padding = new Padding(24)
        };

        var root = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = false,
            BackColor = Background
        };

        root.Controls.Add(MakeLabel("Griffin Stream Server", 15f, FontStyle.Bold, Gold, new Padding(0, 0, 0, 2)));
        root.Controls.Add(MakeLabel("Running — your PC is ready to connect", 9.5f, FontStyle.Regular, Green, new Padding(0, 0, 0, 16)));

        _addressLabel = new Label
        {
            Text = $"detecting IP… : {port}",
            Font = new Font("Consolas", 15f, FontStyle.Bold),
            ForeColor = TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 0)
        };
        root.Controls.Add(MakeCard("CONNECT TO THIS ADDRESS", _addressLabel));

        var pinLabel = new Label
        {
            Text = pin,
            Font = new Font("Consolas", 38f, FontStyle.Bold),
            ForeColor = Gold,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 6)
        };
        var copyButton = new Button
        {
            Text = "Copy PIN",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            ForeColor = TextPrimary,
            BackColor = PanelColor,
            Font = new Font("Segoe UI", 9f),
            Cursor = Cursors.Hand,
            Padding = new Padding(10, 4, 10, 4),
            Margin = new Padding(0)
        };
        copyButton.FlatAppearance.BorderColor = Gold;
        copyButton.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(pin);
                copyButton.Text = "Copied!";
                var reset = new System.Windows.Forms.Timer { Interval = 1500 };
                reset.Tick += (_, _) => { copyButton.Text = "Copy PIN"; reset.Stop(); reset.Dispose(); };
                reset.Start();
            }
            catch { /* clipboard busy; ignore */ }
        };
        root.Controls.Add(MakeCard("PAIRING PIN", pinLabel, copyButton));

        root.Controls.Add(new Label
        {
            Text = "In the Griffin Stream app, tap Scan (or type the address above), then enter this " +
                   "PIN to pair. You only need the PIN the first time you add a device.",
            Font = new Font("Segoe UI", 9f),
            ForeColor = TextSecondary,
            AutoSize = false,
            Width = 392,
            Height = 52,
            Margin = new Padding(0, 12, 0, 0)
        });

        root.Controls.Add(new Label
        {
            Text = "You can close this window — the server keeps running in the background.",
            Font = new Font("Segoe UI", 8f, FontStyle.Italic),
            ForeColor = TextSecondary,
            AutoSize = false,
            Width = 392,
            Height = 20,
            Margin = new Padding(0, 4, 0, 0)
        });

        form.Controls.Add(root);
        return form;
    }

    private static Label MakeLabel(string text, float size, FontStyle style, Color color, Padding margin) => new()
    {
        Text = text,
        Font = new Font("Segoe UI", size, style),
        ForeColor = color,
        AutoSize = true,
        Margin = margin
    };

    /// <summary>A dark panel with a small uppercase green caption above the supplied content controls.</summary>
    private static Panel MakeCard(string caption, params Control[] content)
    {
        var inner = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = PanelColor,
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };
        inner.Controls.Add(new Label
        {
            Text = caption,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            ForeColor = Green,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2)
        });
        foreach (var c in content) inner.Controls.Add(c);

        var card = new Panel
        {
            BackColor = PanelColor,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 10, 14, 12),
            Margin = new Padding(0, 0, 0, 12),
            MinimumSize = new Size(392, 0)
        };
        card.Controls.Add(inner);
        return card;
    }
}
