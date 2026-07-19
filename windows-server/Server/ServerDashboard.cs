using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PCRemote.Server.Security;
using PCRemote.Server.Licensing;
using PCRemote.Server.Logging;
using PCRemote.Server.Update;

namespace PCRemote.Server;

/// <summary>
/// The Griffin Stream Server desktop dashboard: a borderless, custom-chrome window styled to match
/// the Android app (deep circuit-black background with a neon-green glow, gold accents, rounded
/// glowing cards). It shows the server status, connection address, pairing PIN, Pro tier, and hosts
/// the debug-log toggle and update actions. Closing the window stops the server; minimizing keeps it
/// running in the background. Runs its own STA message loop on a background thread.
/// </summary>
public static class ServerDashboard
{
    private static Thread? _thread;
    private static DashboardForm? _form;
    private static Action? _onClose;
    private static Label? _addressLabel;

    private static Pill? _tierBadge;
    private static Label? _licenseTitle;
    private static Label? _licenseBody;
    private static TextBox? _activateInput;
    private static Button? _activateButton;
    private static Panel? _activateRow;

    private static Button? _debugButton;
    private static Button? _updateButton;
    private static Label? _versionLabel;

    // Paragraph labels whose wrap width tracks the (resizable) window width.
    private static Label? _instructions;
    private static Label? _closeNote;

    // ── Brand palette (exact match to the Android app theme, Color.kt) ──
    private static readonly Color Bg          = Color.FromArgb(0x0A, 0x0A, 0x0C);
    private static readonly Color Surface     = Color.FromArgb(0x12, 0x12, 0x1A);
    private static readonly Color Card        = Color.FromArgb(0x16, 0x16, 0x20);
    private static readonly Color CardBorder  = Color.FromArgb(0x2A, 0x2A, 0x36);
    private static readonly Color Green       = Color.FromArgb(0x00, 0xFF, 0x88);
    private static readonly Color GreenDark   = Color.FromArgb(0x00, 0xCC, 0x6A);
    private static readonly Color Gold        = Color.FromArgb(0xFF, 0xB7, 0x00);
    private static readonly Color TextPrimary = Color.FromArgb(0xF0, 0xF0, 0xF2);
    private static readonly Color TextSecondary = Color.FromArgb(0xB0, 0xB0, 0xB8);
    private static readonly Color TextMuted   = Color.FromArgb(0x70, 0x70, 0x78);
    private static readonly Color Danger      = Color.FromArgb(0xFF, 0x66, 0x66);

    private const int FormWidth = 460;
    private const int TitleBarHeight = 40;
    private const int SidePad = 24;
    private const int ContentWidth = FormWidth - (SidePad * 2);
    private const int CardInnerWidth = ContentWidth - 40;
    private const string PlaceholderKey = "Paste license key…";

