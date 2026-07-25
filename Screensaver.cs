using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Windows.Forms;

namespace AquariumSaver;

// ── ExitWatcher — mouse/keyboard quit detection ────────────────────────────────

public class ExitWatcher
{
    readonly Point _startPos;
    public event EventHandler? Quit;
    public bool ShouldQuit { get; private set; }

    public ExitWatcher() => _startPos = Cursor.Position;

    public void AddQuit(EventHandler h) => Quit += h;
    public void RemoveQuit(EventHandler h) => Quit -= h;

    public bool Check(Point mousePos, bool keyPressed)
    {
        if (ShouldQuit) return true;
        if (keyPressed) { ShouldQuit = true; Quit?.Invoke(this, EventArgs.Empty); return true; }
        var dx = mousePos.X - _startPos.X;
        var dy = mousePos.Y - _startPos.Y;
        if (dx * dx + dy * dy > 64) // 8px threshold²
        {
            ShouldQuit = true;
            Quit?.Invoke(this, EventArgs.Empty);
            return true;
        }
        return false;
    }
}

// ── ScreensaverForm — full-screen per monitor ──────────────────────────────────

public class ScreensaverForm : Form
{
    readonly Screen? _screen;
    readonly ExitWatcher? _exitWatcher;
    readonly int _seed;
    readonly SettingsData _settings;
    System.Windows.Forms.Timer? _timer;
    Scene? _scene;
    Bitmap? _backBuffer;
    Graphics? _backGraphics;
    bool _closing;

    public ScreensaverForm(Screen? screen, ExitWatcher? exitWatcher, int seed, SettingsData settings)
    {
        _screen = screen;
        _exitWatcher = exitWatcher;
        _seed = seed;
        _settings = settings;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        if (screen != null)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = screen.Bounds;
        }
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        _exitWatcher?.AddQuit(OnQuit);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        InitScene();
        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    void OnTimerTick(object? sender, EventArgs e)
    {
        if (_closing || _exitWatcher?.ShouldQuit == true) return;

        // Render every timer tick (~60hz) for smooth motion on
        // high-refresh displays.  The Scene uses a shared Stopwatch
        // so animation time is always continuous and smooth.
        if (_backGraphics != null)
        {
            _scene?.Draw(_backGraphics, ClientSize);
        }

        Invalidate();
        Update();
    }

    void InitScene()
    {
        // Full-screen monitor forms use the four-argument constructor so
        // every monitor shares one virtual-desktop aquarium.
        if (_screen != null)
        {
            _scene = new Scene(_seed, _settings, ClientSize, _screen.Bounds);
        }
        else
        {
            _scene = new Scene(_seed, _settings, ClientSize);
        }
        _backBuffer = new Bitmap(ClientSize.Width, ClientSize.Height);
        _backGraphics = Graphics.FromImage(_backBuffer);
        _backGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        _backGraphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        _backGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _backBuffer?.Dispose(); _backGraphics?.Dispose();
        _backBuffer = new Bitmap(ClientSize.Width, ClientSize.Height);
        _backGraphics = Graphics.FromImage(_backBuffer);
        _backGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        _backGraphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        _backGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        _scene?.Dispose();
        if (_screen != null)
        {
            _scene = new Scene(_seed, _settings, ClientSize, _screen.Bounds);
        }
        else
        {
            _scene = new Scene(_seed, _settings, ClientSize);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_backBuffer != null)
            e.Graphics.DrawImageUnscaled(_backBuffer, 0, 0);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DISPLAYCHANGE)
        {
            if (!_closing) { _closing = true; _timer?.Stop(); Application.Exit(); }
        }
        else if (m.Msg is 0x0200 or 0x0201 or 0x0204 or 0x0207 or 0x0100 or 0x0101)
        {
            var keyPressed = m.Msg is 0x0100 or 0x0101;
            if (_exitWatcher != null && _exitWatcher.Check(Cursor.Position, keyPressed))
            { OnQuit(null, EventArgs.Empty); return; }
        }
        base.WndProc(ref m);
    }

    void OnQuit(object? s, EventArgs e)
    {
        if (_closing) return;
        _closing = true;
        _timer?.Stop();
        _exitWatcher?.RemoveQuit(OnQuit);
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exitWatcher?.RemoveQuit(OnQuit);
            _timer?.Dispose();
            _backGraphics?.Dispose();
            _backBuffer?.Dispose();
            _scene?.Dispose();
        }
        base.Dispose(disposing);
    }
}

