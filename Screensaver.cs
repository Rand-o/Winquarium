using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AquariumSaver;

// ── Logging ────────────────────────────────────────────────────────────────────

internal static class AppLog
{
    private static readonly string LogPath =
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "AquariumSaver",
            "AquariumSaver.log");

    public static void Log(string message)
    {
        try
        {
            string? directory = Path.GetDirectoryName(LogPath);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.AppendAllText(
                LogPath,
                $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never crash the screensaver.
        }
    }
}

// ── ExitWatcher — mouse/keyboard quit detection ────────────────────────────────

public sealed class ExitWatcher
{
    private Point _startPos;
    private readonly Stopwatch _startupClock = Stopwatch.StartNew();

    private const int StartupGraceMilliseconds = 3000;
    private const int MovementThresholdPixels = 80;

    public event EventHandler? Quit;
    public bool ShouldQuit { get; private set; }

    public ExitWatcher()
    {
        _startPos = Cursor.Position;
    }

    public void AddQuit(EventHandler handler)
    {
        Quit += handler;
    }

    public void RemoveQuit(EventHandler handler)
    {
        Quit -= handler;
    }

    public bool Check(Point mousePosition, bool keyPressed)
    {
        if (ShouldQuit)
            return true;

        if (_startupClock.ElapsedMilliseconds < StartupGraceMilliseconds)
        {
            // Ignore pointer movement caused by form activation,
            // monitor switching, or initial cursor settling.
            _startPos = mousePosition;
            return false;
        }

        if (keyPressed)
        {
            RequestQuit();
            return true;
        }

        long dx = mousePosition.X - _startPos.X;
        long dy = mousePosition.Y - _startPos.Y;

        long thresholdSquared =
            MovementThresholdPixels * MovementThresholdPixels;

        if (dx * dx + dy * dy > thresholdSquared)
        {
            RequestQuit();
            return true;
        }

        return false;
    }

    private void RequestQuit()
    {
        if (ShouldQuit)
            return;

        ShouldQuit = true;
        Quit?.Invoke(this, EventArgs.Empty);
    }
}

// ── ScreensaverForm — full-screen per monitor ──────────────────────────────────

public class ScreensaverForm : Form
{
    readonly Screen? _screen;
    readonly ExitWatcher? _exitWatcher;
    readonly int _seed;
    readonly SettingsData _settings;
    System.Windows.Forms.Timer? _renderTimer;
    System.Windows.Forms.Timer? _displayChangeTimer;
    readonly Stopwatch _renderClock = new();
    double _lastRenderTime;
    volatile bool _closing;
    Scene? _scene;

    // ── Double-buffered publication ──
    // One bitmap is the last successfully completed frame (front).
    // The other is the hidden render target. A failed draw is never presented.
    Bitmap? _bufferA;
    Bitmap? _bufferB;
    Graphics? _graphicsA;
    Graphics? _graphicsB;
    bool _frontIsA;

    private Bitmap? FrontBuffer => _frontIsA ? _bufferA : _bufferB;
    private Graphics? RenderGraphics => _frontIsA ? _graphicsB : _graphicsA;