    public static void Start(SecurityManager security, int port, Action? onClose = null)
    {
        if (_thread != null) return;
        _onClose = onClose;

        _thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                _form = BuildForm(security.PairingPin, port);
                Application.Run(_form);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ServerDashboard] Unavailable: {ex.Message}");
            }
        });
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
    }

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

    private static DashboardForm BuildForm(string pin, int port)
    {
        var version = Updater.DisplayVersion;
        var form = new DashboardForm
        {
            Text = $"Griffin Stream Server {version}",
            BackColor = Bg,
            ForeColor = TextPrimary,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            TopMost = true,
            ShowInTaskbar = true,
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = new Font("Segoe UI", 9f),
            ClientSize = new Size(FormWidth, 700)
        };
        TryLoadIcon(form);

        form.Controls.Add(BuildContent(pin, port)); // Dock=Fill (added first so it sits under the bar)
        form.Controls.Add(BuildTitleBar(form));      // Dock=Top

        // Size to content (no scrollbar) once layout is known, then center + round the corners.
        form.Load += (_, _) =>
        {
            var content = form.Controls.OfType<TableLayoutPanel>().FirstOrDefault();
            int contentHeight = content?.PreferredSize.Height ?? 600;
            form.ClientSize = new Size(FormWidth, TitleBarHeight + contentHeight);
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            form.Location = new Point(wa.X + (wa.Width - form.Width) / 2, wa.Y + (wa.Height - form.Height) / 2);
            form.ApplyRoundedCorners();
        };

        form.FormClosed += (_, _) => { try { _onClose?.Invoke(); } catch { } };

        UpdateTierUi();
        LicenseManager.TierChanged += _ => UpdateTierUi();
        if (Updater.Available != null) ReflectUpdate(Updater.Available);
        Updater.UpdateFound += info => ReflectUpdate(info);

        return form;
    }

    // ── Title bar (custom chrome) ────────────────────────────

    private static Control BuildTitleBar(DashboardForm form)
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = TitleBarHeight, BackColor = Color.Transparent };

        var closeBtn = MakeChromeButton("\u2715", Color.FromArgb(232, 17, 35));
        closeBtn.Dock = DockStyle.Right;
        closeBtn.Click += (_, _) => form.Close();

        var minBtn = MakeChromeButton("\u2013", CardBorder);
        minBtn.Dock = DockStyle.Right;
        minBtn.Click += (_, _) => form.WindowState = FormWindowState.Minimized;

        var titleArea = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var wordmark = new Label
        {
            Text = "GRIFFIN STREAM",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = TextMuted,
            AutoSize = true,
            Location = new Point(SidePad, 13)
        };
        titleArea.Controls.Add(wordmark);

        // Drag the window by the title bar (borderless move trick).
        void StartDrag(object? s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(form.Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
        }
        bar.MouseDown += StartDrag;
        titleArea.MouseDown += StartDrag;
        wordmark.MouseDown += StartDrag;

        bar.Controls.Add(titleArea);
        bar.Controls.Add(minBtn);
        bar.Controls.Add(closeBtn);
        return bar;
    }

    // ── Content ──────────────────────────────────────────────

    private static Control BuildContent(string pin, int port)
    {
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(SidePad, 4, SidePad, 20),
            Margin = new Padding(0)
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        stack.Controls.Add(BuildHero());
        stack.Controls.Add(BuildAddressCard(port));
        stack.Controls.Add(BuildPinCard(pin));
        stack.Controls.Add(BuildLicenseCard());

        _instructions = new Label
        {
            Text = "In the Griffin Stream app, tap Scan (or type the address above), then enter this " +
                   "PIN to pair. You only need the PIN the first time you add a device.",
            Font = new Font("Segoe UI", 9f),
            ForeColor = TextSecondary,
            AutoSize = true,
            MaximumSize = new Size(ContentWidth, 0),
            BackColor = Color.Transparent,
            Margin = new Padding(2, 14, 2, 0)
        };
        stack.Controls.Add(_instructions);

        stack.Controls.Add(BuildFooter());

        _closeNote = new Label
        {
            Text = "Closing this window stops the server. Minimize (\u2013) to keep it running.",
            Font = new Font("Segoe UI", 8.25f, FontStyle.Italic),
            ForeColor = TextMuted,
            AutoSize = true,
            MaximumSize = new Size(ContentWidth, 0),
            BackColor = Color.Transparent,
            Margin = new Padding(2, 12, 2, 0)
        };
        stack.Controls.Add(_closeNote);

        return stack;
    }

    private static Control BuildHero()
    {
        var hero = new GradientPanel
        {
            Dock = DockStyle.Fill,
            Height = 96,
            TopColor = Color.FromArgb(0x0E, 0x2A, 0x1E),
            BottomColor = Surface,
            BorderColor = Color.FromArgb(0x1E, 0x4D, 0x38),
            AccentColor = Green,
            Radius = 16,
            Margin = new Padding(0, 6, 0, 14),
            Padding = new Padding(16, 0, 16, 0),
            BackColor = Color.Transparent
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        grid.Controls.Add(new LogoBadge
        {
            Width = 50, Height = 50, Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 23, 10, 23), Ring = Green, Fill = Surface, BackColor = Color.Transparent
        }, 0, 0);

        var titleBlock = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            Anchor = AnchorStyles.Left, BackColor = Color.Transparent, Margin = new Padding(0)
        };
        titleBlock.Controls.Add(new Label
        {
            Text = "Griffin Stream", Font = new Font("Segoe UI", 16.5f, FontStyle.Bold),
            ForeColor = TextPrimary, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 1)
        });
        var statusRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true,
            BackColor = Color.Transparent, Margin = new Padding(0)
        };
        statusRow.Controls.Add(new StatusDot { Width = 10, Height = 10, Margin = new Padding(0, 5, 6, 0), DotColor = Green, BackColor = Color.Transparent });
        statusRow.Controls.Add(new Label
        {
            Text = "Server running", Font = new Font("Segoe UI", 9.5f), ForeColor = Green,
            AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0)
        });
        titleBlock.Controls.Add(statusRow);
        grid.Controls.Add(titleBlock, 1, 0);

        _tierBadge = new Pill
        {
            Anchor = AnchorStyles.Right, Margin = new Padding(0, 32, 0, 32),
            Text = "PRO", FillColor = Gold, TextColor = Color.Black, BackColor = Color.Transparent
        };
        grid.Controls.Add(_tierBadge, 2, 0);

        hero.Controls.Add(grid);
        return hero;
    }

    private static Control BuildAddressCard(int port)
    {
        var table = NewCardTable(2);
        table.Controls.Add(MakeCaption("CONNECT TO THIS ADDRESS", Green), 0, 0);
        _addressLabel = new Label
        {
            Text = $"detecting IP… : {port}", Font = new Font("Consolas", 15f, FontStyle.Bold),
            ForeColor = TextPrimary, AutoSize = true, BackColor = Color.Transparent,
            Anchor = AnchorStyles.None, Margin = new Padding(0, 4, 0, 2)
        };
        table.Controls.Add(_addressLabel, 0, 1);
        return WrapCard(table);
    }

    private static Control BuildPinCard(string pin)
    {
        var table = NewCardTable(2);
        table.Controls.Add(MakeCaption("PAIRING PIN", Green), 0, 0);

        // Digit cells: a clean, centered OTP-style row (the PIN is typed on the phone, so no copy button).
        var cells = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent,
            Anchor = AnchorStyles.None, Margin = new Padding(0, 10, 0, 2)
        };
        foreach (var ch in pin)
            cells.Controls.Add(new DigitCell { Text = ch.ToString(), Margin = new Padding(3, 0, 3, 0) });
        table.Controls.Add(cells, 0, 1);

        return WrapCard(table);
    }

    private static Control BuildLicenseCard()
    {
        var table = NewCardTable(4);
        table.Controls.Add(MakeCaption("LICENSE", Gold), 0, 0);

        _licenseTitle = new Label
        {
            Font = new Font("Segoe UI", 11.5f, FontStyle.Bold), ForeColor = Gold,
            AutoSize = true, BackColor = Color.Transparent, Anchor = AnchorStyles.None, Margin = new Padding(0, 2, 0, 3)
        };
        table.Controls.Add(_licenseTitle, 0, 1);

        _licenseBody = new Label
        {
            Font = new Font("Segoe UI", 9.5f), ForeColor = TextSecondary, AutoSize = true,
            MaximumSize = new Size(CardInnerWidth, 0), BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleCenter, Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, 0)
        };
        table.Controls.Add(_licenseBody, 0, 2);

        var (inputPanel, input) = MakeRoundedInput(240, PlaceholderKey);
        _activateInput = input;
        _activateButton = MakePrimaryButton("Activate");
        _activateButton.Margin = new Padding(0, 0, 0, 0);
        _activateButton.Click += OnActivateClicked;

        _activateRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent,
            Anchor = AnchorStyles.None, Margin = new Padding(0, 10, 0, 0)
        };
        inputPanel.Margin = new Padding(0, 0, 8, 0);
        _activateRow.Controls.Add(inputPanel);
        _activateRow.Controls.Add(_activateButton);
        table.Controls.Add(_activateRow, 0, 3);

        return WrapCard(table);
    }

    private static Control BuildFooter()
    {
        _debugButton = MakeSecondaryButton(ConsoleTee.IsConsoleVisible ? "Hide debug log" : "Show debug log");
        _debugButton.Click += (_, _) =>
        {
            bool visible = ConsoleTee.ToggleConsole();
            if (_debugButton != null) _debugButton.Text = visible ? "Hide debug log" : "Show debug log";
        };

        _updateButton = MakeSecondaryButton("Check for updates");
        _updateButton.Click += OnUpdateClicked;

        _versionLabel = new Label
        {
            Text = $"v{Updater.DisplayVersion}",
            AutoSize = true,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 8.5f),
            Margin = new Padding(8, 10, 0, 0),
            BackColor = Color.Transparent
        };

        var footer = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0, 16, 0, 0)
        };
        footer.Controls.Add(_debugButton);
        footer.Controls.Add(_updateButton);
        footer.Controls.Add(_versionLabel);
        return footer;
    }

    private static void TryLoadIcon(Form form)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "griffin.ico");
            if (File.Exists(path)) form.Icon = new Icon(path);
        }
        catch { /* default icon is fine */ }
    }

    // ── State updates ────────────────────────────────────────

    private static void UpdateTierUi()
    {
        var form = _form;
        if (form == null || form.IsDisposed) return;
        void Apply()
        {
            bool isPro = LicenseManager.CurrentTier == ServerTier.Pro;
            bool beta = LicenseManager.BetaFreePro;

            if (_tierBadge != null)
            {
                _tierBadge.Text = isPro ? "PRO" : "FREE";
                _tierBadge.FillColor = isPro ? Gold : Card;
                _tierBadge.TextColor = isPro ? Color.Black : TextSecondary;
                _tierBadge.Invalidate();
            }

            if (beta)
            {
                if (_licenseTitle != null) { _licenseTitle.Text = "Pro — free during the beta"; _licenseTitle.ForeColor = Gold; }
                if (_licenseBody != null)
                {
                    _licenseBody.Text = "All Pro features are unlocked for testers on this build. " +
                                        "No license key needed — just connect from the app.";
                    _licenseBody.ForeColor = TextSecondary;
                }
                if (_activateRow != null) _activateRow.Visible = false;
            }
            else if (isPro)
            {
                if (_licenseTitle != null) { _licenseTitle.Text = "Pro is active on this PC"; _licenseTitle.ForeColor = Green; }
                if (_licenseBody != null) { _licenseBody.Text = LicenseManager.StatusText; _licenseBody.ForeColor = TextSecondary; }
                if (_activateRow != null) _activateRow.Visible = false;
            }
            else
            {
                if (_licenseTitle != null) { _licenseTitle.Text = "Free tier"; _licenseTitle.ForeColor = TextSecondary; }
                if (_licenseBody != null) { _licenseBody.Text = "Paste a Pro license key to unlock the full experience."; _licenseBody.ForeColor = TextSecondary; }
                if (_activateRow != null) _activateRow.Visible = true;
                if (_activateInput != null && string.IsNullOrWhiteSpace(_activateInput.Text))
                {
                    _activateInput.Text = PlaceholderKey;
                    _activateInput.ForeColor = TextMuted;
                }
            }
        }
        try { if (form.InvokeRequired) form.BeginInvoke((Action)Apply); else Apply(); }
        catch { /* window closed; ignore */ }
    }

    private static async void OnActivateClicked(object? sender, EventArgs e)
    {
        var input = _activateInput;
        var button = _activateButton;
        if (input == null || button == null) return;

        var key = input.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(key) || key == PlaceholderKey)
        {
            if (_licenseBody != null) { _licenseBody.ForeColor = Danger; _licenseBody.Text = "Enter your license key first."; }
            return;
        }

        button.Enabled = false;
        var original = button.Text;
        button.Text = "Activating…";
        if (_licenseBody != null) { _licenseBody.ForeColor = TextSecondary; _licenseBody.Text = "Contacting the license server…"; }

        var (ok, message) = await LicenseManager.ActivateAsync(key);

        button.Text = original;
        button.Enabled = true;
        if (_licenseBody != null) { _licenseBody.ForeColor = ok ? Green : Danger; _licenseBody.Text = message; }
    }

    private static async void OnUpdateClicked(object? sender, EventArgs e)
    {
        var button = _updateButton;
        if (button == null) return;

        var known = Updater.Available;
        if (known != null)
        {
            button.Enabled = false;
            button.Text = "Downloading…";
            var progress = new Progress<double>(p =>
            {
                var btn = _updateButton;
                if (btn == null || btn.IsDisposed) return;
                void Apply() => btn.Text = $"Downloading… {(int)(p * 100)}%";
                try
                {
                    if (_form != null && _form.InvokeRequired) _form.BeginInvoke((Action)Apply);
                    else Apply();
                }
                catch { /* ignore */ }
            });
            bool started = await Updater.DownloadAndRunAsync(known, progress);
            if (started)
            {
                // Helper waits for exit, runs silent Setup, relaunches — no MessageBox.
                button.Text = "Restarting…";
                Environment.Exit(0);
            }
            else
            {
                button.Enabled = true;
                button.Text = "Retry update";
            }
            return;
        }

        button.Enabled = false;
        button.Text = "Checking…";
        var info = await Updater.CheckAsync();
        button.Enabled = true;
        if (info != null) ReflectUpdate(info);
        else
        {
            button.Text = "Up to date";
            var reset = new System.Windows.Forms.Timer { Interval = 2500 };
            reset.Tick += (_, _) => { if (Updater.Available == null && _updateButton != null) _updateButton.Text = "Check for updates"; reset.Stop(); reset.Dispose(); };
            reset.Start();
        }
    }

    private static void ReflectUpdate(UpdateInfo info)
    {
        var form = _form;
        var button = _updateButton;
        if (form == null || button == null || form.IsDisposed) return;
        void Apply()
        {
            button.Text = $"Update to {info.Tag}";
            button.ForeColor = Color.Black;
            button.BackColor = Green;
            button.FlatAppearance.BorderColor = Green;
        }
        try { if (form.InvokeRequired) form.BeginInvoke((Action)Apply); else Apply(); }
        catch { /* ignore */ }
    }

    // ── UI building blocks ───────────────────────────────────

    private static Button MakeChromeButton(string glyph, Color hoverBg)
    {
        var b = new Button
        {
            Text = glyph, Width = 46, Height = TitleBarHeight, FlatStyle = FlatStyle.Flat,
            ForeColor = TextSecondary, BackColor = Color.Transparent, Font = new Font("Segoe UI", 10f),
            Cursor = Cursors.Hand, TabStop = false, UseVisualStyleBackColor = false
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = hoverBg;
        b.FlatAppearance.MouseDownBackColor = hoverBg;
        return b;
    }

    private static Button MakePrimaryButton(string text) => new RoundedButton
    {
        Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ForeColor = Color.Black, FillColor = Green, HoverColor = GreenDark, Radius = 9,
        Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
        Padding = new Padding(20, 8, 20, 9), Margin = new Padding(0, 0, 8, 0)
    };

    private static Button MakeSecondaryButton(string text) => new RoundedButton
    {
        Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ForeColor = TextPrimary, FillColor = Card, HoverColor = CardBorder, BorderCol = CardBorder, Radius = 9,
        Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        Padding = new Padding(16, 7, 16, 8), Margin = new Padding(0, 0, 8, 8)
    };

    /// <summary>A flat button with rounded corners and hover feedback (replaces the default square chrome).</summary>
    private sealed class RoundedButton : Button
    {
        public Color FillColor { get; set; } = Green;
        public Color HoverColor { get; set; } = GreenDark;
        public Color BorderCol { get; set; } = Color.Transparent;
        public int Radius { get; set; } = 9;
        private bool _hover;

        public RoundedButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            MouseEnter += (_, _) => { _hover = true; Invalidate(); };
            MouseLeave += (_, _) => { _hover = false; Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            InvokePaintBackground(this, e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = RoundedRect(rect, Radius);
            using (var fill = new SolidBrush(_hover ? HoverColor : FillColor)) g.FillPath(fill, path);
            if (BorderCol.A > 0) { using var pen = new Pen(BorderCol); g.DrawPath(pen, path); }
            TextRenderer.DrawText(g, Text, Font, rect, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>A single-column, auto-sizing layout table used as a card's inner content.</summary>
    private static TableLayoutPanel NewCardTable(int rows)
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = rows, BackColor = Color.Transparent, Margin = new Padding(0)
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (int i = 0; i < rows; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return t;
    }

    private static Label MakeCaption(string text, Color color) => new()
    {
        Text = text, Font = new Font("Segoe UI", 8f, FontStyle.Bold), ForeColor = color,
        AutoSize = true, BackColor = Color.Transparent, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 4)
    };

    /// <summary>Wrap a content control in a rounded card panel.</summary>
    private static Control WrapCard(Control inner)
    {
        var card = new RoundedPanel
        {
            FillColor = Card, BorderColor = CardBorder, Radius = 16, Dock = DockStyle.Fill,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(20, 14, 20, 16), Margin = new Padding(0, 0, 0, 12), BackColor = Color.Transparent
        };
        card.Controls.Add(inner);
        return card;
    }

    /// <summary>A rounded, bordered text input with placeholder behavior. Returns (panel, textbox).</summary>
    private static (RoundedPanel panel, TextBox input) MakeRoundedInput(int width, string placeholder)
    {
        var panel = new RoundedPanel
        {
            FillColor = Bg, BorderColor = CardBorder, Radius = 9, BackColor = Color.Transparent,
            Width = width, Height = 34, Padding = new Padding(11, 7, 11, 7)
        };
        var input = new TextBox
        {
            BorderStyle = BorderStyle.None, BackColor = Bg, ForeColor = TextMuted,
            Font = new Font("Consolas", 10.5f), Dock = DockStyle.Fill, Text = placeholder
        };
        input.GotFocus += (_, _) => { if (input.Text == placeholder) { input.Text = ""; input.ForeColor = TextPrimary; } };
        input.LostFocus += (_, _) => { if (string.IsNullOrWhiteSpace(input.Text)) { input.Text = placeholder; input.ForeColor = TextMuted; } };
        panel.Controls.Add(input);
        return (panel, input);
    }

    // ── Win32 interop (borderless drag + rounded corners) ────

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    /// <summary>Borderless form that paints the branded circuit background and blocks stray closes.</summary>
    private sealed class DashboardForm : Form
    {
        public DashboardForm() => DoubleBuffered = true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW - subtle shadow around the borderless window
                cp.Style |= unchecked((int)0x00020000); // WS_MINIMIZEBOX (enables minimize animation)
                return cp;
            }
        }

        public void ApplyRoundedCorners()
        {
            try
            {
                int pref = DWMWCP_ROUND;
                DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch { /* pre-Win11: square corners, still fine */ }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Faint circuit grid.
            using (var grid = new Pen(Color.FromArgb(10, 0, 255, 136)))
            {
                for (int x = 0; x < Width; x += 26) g.DrawLine(grid, x, 0, x, Height);
                for (int y = 0; y < Height; y += 26) g.DrawLine(grid, 0, y, Width, y);
            }

            // Neon-green glow bleeding down from the top.
            using var glowPath = new GraphicsPath();
            glowPath.AddEllipse(-140, -220, Width + 280, 420);
            using var glow = new PathGradientBrush(glowPath)
            {
                CenterColor = Color.FromArgb(70, 0, 255, 136),
                SurroundColors = new[] { Color.FromArgb(0, 0, 255, 136) }
            };
            g.FillRectangle(glow, 0, 0, Width, 300);
        }
    }

    // ── Custom-painted controls ──────────────────────────────

    private class RoundedPanel : Panel
    {
        public Color FillColor { get; set; } = Color.FromArgb(0x16, 0x16, 0x20);
        public Color BorderColor { get; set; } = Color.FromArgb(0x2A, 0x2A, 0x36);
        public int Radius { get; set; } = 16;

        public RoundedPanel() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                                          ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Transparent parent (circuit bg) shows in the margins around the rounded card.
            base.OnPaintBackground(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = RoundedRect(rect, Radius);
            using var fill = new SolidBrush(FillColor);
            using var pen = new Pen(BorderColor);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
    }

    private sealed class GradientPanel : RoundedPanel
    {
        public Color TopColor { get; set; } = Color.FromArgb(0x0E, 0x2A, 0x1E);
        public Color BottomColor { get; set; } = Color.FromArgb(0x12, 0x12, 0x1A);
        public Color AccentColor { get; set; } = Color.FromArgb(0x00, 0xFF, 0x88);

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = RoundedRect(rect, Radius);
            using (var brush = new LinearGradientBrush(new Rectangle(0, 0, Width, Height), TopColor, BottomColor, LinearGradientMode.Vertical))
                g.FillPath(brush, path);
            using (var pen = new Pen(BorderColor)) g.DrawPath(pen, path);
            using var accent = new Pen(AccentColor, 2f);
            g.DrawLine(accent, Radius, 1, Width - Radius, 1);
        }
    }

    /// <summary>A single rounded PIN digit cell with a subtle green underline accent.</summary>
    private sealed class DigitCell : Control
    {
        public DigitCell()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(42, 54);
            Font = new Font("Consolas", 22f, FontStyle.Bold);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            InvokePaintBackground(this, e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = RoundedRect(rect, 10);
            using (var fill = new SolidBrush(Color.FromArgb(0x12, 0x12, 0x1A))) g.FillPath(fill, path);
            using (var pen = new Pen(Color.FromArgb(0x2A, 0x2A, 0x36))) g.DrawPath(pen, path);
            using (var accent = new Pen(Color.FromArgb(150, 0, 255, 136), 2f))
                g.DrawLine(accent, 11, Height - 5, Width - 11, Height - 5);
            TextRenderer.DrawText(g, Text, Font, rect, Color.FromArgb(0xF0, 0xF0, 0xF2),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    private sealed class StatusDot : Control
    {
        public Color DotColor { get; set; } = Color.FromArgb(0x00, 0xFF, 0x88);
        public StatusDot() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                                       ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        protected override void OnPaint(PaintEventArgs e)
        {
            InvokePaintBackground(this, e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var halo = new SolidBrush(Color.FromArgb(70, DotColor)))
                e.Graphics.FillEllipse(halo, -2, -2, Width + 3, Height + 3);
            using var b = new SolidBrush(DotColor);
            e.Graphics.FillEllipse(b, 1, 1, Width - 3, Height - 3);
        }
    }

    private sealed class Pill : Control
    {
        public Color FillColor { get; set; } = Color.FromArgb(0xFF, 0xB7, 0x00);
        public Color TextColor { get; set; } = Color.Black;

        public Pill()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            Size = new Size(58, 26);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            InvokePaintBackground(this, e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = RoundedRect(rect, Height / 2);
            using (var fill = new SolidBrush(FillColor)) e.Graphics.FillPath(fill, path);
            TextRenderer.DrawText(e.Graphics, Text, Font, rect, TextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    private sealed class LogoBadge : Control
    {
        public Color Ring { get; set; } = Color.FromArgb(0x00, 0xFF, 0x88);
        public Color Fill { get; set; } = Color.FromArgb(0x12, 0x12, 0x1A);
        private readonly Image? _icon;

        public LogoBadge()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "griffin.ico");
                if (File.Exists(path)) _icon = new Icon(path, 64, 64).ToBitmap();
            }
            catch { _icon = null; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            InvokePaintBackground(this, e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var halo = new SolidBrush(Color.FromArgb(60, Ring)))
                g.FillEllipse(halo, -2, -2, Width + 3, Height + 3);
            var rect = new Rectangle(1, 1, Width - 3, Height - 3);
            using (var fill = new SolidBrush(Fill)) g.FillEllipse(fill, rect);
            using (var pen = new Pen(Ring, 2f)) g.DrawEllipse(pen, rect);

            if (_icon != null)
            {
                var inset = new Rectangle(8, 8, Width - 16, Height - 16);
                g.DrawImage(_icon, inset);
            }
            else
            {
                float cx = Width / 2f, cy = Height / 2f;
                var pts = new[]
                {
                    new PointF(cx + 2, cy - 12), new PointF(cx - 7, cy + 2), new PointF(cx - 1, cy + 2),
                    new PointF(cx - 2, cy + 12), new PointF(cx + 8, cy - 3), new PointF(cx + 1, cy - 3),
                };
                using var b = new SolidBrush(Ring);
                g.FillPolygon(b, pts);
            }
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