// ── PreviewForm — parented to control panel ────────────────────────────────────

public class PreviewForm : Form
{
    readonly IntPtr _parentHwnd;
    readonly SettingsData _settings;
    System.Windows.Forms.Timer? _renderTimer, _parentCheck;
    Scene? _scene;
    Bitmap? _backBuffer;
    Graphics? _backGraphics;
    bool _closing;

    public PreviewForm(IntPtr parentHwnd, SettingsData settings)
    {
        _parentHwnd = parentHwnd;
        _settings = settings;

        CreateControl();
        var style = Native.GetWindowLongPtr(Handle, Native.GWL_STYLE);
        Native.SetWindowLongPtr(Handle, Native.GWL_STYLE, (nint)(Native.WS_CHILD | Native.WS_VISIBLE));
        Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, 0);
        Native.SetParent(Handle, parentHwnd);

        if (Native.GetClientRect(parentHwnd, out var rect))
        { Width = rect.Width; Height = rect.Height; }

        ShowInTaskbar = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var previewFish = Math.Max(2, Math.Min(3, _settings.FishCount));
        var previewBubbles = Math.Max(5, Math.Min(20, _settings.BubbleDensity));
        var previewSettings = new SettingsData
        {
            FishCount = previewFish,
            BubbleDensity = previewBubbles,
            ShowSeaweed = _settings.ShowSeaweed,
            ShowLightShafts = false,
            ShowBackgroundChest = false,
            BackgroundTopColor = _settings.BackgroundTopColor,
            BackgroundBottomColor = _settings.BackgroundBottomColor,
        };

        _scene = new Scene(42, previewSettings, ClientSize);
        _backBuffer = new Bitmap(ClientSize.Width, ClientSize.Height);
        _backGraphics = Graphics.FromImage(_backBuffer);
        _backGraphics.SmoothingMode = SmoothingMode.AntiAlias;

        _renderTimer = new System.Windows.Forms.Timer { Interval = 33 };
        _renderTimer.Tick += (_, _) => { if (!_closing) Invalidate(); };
        _renderTimer.Start();

        _parentCheck = new System.Windows.Forms.Timer { Interval = 500 };
        _parentCheck.Tick += (_, _) => { if (!Native.IsWindow(_parentHwnd)) ClosePreview(); };
        _parentCheck.Start();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_scene == null || _backGraphics == null || _backBuffer == null) return;
        _scene.Update(1.0 / 30.0);
        _scene.Draw(_backGraphics, ClientSize);
        e.Graphics.DrawImageUnscaled(_backBuffer, 0, 0);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _backBuffer?.Dispose(); _backGraphics?.Dispose();
        _backBuffer = new Bitmap(ClientSize.Width, ClientSize.Height);
        _backGraphics = Graphics.FromImage(_backBuffer);
        _backGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        _scene?.Dispose();
        _scene = new Scene(42, new SettingsData { FishCount = Math.Max(2, Math.Min(3, _settings.FishCount)),
            BubbleDensity = Math.Max(5, Math.Min(20, _settings.BubbleDensity)),
            ShowSeaweed = _settings.ShowSeaweed, ShowLightShafts = false, ShowBackgroundChest = false,
            BackgroundTopColor = _settings.BackgroundTopColor, BackgroundBottomColor = _settings.BackgroundBottomColor }, ClientSize);
    }

    void ClosePreview()
    {
        if (_closing) return;
        _closing = true;
        _renderTimer?.Stop();
        _parentCheck?.Stop();
        BeginInvoke(() => Application.ExitThread());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _renderTimer?.Dispose(); _parentCheck?.Dispose(); _backGraphics?.Dispose(); _backBuffer?.Dispose(); _scene?.Dispose(); }
        base.Dispose(disposing);
    }
}

// ── ConfigForm — settings dialog ───────────────────────────────────────────────

public class ConfigForm : Form
{
    NumericUpDown _nudFish = null!, _nudBubbles = null!;
    TrackBar _trkSpeed = null!;
    Label _lblSpeed = null!;
    CheckBox _chkChest = null!, _chkIndependent = null!, _chkBattery = null!;
    Button _btnTopColor = null!, _btnBottomColor = null!;
    Label _lblTopColor = null!, _lblBottomColor = null!;
    ComboBox _cmbFps = null!;
    Button _btnOk = null!, _btnCancel = null!, _btnDefaults = null!;
    Panel _previewPanel = null!;

    SettingsData _settings;
    Scene? _previewScene;
    System.Windows.Forms.Timer? _previewTimer;