    Size _clientSize;
    bool _loaded;
    bool _rebuilding;

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
        BackColor = Color.Black;

        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.Opaque |
            ControlStyles.ResizeRedraw,
            true);

        UpdateStyles();

        _exitWatcher?.AddQuit(OnQuit);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        _loaded = true;

        InitScene();

        int targetFps = Math.Clamp(
            _settings.TargetFps > 0
                ? _settings.TargetFps
                : DetectRefreshRate(_screen),
            30,
            240);

        _renderClock.Restart();
        _lastRenderTime = _renderClock.Elapsed.TotalSeconds;

        _renderTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(1, 1000 / targetFps)
        };

        _renderTimer.Tick += RenderTimerTick;
        _renderTimer.Start();

        Invalidate();
    }

    private int _consecutiveRenderFailures;
    private DateTime _lastRenderFailureLog = DateTime.MinValue;

    private void RenderTimerTick(object? sender, EventArgs e)
    {
        if (_closing ||
            IsDisposed ||
            _scene == null ||
            FrontBuffer == null ||
            RenderGraphics == null)
        {
            return;
        }

        double now =
            _renderClock.Elapsed.TotalSeconds;

        double elapsed =
            Math.Clamp(
                now - _lastRenderTime,
                0.0,
                0.05);

        _lastRenderTime = now;

        try
        {
            _scene.Update(elapsed);

            /*
             * Draw only into the hidden buffer. Scene.Draw() may clear or
             * partially modify it without affecting the visible frame.
             */
            Graphics renderGraphics =
                RenderGraphics;

            _scene.Draw(
                renderGraphics,
                _clientSize);

            /*
             * Publish only after the complete frame succeeds.
             * Timer Tick and OnPaint run on the same UI thread, so this
             * boolean swap is safe.
             */
            _frontIsA = !_frontIsA;

            _consecutiveRenderFailures = 0;

            Invalidate();
        }
        catch (Exception exception)
        {
            _consecutiveRenderFailures++;

            /*
             * Avoid writing the same large exception hundreds of times per
             * second, but retain enough information to diagnose the failure.
             */
            if ((DateTime.UtcNow - _lastRenderFailureLog)
                    .TotalSeconds >= 1.0)
            {
                _lastRenderFailureLog =
                    DateTime.UtcNow;

                AppLog.Log(
                    $"Render failure #{_consecutiveRenderFailures}:" +
                    Environment.NewLine +
                    exception);
            }

            /*
             * Do not swap buffers.
             * Do not call InitScene().
             * Do not invalidate.
             *
             * The last successfully completed frame remains visible.
             */
            if (_consecutiveRenderFailures >= 300)
            {
                AppLog.Log(
                    "Rendering suspended after 300 consecutive failures.");

                _renderTimer?.Stop();
            }
        }
    }

    /// <summary>
    /// Detect the specified monitor's refresh rate, clamped to 30-240hz.
    /// Falls back to 60hz if detection fails.
    /// Uses the form's own screen, not always the primary monitor.
    /// </summary>
    static int DetectRefreshRate(Screen? screen)
    {
        try
        {
            screen ??= Screen.PrimaryScreen;
            if (screen != null)
            {
                var dm = new DevMode
                {
                    dmSize = (short)Marshal.SizeOf<DevMode>()
                };
                if (EnumDisplaySettings(screen.DeviceName, -1, ref dm) && dm.dmDisplayFrequency > 0)
                {
                    return Math.Clamp((int)dm.dmDisplayFrequency, 30, 240);
                }
            }
        }
        catch { /* fall through to default */ }
        return 60;
    }

    // DEVMODEA — matches the Windows SDK layout exactly.
    // dmPosition is a POINTL (two ints), not two shorts.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DevMode lpDevMode);

    private void InitScene()
    {
        _scene?.Dispose();
        _scene = null;

        DisposeRenderResources();

        _clientSize = new Size(
            Math.Max(1, ClientSize.Width),
            Math.Max(1, ClientSize.Height));

        AppLog.Log($"InitScene: ClientSize={ClientSize}, Bounds={Bounds}, screen={(_screen?.DeviceName ?? "null")}, screenBounds={(_screen?.Bounds.ToString() ?? "null")}");

        // Full-screen monitor forms use the four-argument constructor so
        // every monitor shares one virtual-desktop aquarium.
        if (_screen != null)
        {
            _scene = new Scene(
                _seed,
                _settings,
                _clientSize,
                _screen.Bounds);
        }
        else
        {
            _scene = new Scene(
                _seed,
                _settings,
                _clientSize);
        }

        CreateRenderResources(_clientSize);

        /*
         * Render into buffer B. Buffer A remains the opaque black fallback.
         * Publish B only if the entire draw succeeds.
         */
        _scene.Update(0.0);
        _scene.Draw(_graphicsB!, _clientSize);

        _frontIsA = false;
    }

    private void DisposeRenderResources()
    {
        // Graphics must be disposed before their bitmaps.
        _graphicsA?.Dispose();
        _graphicsA = null;

        _graphicsB?.Dispose();
        _graphicsB = null;

        _bufferA?.Dispose();
        _bufferA = null;

        _bufferB?.Dispose();
        _bufferB = null;
    }

    private static void ConfigureGraphics(Graphics graphics)
    {
        graphics.CompositingMode =
            CompositingMode.SourceOver;

        graphics.CompositingQuality =
            CompositingQuality.HighSpeed;

        graphics.InterpolationMode =
            InterpolationMode.HighQualityBilinear;

        graphics.PixelOffsetMode =
            PixelOffsetMode.HighQuality;

        graphics.SmoothingMode =
            SmoothingMode.None;
    }

    private void CreateRenderResources(Size size)
    {
        DisposeRenderResources();

        _bufferA = new Bitmap(
            size.Width,
            size.Height,
            PixelFormat.Format32bppPArgb);

        _bufferB = new Bitmap(
            size.Width,
            size.Height,
            PixelFormat.Format32bppPArgb);

        _graphicsA = Graphics.FromImage(_bufferA);
        _graphicsB = Graphics.FromImage(_bufferB);

        ConfigureGraphics(_graphicsA);
        ConfigureGraphics(_graphicsB);

        // Establish safe opaque contents in both buffers.
        _graphicsA.CompositingMode = CompositingMode.SourceCopy;
        _graphicsA.Clear(Color.Black);
        _graphicsA.CompositingMode = CompositingMode.SourceOver;

        _graphicsB.CompositingMode = CompositingMode.SourceCopy;
        _graphicsB.Clear(Color.Black);
        _graphicsB.CompositingMode = CompositingMode.SourceOver;

        _frontIsA = true;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);

        if (!_loaded ||
            _closing ||
            _rebuilding ||
            !IsHandleCreated ||
            ClientSize.Width <= 0 ||
            ClientSize.Height <= 0)
        {
            return;
        }

        Size newSize = ClientSize;

        if (newSize == _clientSize)
        {
            return;
        }

        RebuildSceneSafely("client size changed");
    }

    private void RebuildSceneSafely(string reason)
    {
        if (_closing ||
            _rebuilding ||
            IsDisposed)
        {
            return;
        }

        _rebuilding = true;

        bool timerWasRunning =
            _renderTimer?.Enabled == true;

        try
        {
            _renderTimer?.Stop();

            AppLog.Log(
                $"Rebuilding scene: {reason}.");

            InitScene();

            _lastRenderTime =
                _renderClock.Elapsed.TotalSeconds;

            _consecutiveRenderFailures = 0;

            Invalidate();
        }
        catch (Exception exception)
        {
            AppLog.Log(
                $"Scene rebuild failed ({reason}):" +
                Environment.NewLine +
                exception);
        }
        finally
        {
            _rebuilding = false;

            if (timerWasRunning &&
                !_closing &&
                !IsDisposed)
            {
                _renderTimer?.Start();
            }
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // OnPaint paints the entire opaque client area.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Bitmap? completedFrame = FrontBuffer;

        if (completedFrame == null)
        {
            e.Graphics.Clear(Color.Black);
            return;
        }

        e.Graphics.ResetTransform();
        e.Graphics.ResetClip();

        /*
         * The frame is opaque and covers the entire client area.
         * Copy it directly without first exposing a black intermediate image.
         */
        e.Graphics.CompositingMode =
            CompositingMode.SourceCopy;

        e.Graphics.DrawImageUnscaled(
            completedFrame,
            0,
            0);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DISPLAYCHANGE)
        {
            AppLog.Log(
                "WM_DISPLAYCHANGE received; scheduling rebuild.");

            _displayChangeTimer ??=
                new System.Windows.Forms.Timer
                {
                    Interval = 750
                };

            _displayChangeTimer.Stop();
            _displayChangeTimer.Tick -=
                DisplayChangeTimerTick;

            _displayChangeTimer.Tick +=
                DisplayChangeTimerTick;

            _displayChangeTimer.Start();

            m.Result = IntPtr.Zero;
            return;
        }
        else if (m.Msg is
                 0x0201 or // left button
                 0x0204 or // right button
                 0x0207 or // middle button
                 0x0100)   // key down
        {
            var keyPressed = m.Msg is 0x0100;
            if (_exitWatcher != null && _exitWatcher.Check(Cursor.Position, keyPressed))
            { OnQuit(null, EventArgs.Empty); return; }
        }
        base.WndProc(ref m);
    }

    private void OnQuit(object? sender, EventArgs e)
    {
        if (_closing)
        {
            return;
        }

        AppLog.Log(
            $"Intentional shutdown requested. " +
            $"Cursor={CursorPositionString()}");

        _closing = true;

        _renderTimer?.Stop();
        _displayChangeTimer?.Stop();

        _exitWatcher?.RemoveQuit(OnQuit);

        Application.Exit();
    }

    private static string CursorPositionString()
    {
        try
        {
            var p = Cursor.Position;
            return $"({p.X},{p.Y})";
        }
        catch
        {
            return "unknown";
        }
    }

    private void DisplayChangeTimerTick(
        object? sender,
        EventArgs e)
    {
        _displayChangeTimer?.Stop();

        RebuildSceneSafely(
            "display configuration changed");
    }

    protected override void OnFormClosing(
        FormClosingEventArgs e)
    {
        AppLog.Log(
            $"ScreensaverForm closing. " +
            $"Reason={e.CloseReason}, " +
            $"ClosingFlag={_closing}, " +
            $"RenderFailures={_consecutiveRenderFailures}.");

        base.OnFormClosing(e);
    }

    protected override void OnHandleDestroyed(
        EventArgs e)
    {
        AppLog.Log(
            $"ScreensaverForm handle destroyed. " +
            $"RecreatingHandle={RecreatingHandle}, " +
            $"Disposed={IsDisposed}.");

        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing = true;

            if (_renderTimer != null)
            {
                _renderTimer.Stop();
                _renderTimer.Tick -=
                    RenderTimerTick;

                _renderTimer.Dispose();
                _renderTimer = null;
            }

            if (_displayChangeTimer != null)
            {
                _displayChangeTimer.Stop();
                _displayChangeTimer.Tick -=
                    DisplayChangeTimerTick;

                _displayChangeTimer.Dispose();
                _displayChangeTimer = null;
            }

            _renderClock.Stop();

            _exitWatcher?.RemoveQuit(OnQuit);

            DisposeRenderResources();

            _scene?.Dispose();
            _scene = null;
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
        BackColor = Color.Black;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.Opaque,
            true);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var previewSettings = new SettingsData
        {
            SwimAngle = _settings.SwimAngle,
            BackgroundTopColor = _settings.BackgroundTopColor,
            BackgroundBottomColor = _settings.BackgroundBottomColor,
            BubbleEmitters = _settings.BubbleEmitters.Select(e => (BubbleEmitterConfig)e.Clone()).ToArray(),
            SpeciesConfigs = _settings.SpeciesConfigs.ToDictionary(k => k.Key, v => (SpeciesConfig)v.Value.Clone()),
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

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // OnPaint paints the entire opaque client area.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.Black);

        if (_scene == null || _backGraphics == null || _backBuffer == null) return;
        _scene.Update(1.0 / 30.0);
        _scene.Draw(_backGraphics, ClientSize);
        e.Graphics.CompositingMode = CompositingMode.SourceOver;
        e.Graphics.DrawImageUnscaled(_backBuffer, 0, 0);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);

        if (ClientSize.Width <= 0 ||
            ClientSize.Height <= 0)
        {
            return;
        }

        _backGraphics?.Dispose();
        _backGraphics = null;

        _backBuffer?.Dispose();
        _backBuffer = null;

        _scene?.Dispose();
        _scene = null;

        _backBuffer = new Bitmap(ClientSize.Width, ClientSize.Height);
        _backGraphics = Graphics.FromImage(_backBuffer);
        _backGraphics.SmoothingMode = SmoothingMode.AntiAlias;

        _scene = new Scene(42, new SettingsData {
            SwimAngle = _settings.SwimAngle,
            BackgroundTopColor = _settings.BackgroundTopColor, BackgroundBottomColor = _settings.BackgroundBottomColor,
            BubbleEmitters = _settings.BubbleEmitters.Select(e => (BubbleEmitterConfig)e.Clone()).ToArray(),
            SpeciesConfigs = _settings.SpeciesConfigs.ToDictionary(k => k.Key, v => (SpeciesConfig)v.Value.Clone()),
        }, ClientSize);
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
    // Global settings controls
    NumericUpDown _nudSwimAngle = null!;
    Label _lblSwimAngle = null!;
    CheckBox _chkIndependent = null!, _chkBattery = null!;
    ComboBox _cmbFps = null!;
    Button _btnOk = null!, _btnCancel = null!, _btnDefaults = null!;
    Panel _previewPanel = null!;

    // Per-species controls stored in a list
    readonly List<SpeciesRow> _speciesRows = new();
    // Per-bubble-emitter controls
    readonly List<BubbleEmitterRow> _bubbleEmitterRows = new();
    Button _btnAddEmitter = null!;

    SettingsData _settings;
    Scene? _previewScene;
    System.Windows.Forms.Timer? _previewTimer;

    public ConfigForm()
    {
        _settings = Settings.Load();
        Text = "Aquarium Screensaver Settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        BuildUi();
        LoadValues();
    }

    void BuildUi()
    {
        int xR = 215, xL = 15, y = 12, rh = 28;

        // ── Global section ──
        var globalHeader = new Label { Text = "Global Settings", Location = new Point(xL, y), AutoSize = true, Font = new Font(Font.FontFamily, 9f, FontStyle.Bold) };
        Controls.Add(globalHeader);
        y += 20;

        AddLabel(xL, y, "Swim angle (deg):");
        _nudSwimAngle = AddNud(xR, y, SettingsData.SwimAngleMin, SettingsData.SwimAngleMax);
        _nudSwimAngle.DecimalPlaces = 1;
        _nudSwimAngle.Increment = 1;
        Controls.Add(_nudSwimAngle);
        _lblSwimAngle = new Label { Location = new Point(xR + 70, y + 2), AutoSize = true };
        Controls.Add(_lblSwimAngle);
        y += rh;

                _chkIndependent = AddCb(xL, y, "Independent scenes per monitor"); y += rh;
        _chkBattery = AddCb(xL, y, "Pause on battery"); y += rh + 2;

        AddLabel(xL, y, "Target FPS:");
        _cmbFps = new ComboBox { Location = new Point(xR, y + 1), DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
        _cmbFps.Items.AddRange(new object[] { "Auto", "30", "50", "60", "100", "120" });
        Controls.Add(_cmbFps);
        y += rh + 8;

        // ── Bubble emitters section ──
        var bubbleHeader = new Label { Text = "Bubble Emitters", Location = new Point(xL, y), AutoSize = true, Font = new Font(Font.FontFamily, 9f, FontStyle.Bold) };
        Controls.Add(bubbleHeader);
        y += 20;

        _btnAddEmitter = new Button { Text = "Add Emitter", Location = new Point(xL + 340, y - 18), Size = new Size(90, 22) };
        _btnAddEmitter.Click += (_, _) => AddEmitterRow();
        Controls.Add(_btnAddEmitter);
        y += 4;

        // Emitters are populated in LoadValues(); placeholder to reserve space
        y += 26; // will be adjusted after LoadValues populates rows

        // ── Species section ──
        var speciesHeader = new Label { Text = "Fish Species", Location = new Point(xL, y), AutoSize = true, Font = new Font(Font.FontFamily, 9f, FontStyle.Bold) };
        Controls.Add(speciesHeader);
        y += 20;

        // Build per-species rows from the sprite atlas
        SpriteAtlas atlas = SpriteAtlas.Instance;
        for (int i = 0; i < atlas.SpeciesCount; i++)
        {
            FishSpriteSet species = atlas.GetSpecies(i);
            var row = new SpeciesRow(species.Name);
            _speciesRows.Add(row);
            row.Location = new Point(xL, y);
            row.OnChanged += () => UpdatePreview();
            Controls.Add(row);
            y += 26;
        }
        y += 8;

        // ── Preview ──
        Controls.Add(new Label { Text = "Preview:", Location = new Point(xL, y), AutoSize = true });
        y += 18;
        _previewPanel = new Panel { Location = new Point(xL, y), Size = new Size(550, 100), BackColor = Color.Black, BorderStyle = BorderStyle.FixedSingle };
        Controls.Add(_previewPanel);
        y += 110;

        // ── Buttons ──
        _btnDefaults = new Button { Text = "Defaults", Location = new Point(xL, y), Size = new Size(75, 25) };
        _btnDefaults.Click += (_, _) => { _settings = SettingsData.Defaults; LoadValues(); };
        Controls.Add(_btnDefaults);

        _btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(425, y), Size = new Size(75, 25) };
        Controls.Add(_btnOk);
        _btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(508, y), Size = new Size(75, 25) };
        Controls.Add(_btnCancel);
        AcceptButton = _btnOk;
        CancelButton = _btnCancel;

        // Size to content — use ClientSize so all controls fit including non-client chrome
        ClientSize = new Size(580, y + 45);
        MinimumSize = Size;
    }

    NumericUpDown AddNud(int x, int y, int min, int max)
    {
        var nud = new NumericUpDown { Location = new Point(x, y + 2), Minimum = min, Maximum = max, Width = 60 };
        Controls.Add(nud);
        return nud;
    }

    NumericUpDown AddNud(int x, int y, float min, float max)
    {
        var nud = new NumericUpDown { Location = new Point(x, y + 2), Minimum = (decimal)min, Maximum = (decimal)max, Width = 60 };
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
        _nudSwimAngle.Value = (decimal)_settings.SwimAngle;
        _lblSwimAngle.Text = $"{_settings.SwimAngle:F1}°";

        // Load per-emitter bubble values — rebuild dynamic rows
        RebuildEmitterRows(_settings.BubbleEmitters);

        _chkIndependent.Checked = _settings.IndependentScenesPerMonitor;
        _chkBattery.Checked = _settings.PauseOnBattery;
        _cmbFps.SelectedItem = _settings.TargetFps <= 0
            ? "Auto"
            : _settings.TargetFps.ToString();

        // Load per-species values
        foreach (var row in _speciesRows)
        {
            var cfg = _settings.GetSpeciesConfig(row.Name);
            row.Speed = cfg.Speed;
            row.Scale = cfg.Scale;
        }

        UpdatePreview();
    }

    void RebuildEmitterRows(BubbleEmitterConfig[] configs)
    {
        // Remove existing rows
        foreach (var row in _bubbleEmitterRows)
        {
            Controls.Remove(row);
            row.Dispose();
        }
        _bubbleEmitterRows.Clear();

        // Find the Y position where emitter rows start (after the "Bubble Emitters" header + Add button)
        // We need to compute the correct Y. Walk controls to find the species header's Y - 26.
        int startY = FindEmitterStartY();

        for (int i = 0; i < configs.Length; i++)
        {
            var cfg = configs[i];
            var row = new BubbleEmitterRow(i + 1);
            row.X = cfg.X;
            row.Y = cfg.Y;
            row.Speed = cfg.Speed;
            row.SizeMax = cfg.SizeMax;
            row.SizeMin = cfg.SizeMin;
            row.EmitterEnabled = cfg.Enabled;
            row.OnChanged += () => UpdatePreview();
            row.OnRemove += () => RemoveEmitterRow(row);
            _bubbleEmitterRows.Add(row);
            row.Location = new Point(15, startY);
            Controls.Add(row);
            startY += 26;
        }

        // Reposition controls below emitters
        RepositionBelowEmitters(startY);

        UpdateAddButtonState();
    }

    int FindEmitterStartY()
    {
        // The Add button is at y-18 from the header y. We need the row start after it.
        // Walk controls to find the species header and work backwards.
        foreach (Control ctrl in Controls)
        {
            if (ctrl is Label lbl && lbl.Text == "Fish Species")
            {
                // Emitter rows end 8px before species header
                return lbl.Location.Y - 8 - (_bubbleEmitterRows.Count > 0 ? _bubbleEmitterRows.Count * 26 : 26);
            }
        }
        // Fallback: compute from Add button position
        return _btnAddEmitter.Location.Y + 24;
    }

    void RepositionBelowEmitters(int afterEmitterY)
    {
        // Find the species header and everything below it, shift down
        Control? speciesHeader = null;
        foreach (Control ctrl in Controls)
        {
            if (ctrl is Label lbl && lbl.Text == "Fish Species")
            {
                speciesHeader = ctrl;
                break;
            }
        }
        if (speciesHeader == null) return;

        int oldSpeciesY = speciesHeader.Location.Y;
        int newSpeciesY = afterEmitterY + 8;
        int deltaY = newSpeciesY - oldSpeciesY;
        if (deltaY == 0) return;

        // Move all controls that are at or below speciesHeader
        foreach (Control ctrl in Controls.Cast<Control>().ToArray())
        {
            if (ctrl.Location.Y >= oldSpeciesY)
            {
                ctrl.Location = new Point(ctrl.Location.X, ctrl.Location.Y + deltaY);
            }
        }

        // Resize form
        ClientSize = new Size(ClientSize.Width, ClientSize.Height + deltaY);
    }

    void AddEmitterRow()
    {
        if (_bubbleEmitterRows.Count >= SettingsData.MaxEmitters) return;

        var cfg = new BubbleEmitterConfig();
        var row = new BubbleEmitterRow(_bubbleEmitterRows.Count + 1);
        row.EmitterEnabled = true;
        row.OnChanged += () => UpdatePreview();
        row.OnRemove += () => RemoveEmitterRow(row);

        // Find insertion Y
        int insertY = _bubbleEmitterRows.Count > 0
            ? _bubbleEmitterRows[^1].Location.Y + 26
            : FindEmitterStartY();

        row.Location = new Point(15, insertY);
        _bubbleEmitterRows.Add(row);
        Controls.Add(row);

        RepositionBelowEmitters(insertY + 26);
        UpdateAddButtonState();
        UpdatePreview();
    }

    void RemoveEmitterRow(BubbleEmitterRow row)
    {
        if (_bubbleEmitterRows.Count <= SettingsData.MinEmitters) return;

        int idx = _bubbleEmitterRows.IndexOf(row);
        _bubbleEmitterRows.RemoveAt(idx);
        Controls.Remove(row);
        row.Dispose();

        // Re-index labels
        for (int i = 0; i < _bubbleEmitterRows.Count; i++)
        {
            _bubbleEmitterRows[i].Index = i + 1;
        }

        // Reposition remaining rows
        int startY = FindEmitterStartY();
        for (int i = 0; i < _bubbleEmitterRows.Count; i++)
        {
            _bubbleEmitterRows[i].Location = new Point(15, startY + i * 26);
        }

        RepositionBelowEmitters(startY + _bubbleEmitterRows.Count * 26);
        UpdateAddButtonState();
        UpdatePreview();
    }

    void UpdateAddButtonState()
    {
        _btnAddEmitter.Enabled = _bubbleEmitterRows.Count < SettingsData.MaxEmitters;
    }

    void UpdatePreview()
    {
        // Build settings from current UI state
        var previewSettings = BuildSettingsFromUi();

        _previewScene?.Dispose();
        _previewScene = new Scene(42, previewSettings, _previewPanel.ClientSize);

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

    SettingsData BuildSettingsFromUi()
    {
        var s = new SettingsData
        {
            SwimAngle = (float)_nudSwimAngle.Value,
            IndependentScenesPerMonitor = _chkIndependent.Checked,
            PauseOnBattery = _chkBattery.Checked,
        };

        string selectedFps = _cmbFps.SelectedItem?.ToString() ?? "Auto";
        s.TargetFps = selectedFps == "Auto" ? 0 : int.Parse(selectedFps);

        // Collect per-emitter bubble configs from UI rows
        s.BubbleEmitters = _bubbleEmitterRows.Select(row => new BubbleEmitterConfig
        {
            X = row.X,
            Y = row.Y,
            Speed = row.Speed,
            SizeMin = row.SizeMin,
            SizeMax = row.SizeMax,
            Enabled = row.EmitterEnabled,
        }).ToArray();

        // Collect per-species configs from UI rows
        foreach (var row in _speciesRows)
        {
            var cfg = new SpeciesConfig
            {
                Name = row.Name,
                Speed = row.Speed,
                Scale = row.Scale,
            };
            s.SpeciesConfigs[row.Name] = cfg;
        }

        return s.Clamp();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
        {
            var finalSettings = BuildSettingsFromUi();
            Settings.Save(finalSettings);
        }
        base.OnFormClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _previewTimer?.Stop(); _previewTimer?.Dispose(); _previewScene?.Dispose();
        base.OnClosed(e);
    }

    // ── BubbleEmitterRow — per-emitter bubble config row ────────────────────
    private class BubbleEmitterRow : Panel
    {
        public int Index
        {
            get => int.Parse(_lblName.Text.Replace("Emitter ", ""));
            set => _lblName.Text = $"Emitter {value}";
        }

        public float X
        {
            get => (float)_nudX.Value;
            set => _nudX.Value = (decimal)Math.Clamp(value, BubbleEmitterConfig.XMin, BubbleEmitterConfig.XMax);
        }
        public float Y
        {
            get => (float)_nudY.Value;
            set => _nudY.Value = (decimal)Math.Clamp(value, BubbleEmitterConfig.YMin, BubbleEmitterConfig.YMax);
        }
        public float Speed
        {
            get => (float)_nudSpeed.Value;
            set => _nudSpeed.Value = (decimal)Math.Clamp(value, BubbleEmitterConfig.SpeedMin, BubbleEmitterConfig.SpeedMax);
        }
        public float SizeMin
        {
            get => (float)_nudSizeMin.Value;
            set => _nudSizeMin.Value = (decimal)Math.Clamp(value, BubbleEmitterConfig.SizeMinMin, BubbleEmitterConfig.SizeMinMax);
        }
        public float SizeMax
        {
            get => (float)_nudSizeMax.Value;
            set => _nudSizeMax.Value = (decimal)Math.Clamp(value, BubbleEmitterConfig.SizeMinMin, BubbleEmitterConfig.SizeMinMax);
        }
        public bool EmitterEnabled
        {
            get => _chkEnabled.Checked;
            set => _chkEnabled.Checked = value;
        }

        public event Action? OnChanged;
        public event Action? OnRemove;

        private readonly CheckBox _chkEnabled;
        private readonly Label _lblName;
        private readonly NumericUpDown _nudX;
        private readonly NumericUpDown _nudY;
        private readonly NumericUpDown _nudSpeed;
        private readonly NumericUpDown _nudSizeMin;
        private readonly NumericUpDown _nudSizeMax;
        private readonly Button _btnRemove;

        public BubbleEmitterRow(int index)
        {
            Size = new Size(450, 24);
            BackColor = SystemColors.Control;
            DoubleBuffered = true;

            _chkEnabled = new CheckBox { Location = new Point(2, 3), AutoSize = true, Checked = true };
            _chkEnabled.CheckedChanged += (_, _) => OnChanged?.Invoke();
            Controls.Add(_chkEnabled);

            _lblName = new Label { Text = $"Emitter {index}", Location = new Point(30, 5), AutoSize = true };
            Controls.Add(_lblName);

            var lblX = new Label { Text = "X:", Location = new Point(100, 3), AutoSize = true };
            Controls.Add(lblX);
            _nudX = new NumericUpDown
            {
                Location = new Point(112, 1),
                Minimum = (decimal)BubbleEmitterConfig.XMin,
                Maximum = (decimal)BubbleEmitterConfig.XMax,
                DecimalPlaces = 0,
                Increment = 1m,
                Width = 50
            };
            _nudX.ValueChanged += (_, _) => OnChanged?.Invoke();
            Controls.Add(_nudX);

            var lblY = new Label { Text = "Y:", Location = new Point(168, 3), AutoSize = true };
            Controls.Add(lblY);
            _nudY = new NumericUpDown
            {
                Location = new Point(180, 1),
                Minimum = (decimal)BubbleEmitterConfig.YMin,
                Maximum = (decimal)BubbleEmitterConfig.YMax,
                DecimalPlaces = 0,
                Increment = 1m,
                Width = 50
            };
            _nudY.ValueChanged += (_, _) => OnChanged?.Invoke();
            Controls.Add(_nudY);

            var lblSpeed = new Label { Text = "Speed:", Location = new Point(236, 3), AutoSize = true };
            Controls.Add(lblSpeed);
            _nudSpeed = new NumericUpDown
            {
                Location = new Point(278, 1),
                Minimum = (decimal)BubbleEmitterConfig.SpeedMin,
                Maximum = (decimal)BubbleEmitterConfig.SpeedMax,
                DecimalPlaces = 1,
                Increment = 0.1m,
                Width = 50
            };
            _nudSpeed.ValueChanged += (_, _) => OnChanged?.Invoke();
            Controls.Add(_nudSpeed);

            var lblSizeMin = new Label { Text = "SzMin:", Location = new Point(334, 3), AutoSize = true };
            Controls.Add(lblSizeMin);
            _nudSizeMin = new NumericUpDown
            {
                Location = new Point(378, 1),
                Minimum = (decimal)BubbleEmitterConfig.SizeMinMin,
                Maximum = (decimal)BubbleEmitterConfig.SizeMinMax,
                DecimalPlaces = 0,
                Increment = 1m,
                Width = 45
            };
            _nudSizeMin.ValueChanged += (_, _) => ValidateSizeAndNotify();
            Controls.Add(_nudSizeMin);

            var lblSizeMax = new Label { Text = "SzMax:", Location = new Point(428, 3), AutoSize = true };
            Controls.Add(lblSizeMax);
            _nudSizeMax = new NumericUpDown
            {
                Location = new Point(475, 1),
                Minimum = (decimal)BubbleEmitterConfig.SizeMinMin,
                Maximum = (decimal)BubbleEmitterConfig.SizeMinMax,
                DecimalPlaces = 0,
                Increment = 1m,
                Width = 45
            };
            _nudSizeMax.ValueChanged += (_, _) => ValidateSizeAndNotify();
            Controls.Add(_nudSizeMax);

            // Expand panel width to fit all controls
            Size = new Size(530, 24);

            _btnRemove = new Button { Text = "\u2715", Location = new Point(530, 1), Size = new Size(22, 22) };
            _btnRemove.Click += (_, _) => OnRemove?.Invoke();
            Controls.Add(_btnRemove);
            Size = new Size(555, 24);
        }

        private void ValidateSizeAndNotify()
        {
            // Swap if SizeMin > SizeMax
            if (_nudSizeMin.Value > _nudSizeMax.Value)
            {
                var tmp = _nudSizeMin.Value;
                _nudSizeMin.Value = _nudSizeMax.Value;
                _nudSizeMax.Value = tmp;
            }
            OnChanged?.Invoke();
        }
    }

    // ── SpeciesRow — per-species config row ─────────────────────────────────
    private class SpeciesRow : Panel
    {
        public new string Name { get; }
        public float Speed
        {
            get => (float)_nudSpeed.Value;
            set => _nudSpeed.Value = (decimal)Math.Clamp(value, SpeciesConfig.SpeedMin, SpeciesConfig.SpeedMax);
        }
        public new float Scale
        {
            get => (float)_nudScale.Value;
            set => _nudScale.Value = (decimal)Math.Clamp(value, SpeciesConfig.ScaleMin, SpeciesConfig.ScaleMax);
        }
        public event Action? OnChanged;

        private readonly NumericUpDown _nudSpeed;
        private readonly NumericUpDown _nudScale;

        public SpeciesRow(string name)
        {
            Name = name;
            Size = new Size(450, 24);
            BackColor = SystemColors.Control;
            DoubleBuffered = true;

            var displayName = name.Replace("-", " ");
            // Capitalize first letter of each word
            var words = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var title = string.Join(" ", words.Select(w => char.ToUpper(w[0]) + w[1..]));

            var lbl = new Label { Text = title + ":", Location = new Point(0, 3), AutoSize = true, Width = 120 };
            Controls.Add(lbl);

            var lblSpeed = new Label { Text = "Speed:", Location = new Point(130, 3), AutoSize = true };
            Controls.Add(lblSpeed);
            _nudSpeed = new NumericUpDown
            {
                Location = new Point(180, 1),
                Minimum = (decimal)SpeciesConfig.SpeedMin,
                Maximum = (decimal)SpeciesConfig.SpeedMax,
                DecimalPlaces = 3,
                Increment = 0.001m,
                Width = 70
            };
            _nudSpeed.ValueChanged += (_, _) => OnChanged?.Invoke();
            Controls.Add(_nudSpeed);

            var lblScale = new Label { Text = "Scale:", Location = new Point(260, 3), AutoSize = true };
            Controls.Add(lblScale);
            _nudScale = new NumericUpDown
            {
                Location = new Point(305, 1),
                Minimum = (decimal)SpeciesConfig.ScaleMin,
                Maximum = (decimal)SpeciesConfig.ScaleMax,
                DecimalPlaces = 2,
                Increment = 0.05m,
                Width = 70
            };
            _nudScale.ValueChanged += (_, _) => OnChanged?.Invoke();
            Controls.Add(_nudScale);
        }
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