    public ConfigForm()
    {
        _settings = Settings.Load();
        Text = "Aquarium Screensaver Settings";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(400, 540);
        MinimumSize = Size;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        BuildUi();
        LoadValues();
    }

    void BuildUi()
    {
        int xR = 215, xL = 15, y = 12, rh = 28;

        AddLabel(xL, y, "Fish count:");
        _nudFish = AddNud(xR, y, SettingsData.FishCountMin, SettingsData.FishCountMax);
        y += rh;

        AddLabel(xL, y, "Bubble density:");
        _nudBubbles = AddNud(xR, y, SettingsData.BubbleDensityMin, SettingsData.BubbleDensityMax);
        y += rh;

        AddLabel(xL, y, "Speed:");
        _trkSpeed = new TrackBar { Location = new Point(xR - 5, y - 2), Minimum = 25, Maximum = 300, Width = 180, TickFrequency = 25 };
        _trkSpeed.Scroll += (_, _) => _lblSpeed.Text = (_trkSpeed.Value / 100f).ToString("F2") + "x";
        Controls.Add(_trkSpeed);
        _lblSpeed = new Label { Location = new Point(xR + 185, y + 2), AutoSize = true };
        Controls.Add(_lblSpeed);
        y += rh + 4;


        _chkChest = AddCb(xL, y, "Show background chest"); y += rh;
        _chkIndependent = AddCb(xL, y, "Independent scenes per monitor"); y += rh;
        _chkBattery = AddCb(xL, y, "Pause on battery"); y += rh + 4;

        AddLabel(xL, y, "Top color:");
        _btnTopColor = new Button { Location = new Point(xR, y + 1), Text = "▬", Size = new Size(50, 22) };
        _btnTopColor.Click += (_, _) => PickColor(true);
        Controls.Add(_btnTopColor);
        _lblTopColor = new Label { Location = new Point(xR + 58, y + 3), AutoSize = true };
        Controls.Add(_lblTopColor);
        AddLabel(215, y, "Bottom color:");
        _btnBottomColor = new Button { Location = new Point(300, y + 1), Text = "▬", Size = new Size(50, 22) };
        _btnBottomColor.Click += (_, _) => PickColor(false);
        Controls.Add(_btnBottomColor);
        _lblBottomColor = new Label { Location = new Point(358, y + 3), AutoSize = true };
        Controls.Add(_lblBottomColor);
        y += rh + 4;

        AddLabel(xL, y, "Target FPS:");
        _cmbFps = new ComboBox { Location = new Point(xR, y + 1), DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
        _cmbFps.Items.AddRange(new object[] { "30", "60" });
        Controls.Add(_cmbFps);
        y += rh + 12;

        Controls.Add(new Label { Text = "Preview:", Location = new Point(xL, y), AutoSize = true });
        y += 18;
        _previewPanel = new Panel { Location = new Point(xL, y), Size = new Size(365, 100), BackColor = Color.Black, BorderStyle = BorderStyle.FixedSingle };
        Controls.Add(_previewPanel);
        y += 110;

        _btnDefaults = new Button { Text = "Defaults", Location = new Point(xL, y), Size = new Size(75, 25) };
        _btnDefaults.Click += (_, _) => { _settings = SettingsData.Defaults; LoadValues(); };
        Controls.Add(_btnDefaults);

        _btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(215, y), Size = new Size(75, 25) };
        Controls.Add(_btnOk);
        _btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(298, y), Size = new Size(75, 25) };
        Controls.Add(_btnCancel);
        AcceptButton = _btnOk;
        CancelButton = _btnCancel;
    }

    NumericUpDown AddNud(int x, int y, int min, int max)
    {
        var nud = new NumericUpDown { Location = new Point(x, y + 2), Minimum = min, Maximum = max, Width = 60 };
        Controls.Add(nud);
        return nud;
    }

    CheckBox AddCb(int x, int y, string text)
    {
        var cb = new CheckBox { Location = new Point(x, y), Text = text, AutoSize = true };
        Controls.Add(cb);
        return cb;
    }

    void AddLabel(int x, int y, string text)
    {
        var lbl = new Label { Location = new Point(x, y), Text = text, AutoSize = true };
        Controls.Add(lbl);
    }

    void LoadValues()
    {
        _nudFish.Value = _settings.FishCount;
        _nudBubbles.Value = _settings.BubbleDensity;
        _trkSpeed.Value = (int)(_settings.SpeedMultiplier * 100);
        _lblSpeed.Text = (_settings.SpeedMultiplier).ToString("F2") + "x";

        _chkChest.Checked = _settings.ShowBackgroundChest;
        _chkIndependent.Checked = _settings.IndependentScenesPerMonitor;
        _chkBattery.Checked = _settings.PauseOnBattery;
        _cmbFps.SelectedItem = _settings.TargetFps.ToString();

        UpdateColorBtn(true);
        UpdateColorBtn(false);
        UpdatePreview();
    }

    void UpdateColorBtn(bool top)
    {
        var hex = top ? _settings.BackgroundTopColor : _settings.BackgroundBottomColor;
        var btn = top ? _btnTopColor : _btnBottomColor;
        var lbl = top ? _lblTopColor : _lblBottomColor;
        try
        {
            var c = _settings.GetTopColor(); // or GetBottomColor
            c = top ? _settings.GetTopColor() : _settings.GetBottomColor();
            btn.BackColor = c;
            lbl.Text = hex;
        }
        catch { btn.BackColor = Color.Gray; lbl.Text = hex; }
    }

    void PickColor(bool top)
    {
        var dlg = new ColorDialog { FullOpen = true, AnyColor = true };
        try { dlg.Color = top ? _settings.GetTopColor() : _settings.GetBottomColor(); }
        catch { }
        if (dlg.ShowDialog() == DialogResult.OK)
        {
            var hex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
            if (top) _settings.BackgroundTopColor = hex; else _settings.BackgroundBottomColor = hex;
            UpdateColorBtn(top);
            UpdatePreview();
        }
    }

    void UpdatePreview()
    {
        _previewScene?.Dispose();
        _previewScene = new Scene(42, new SettingsData
        {
            FishCount = Math.Max(2, Math.Min(5, _settings.FishCount)),
            BubbleDensity = Math.Max(5, Math.Min(30, _settings.BubbleDensity)),
            ShowSeaweed = _settings.ShowSeaweed,
            ShowLightShafts = _settings.ShowLightShafts,
            ShowBackgroundChest = _settings.ShowBackgroundChest,
            BackgroundTopColor = _settings.BackgroundTopColor,
            BackgroundBottomColor = _settings.BackgroundBottomColor,
        }, _previewPanel.ClientSize);

        _previewPanel.Paint -= PreviewPaint;
        _previewPanel.Paint += PreviewPaint;

        if (_previewTimer == null)
        {
            _previewTimer = new System.Windows.Forms.Timer { Interval = 33 };
            _previewTimer.Tick += (_, _) => _previewPanel.Invalidate();
        }
        _previewTimer.Start();
    }

    void PreviewPaint(object? s, PaintEventArgs e)
    {
        if (_previewScene == null) return;
        _previewScene.Update(1.0 / 30.0);
        _previewScene.Draw(e.Graphics, _previewPanel.ClientSize);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
        {
            _settings.FishCount = (int)_nudFish.Value;
            _settings.BubbleDensity = (int)_nudBubbles.Value;
            _settings.SpeedMultiplier = _trkSpeed.Value / 100f;
            _settings.ShowSeaweed = false;
            _settings.ShowLightShafts = false;
            _settings.ShowBackgroundChest = _chkChest.Checked;
            _settings.IndependentScenesPerMonitor = _chkIndependent.Checked;
            _settings.PauseOnBattery = _chkBattery.Checked;
            if (int.TryParse(_cmbFps.SelectedItem?.ToString(), out var fps)) _settings.TargetFps = fps;
            Settings.Save(_settings.Clamp());
        }
        base.OnFormClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _previewTimer?.Stop(); _previewTimer?.Dispose(); _previewScene?.Dispose();
        base.OnClosed(e);
    }
}

// ── FrameClock ─────────────────────────────────────────────────────────────────

public class FrameClock
{
    readonly Stopwatch _sw = Stopwatch.StartNew();
    double _accumulated, _lastFrameTime;
    readonly double _targetInterval;
    public const float MaxDelta = 0.05f;

    public FrameClock(int targetFps) { _targetInterval = 1.0 / targetFps; }

    public bool TryWaitTick()
    {
        var elapsed = _sw.Elapsed.TotalSeconds;
        if (elapsed - _lastFrameTime < _targetInterval) return false;
        _accumulated += _targetInterval;
        _lastFrameTime = elapsed;
        return true;
    }

    public double GetDelta()
    {
        var raw = _accumulated;
        _accumulated = 0;
        return Math.Min(raw, MaxDelta);
    }
}
