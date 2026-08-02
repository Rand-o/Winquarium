using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Windows.Forms;

namespace AquariumSaver;

/// <summary>
/// Renders a sprite-based aquarium.
///
/// Preview windows use the three-argument constructor.
///
/// Full-screen monitor windows use the four-argument constructor and pass the
/// monitor's bounds in Windows virtual-desktop coordinates. All full-screen
/// instances then display different viewports into one shared aquarium.
/// </summary>
public sealed class Scene : IDisposable
{
    private static readonly object SharedWorldLock = new();
    private static SharedAquarium? _sharedWorld;

    private readonly SharedAquarium _world;
    private readonly bool _localScene;

    private Rectangle _virtualBounds;
    private Rectangle _viewportBounds;

    // Per-scene static background cache (water gradient + reefs).
    private Bitmap? _staticBackground;
    private Size _staticBackgroundSize;

    private bool _disposed;

    public Scene(int seed, SettingsData settings, Size size)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _localScene = true;
        _virtualBounds = new Rectangle(Point.Empty, EnsureValidSize(size));
        _viewportBounds = _virtualBounds;
        _world = new SharedAquarium(seed, settings);
    }

    public Scene(int seed, SettingsData settings, Size size, Rectangle viewportBounds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _localScene = false;
        _virtualBounds = SystemInformation.VirtualScreen;
        _viewportBounds = viewportBounds;

        if (_virtualBounds.Width <= 0 || _virtualBounds.Height <= 0)
            _virtualBounds = new Rectangle(Point.Empty, EnsureValidSize(size));
        if (_viewportBounds.Width <= 0 || _viewportBounds.Height <= 0)
            _viewportBounds = new Rectangle(_virtualBounds.Location, EnsureValidSize(size));

        lock (SharedWorldLock)
        {
            _sharedWorld ??= new SharedAquarium(seed, settings);
            _world = _sharedWorld;
        }
    }

    public Scene(int seed, SettingsData settings, Rectangle virtualBounds, Rectangle viewportBounds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _localScene = false;
        _virtualBounds = virtualBounds;
        _viewportBounds = viewportBounds;

        if (virtualBounds.Width <= 0 || virtualBounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(virtualBounds), "Virtual bounds must have a positive size.");
        if (viewportBounds.Width <= 0 || viewportBounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(viewportBounds), "Viewport bounds must have a positive size.");

        lock (SharedWorldLock)
        {
            _sharedWorld ??= new SharedAquarium(seed, settings);
            _world = _sharedWorld;
        }
    }

    public void SetViewport(Rectangle virtualBounds, Rectangle viewportBounds)
    {
        if (virtualBounds.Width <= 0 || virtualBounds.Height <= 0) return;
        if (viewportBounds.Width <= 0 || viewportBounds.Height <= 0) return;
        _virtualBounds = virtualBounds;
        _viewportBounds = viewportBounds;
    }

    public void Update(double deltaTime) => _world.Advance((float)deltaTime);

    public void Draw(Graphics graphics, Size clientSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(graphics);
        if (clientSize.Width <= 0 || clientSize.Height <= 0) return;

        Rectangle virtualBounds, viewportBounds;
        if (_localScene)
        {
            virtualBounds = new Rectangle(0, 0, clientSize.Width, clientSize.Height);
            viewportBounds = virtualBounds;
        }
        else
        {
            virtualBounds = _virtualBounds;
            viewportBounds = _viewportBounds;
        }

        graphics.ResetTransform();
        graphics.ResetClip();

        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.None;

        /*
         * Always begin with an opaque black frame. This is an inexpensive
         * safety guarantee even if an asset contains unexpected transparency.
         */
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.Clear(Color.Black);

        EnsureStaticBackground(clientSize);

        /*
         * The cached bitmap is now guaranteed opaque, so it may replace the
         * complete back buffer. DrawImageUnscaled ignores interpolation mode.
         */
        graphics.DrawImageUnscaled(_staticBackground!, 0, 0);

        graphics.CompositingMode = CompositingMode.SourceOver;

        GraphicsState state = graphics.Save();
        try
        {
            if (!_localScene)
            {
                graphics.SetClip(new Rectangle(0, 0, clientSize.Width, clientSize.Height), CombineMode.Replace);
                graphics.TranslateTransform(-viewportBounds.Left, -viewportBounds.Top, MatrixOrder.Append);
            }

            // Fish use virtual-desktop coords (shared across monitors)
            _world.DrawFish(graphics, virtualBounds, viewportBounds, alpha: 1.0f);

            if (!_localScene)
            {
                // Restore to client-local coords for bubbles
                graphics.Restore(state);
                state = graphics.Save();
            }

            // Bubbles use client-local coords (0,0) to (clientSize) — per-screen, like reefs
            float viewportHeight = clientSize.Height;
            float viewportScale = Math.Clamp(viewportHeight / 1080f, 0.5f, 3.0f);
            Rectangle clientBounds = new(0, 0, clientSize.Width, clientSize.Height);
            float time = _world.GetCurrentTime();
            _world.DrawBubbles(graphics, clientBounds, viewportScale, time);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private void EnsureStaticBackground(Size clientSize)
    {
        Size validSize = EnsureValidSize(clientSize);

        if (_staticBackground != null &&
            _staticBackgroundSize == validSize)
        {
            return;
        }

        Bitmap newBackground = new(
            validSize.Width,
            validSize.Height,
            PixelFormat.Format32bppPArgb);

        using (Graphics g = Graphics.FromImage(newBackground))
        {
            /*
             * First establish an opaque base. This guarantees that the
             * cached background can never expose the desktop.
             */
            g.CompositingMode = CompositingMode.SourceCopy;
            g.Clear(Color.Black);

            /*
             * Everything drawn after the opaque base must use SourceOver.
             * Transparent glow and reef pixels then preserve the water
             * underneath instead of punching transparent holes in it.
             */
            g.CompositingMode = CompositingMode.SourceOver;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle localBounds = new(
                0,
                0,
                validSize.Width,
                validSize.Height);

            SharedAquarium.DrawWaterBackground(
                g,
                localBounds);

            SharedAquarium.DrawReefBackground(
                g,
                SpriteAtlas.Instance,
                localBounds);
        }

        Bitmap? oldBackground = _staticBackground;

        _staticBackground = newBackground;
        _staticBackgroundSize = validSize;

        oldBackground?.Dispose();
    }

    private static Size EnsureValidSize(Size size) => new(Math.Max(1, size.Width), Math.Max(1, size.Height));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _staticBackground?.Dispose();
        _staticBackground = null;
    }
}

internal sealed class SharedAquarium
{
    private readonly object _advanceLock = new();
    private float _prevSimTime;
    private float _currSimTime;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly FishActor[] _fish;
    private readonly BubbleEmitter[] _bubbleEmitters;
    private readonly float _swimAngleRad;

    public SharedAquarium(int seed, SettingsData settings)
    {
        SpriteAtlas atlas = SpriteAtlas.Instance;
        var random = new Random(seed);
        _swimAngleRad = MathF.PI / 180f * Math.Clamp(settings.SwimAngle, SettingsData.SwimAngleMin, SettingsData.SwimAngleMax);

        int speciesCount = atlas.SpeciesCount;
        _fish = new FishActor[speciesCount];

        for (int i = 0; i < speciesCount; i++)
        {
            FishSpriteSet species = atlas.GetSpecies(i);
            SpeciesConfig cfg = settings.GetSpeciesConfig(species.Name);

            // Per-species speed from settings, with individual variation
            float speedVariation = 1.15f + random.NextSingle() * 0.50f;
            float effectiveSpeed = cfg.Speed * speedVariation;
            float swimDuration = 1.0f / effectiveSpeed;
            float restMultiplier = 0.8f + random.NextSingle() * 0.8f;
            float restDuration = swimDuration * restMultiplier;
            float cyclePeriod = swimDuration + restDuration;
            float baseOffset = (i / (float)speciesCount) * cyclePeriod;
            float entryOffset = baseOffset + (random.NextSingle() - 0.5f) * restDuration * 0.5f;

            // Per-species scale from settings, with individual variation
            float scaleVariation = 0.98f + random.NextSingle() * 0.22f;
            float depth = 0.42f + random.NextSingle() * 0.58f;
            int pathSeed = seed * 7919 + i * 104729 + random.Next(int.MaxValue);

            _fish[i] = new FishActor(i, entryOffset, cyclePeriod, swimDuration, effectiveSpeed,
                cfg.Scale * scaleVariation, depth, random.NextSingle() * MathF.Tau, pathSeed);
        }

        // Sort fish by depth once after construction — avoids per-frame LINQ allocation.
        Array.Sort(_fish, static (left, right) => left.Depth.CompareTo(right.Depth));

        // Build bubble emitters from settings
        BubbleEmitterConfig[] emitterConfigs = settings.BubbleEmitters.Length > 0
            ? settings.BubbleEmitters
            : [new BubbleEmitterConfig()]; // safety fallback: 1 default

        _bubbleEmitters = new BubbleEmitter[emitterConfigs.Length];
        for (int i = 0; i < emitterConfigs.Length; i++)
        {
            var cfg = emitterConfigs[i];
            _bubbleEmitters[i] = new BubbleEmitter(cfg, random);
        }
    }

    public void Advance(float elapsedSeconds)
    {
        lock (_advanceLock)
        {
            float absoluteTime = (float)_clock.Elapsed.TotalSeconds;
            _prevSimTime = _currSimTime;
            _currSimTime = absoluteTime;
        }
    }

    public float GetCurrentTime() => _currSimTime;

    /// <summary>Draw fish only — called in virtual-desktop translated coords.</summary>
    public void DrawFish(Graphics graphics, Rectangle virtualBounds, Rectangle viewportBounds, float alpha)
    {
        float prevSimTime, currSimTime;
        lock (_advanceLock)
        {
            prevSimTime = _prevSimTime;
            currSimTime = _currSimTime;
        }

        float time = prevSimTime + (currSimTime - prevSimTime) * alpha;
        float viewportHeight = viewportBounds.Height;
        float viewportScale = Math.Clamp(viewportHeight / 1080f, 0.5f, 3.0f);

        DrawFishLayer(graphics, virtualBounds, viewportScale, time, rearLayer: true);
        DrawFishLayer(graphics, virtualBounds, viewportScale, time, rearLayer: false);
    }

    // ── Public static background drawing methods ──

    internal static void DrawWaterBackground(Graphics graphics, Rectangle world)
    {
        using var water = new LinearGradientBrush(world,
            Color.FromArgb(255, 0, 6, 12), Color.FromArgb(255, 0, 0, 2), LinearGradientMode.Vertical);
        graphics.FillRectangle(water, world);

        using var glowPath = new GraphicsPath();
        glowPath.AddEllipse(world.Left + world.Width * 0.12f, world.Top - world.Height * 0.66f,
            world.Width * 0.76f, world.Height * 1.24f);

        using var glow = new PathGradientBrush(glowPath)
        {
            CenterColor = Color.FromArgb(17, 8, 34, 49),
            SurroundColors = [Color.FromArgb(0, 0, 0, 0)]
        };
        graphics.FillPath(glow, glowPath);
    }

    internal static void DrawReefBackground(Graphics graphics, SpriteAtlas atlas, Rectangle worldBounds)
    {
        RectangleF bounds = new(worldBounds.Left, worldBounds.Top, worldBounds.Width, worldBounds.Height);
        DrawCornerReefs(graphics, atlas, bounds);
    }

    private static void DrawCornerReefs(Graphics graphics, SpriteAtlas atlas, RectangleF worldBounds)
    {
        // Reduced from 49/47% to 38/36%, cap from 1.35 to 1.15.
        float leftScale = MathF.Min(1.15f, worldBounds.Height * 0.38f / atlas.ReefLeft.Height);
        float rightScale = MathF.Min(1.15f, worldBounds.Height * 0.36f / atlas.ReefRight.Height);

        float leftWidth = atlas.ReefLeft.Width * leftScale;
        float leftHeight = atlas.ReefLeft.Height * leftScale;
        float rightWidth = atlas.ReefRight.Width * rightScale;
        float rightHeight = atlas.ReefRight.Height * rightScale;

        RectangleF leftDestination = new(worldBounds.Left, worldBounds.Bottom - leftHeight, leftWidth, leftHeight);
        RectangleF rightDestination = new(worldBounds.Right - rightWidth, worldBounds.Bottom - rightHeight, rightWidth, rightHeight);

        if (rightDestination.Right > worldBounds.Right + 1f)
            rightDestination.X = worldBounds.Right - rightWidth;

        graphics.DrawImage(atlas.ReefLeft, leftDestination);
        graphics.DrawImage(atlas.ReefRight, rightDestination);
    }

    private void DrawFishLayer(Graphics graphics, Rectangle world, float viewportScale, float time, bool rearLayer)
    {
        SpriteAtlas atlas = SpriteAtlas.Instance;

        // Fish array is pre-sorted by depth — draw directly without LINQ.
        foreach (FishActor fish in _fish)
        {
            bool isRear = fish.Depth < 0.42f;
            if (isRear != rearLayer) continue;

            float cycleTime = PositiveModulo(time - fish.EntryOffset, fish.CyclePeriod);
            if (cycleTime >= fish.SwimDuration) continue;

            int swimIndex = (int)MathF.Floor((time - fish.EntryOffset) / fish.CyclePeriod);
            if (swimIndex < 0) swimIndex = 0;

            int rngSeed = fish.PathSeed + swimIndex * 92821;
            float r0 = HashFloat(rngSeed);
            float r1 = HashFloat(rngSeed + 1);
            float r2 = HashFloat(rngSeed + 2);

            bool movesRight = r0 < 0.5f;
            float baseY = 0.10f + r1 * 0.55f;
            float yDrift = (r2 - 0.5f) * 0.95f;

            FishSpriteSet species = atlas.GetSpecies(fish.Species);
            float margin = species.IsStingray ? 0.19f : 0.14f;
            float swimProgress = cycleTime / fish.SwimDuration;

            float normalizedX = movesRight
                ? -margin + swimProgress * (1f + margin * 2f)
                : 1f + margin - swimProgress * (1f + margin * 2f);

            if (normalizedX < -margin - 0.01f || normalizedX > 1f + margin + 0.01f) continue;

            float bobFrequency = species.IsStingray ? 0.40f : 0.72f;
            float bobAmount = species.IsStingray ? 0.008f : 0.012f;
            float bob = MathF.Sin(time * bobFrequency + fish.Phase) * bobAmount
                      + MathF.Sin(time * 0.21f + fish.Phase * 1.7f) * 0.007f;
            float yAngle = yDrift * swimProgress;

            float x = world.Left + normalizedX * world.Width;
            float y = world.Top + (baseY + bob + yAngle) * world.Height;

            // More predictable size formula anchored to 1080p.
            float targetWidthAt1080p = species.IsStingray ? 360f : 255f;
            float depthScale = 0.84f + fish.Depth * 0.30f;
            float width = targetWidthAt1080p * viewportScale * fish.Scale * depthScale;

            float tailBeatsPerSecond = species.IsStingray
                ? 0.75f + fish.Speed * 8f
                : 1.20f + fish.Speed * 14f;

            float framePosition = time * tailBeatsPerSecond * species.FrameCount
                                + fish.Phase / MathF.Tau * species.FrameCount;
            int frame0 = (int)MathF.Floor(framePosition);
            Bitmap sprite0 = atlas.GetFishFrame(fish.Species, frame0);

            float aspectRatio = sprite0.Height / (float)Math.Max(1, sprite0.Width);
            float height = width * aspectRatio;
            bool flipHorizontally = species.FacesRight != movesRight;
            float opacity = 1f;
            float brightness = 1f;

            // Swim angle: use the configured global swim angle, clamped by yDrift direction
            float swimAngle = MathF.Sign(yDrift) * _swimAngleRad * MathF.Min(MathF.Abs(yDrift), 1.0f);

            DrawFish(graphics, sprite0, x, y, width, height, flipHorizontally, brightness, opacity, swimAngle);
        }
    }

    private static float HashFloat(int seed)
    {
        uint x = (uint)seed;
        x = (x ^ 61) ^ (x >> 16);
        x = x * 9;
        x = x ^ (x >> 4);
        x = x * 0x27d4eb2d;
        x = x ^ (x >> 15);
        return (x & 0x7FFFFFFFu) / (float)0x7FFFFFFFu;
    }

    public void DrawBubbles(Graphics graphics, Rectangle viewport, float viewportScale, float time)
    {
        float referenceHeight = viewport.Height;

        foreach (BubbleEmitter emitter in _bubbleEmitters)
        {
            if (!emitter.Enabled) continue;

            // Advance emission: spawn new bubbles if it's time and cap not reached
            emitter.AdvanceEmission(time);

            // Draw active bubbles
            for (int i = emitter.ActiveBubbles.Count - 1; i >= 0; i--)
            {
                                var bubble = emitter.ActiveBubbles[i];

                // Compute float progress from spawn time
                float floatProgress = (time - bubble.SpawnTime) / bubble.Duration;

                // Remove expired bubbles
                if (floatProgress > 1.0f)
                {
                    emitter.ActiveBubbles.RemoveAt(i);
                    continue;
                }

                // Diameter with growth: 0.85x at spawn to 1.2x at exit
                float diameterAt1080p = bubble.SizeAt1080p * (0.85f + floatProgress * 0.35f);
                float diameter = diameterAt1080p * viewportScale;
                diameter = Math.Max(3f, diameter);

                // Horizontal position with sway — relative to this viewport (each screen independently)
                float sway = MathF.Sin(time * 1.75f + bubble.SwayPhase) * referenceHeight * 0.008f;
                float halfDiam = diameter * 0.5f;
                float x = viewport.Left + (emitter.X / 100f) * viewport.Width + sway;
                x = Math.Clamp(x, viewport.Left + halfDiam, viewport.Left + viewport.Width - halfDiam);

                // Vertical position: linear rise from emitter Y to top of screen
                float startY = viewport.Top + (emitter.Y / 100f) * viewport.Height;
                float y = startY - floatProgress * viewport.Height;

                // Opacity: fade in first 10%, full middle 70%, fade out last 20%
                float opacity;
                if (floatProgress < 0.1f)
                    opacity = floatProgress / 0.1f * 0.6f;
                else if (floatProgress > 0.8f)
                    opacity = (1.0f - floatProgress) / 0.2f * 0.6f;
                else
                    opacity = 0.6f;

                DrawBubble(graphics, x, y, diameter, opacity);
            }
        }
    }

    private static void DrawBubble(Graphics graphics, float x, float y, float diameter, float opacity)
    {
        diameter = Math.Max(3f, diameter);
        Bitmap sprite = SpriteAtlas.Instance.GetBubbleForDiameter(diameter);

        /*
         * Draw the bubble sprite stretched into a destination rectangle.
         * Avoid TranslateTransform/ScaleTransform entirely — those were
         * causing the bubble to render at the wrong screen position
         * (transform order issue with DrawImage(Image, Rectangle)).
         */
        float dstX = x - diameter * 0.5f;
        float dstY = y - diameter * 0.5f;
        graphics.DrawImage(sprite,
            dstX, dstY, diameter, diameter);
    }

    // Shared ImageAttributes to avoid per-frame GC allocation
    private static readonly ImageAttributes s_fishImageAttributes = new();
    private static readonly ColorMatrix s_fishColorMatrix = new();

    private static void DrawFish(Graphics graphics, Bitmap sprite, float centerX, float centerY,
        float width, float height, bool flipHorizontally, float brightness, float opacity, float angle)
    {
        // Fast path: no transform needed — skip Save/Restore overhead
        bool needsTransform = (angle != 0f) || flipHorizontally;
        bool needsColor = (brightness < 0.999f) || (opacity < 0.999f);

        if (!needsTransform && !needsColor)
        {
            graphics.DrawImage(sprite, centerX - width * 0.5f, centerY - height * 0.5f, width, height);
            return;
        }

        GraphicsState state = graphics.Save();
        try
        {
            graphics.TranslateTransform(centerX, centerY);
            if (angle != 0f) graphics.RotateTransform(angle * 180f / MathF.PI);
            if (flipHorizontally) graphics.ScaleTransform(-1f, 1f);

            var destination = new RectangleF(-width * 0.5f, -height * 0.5f, width, height);

            if (!needsColor)
            { graphics.DrawImage(sprite, destination); return; }

            // Reuse shared ImageAttributes/ColorMatrix — DrawImage copies values internally
            s_fishColorMatrix.Matrix00 = brightness;
            s_fishColorMatrix.Matrix11 = brightness;
            s_fishColorMatrix.Matrix22 = brightness;
            s_fishColorMatrix.Matrix33 = opacity;
            s_fishColorMatrix.Matrix44 = 1f;
            s_fishImageAttributes.SetColorMatrix(s_fishColorMatrix);

            graphics.DrawImage(sprite, Rectangle.Truncate(destination),
                0, 0, sprite.Width, sprite.Height, GraphicsUnit.Pixel, s_fishImageAttributes);
        }
        finally { graphics.Restore(state); }
    }

    private static float PositiveModulo(float value, float modulus)
    {
        float result = value % modulus;
        return result < 0f ? result + modulus : result;
    }

    private readonly struct FishActor
    {
        public int Species { get; }
        public float EntryOffset { get; }
        public float CyclePeriod { get; }
        public float SwimDuration { get; }
        public float Speed { get; }
        public float Scale { get; }
        public float Depth { get; }
        public float Phase { get; }
        public int PathSeed { get; }

        public FishActor(int species, float entryOffset, float cyclePeriod, float swimDuration,
            float speed, float scale, float depth, float phase, int pathSeed)
        {
            Species = species; EntryOffset = entryOffset; CyclePeriod = cyclePeriod;
            SwimDuration = swimDuration; Speed = speed; Scale = scale; Depth = depth;
            Phase = phase; PathSeed = pathSeed;
        }
    }

    // ── BubbleEmitter — runtime state for one configurable emitter ────────────

    private sealed class BubbleEmitter
    {
        public const int MaxActiveBubbles = 8;

        public float X { get; }           // 0-100 % from left
        public float Y { get; }           // 0-100 % from top
        public bool Enabled { get; }

        private float _duration;          // seconds for a bubble to float from Y to top
        private float _nextEmissionTime;  // simulated seconds when next bubble spawns
        private readonly float _sizeMin;
        private readonly float _sizeMax;
        private readonly Random _random;

        public readonly List<Bubble> ActiveBubbles = new(MaxActiveBubbles);

        public BubbleEmitter(BubbleEmitterConfig cfg, Random random)
        {
            X = cfg.X;
            Y = cfg.Y;
            _sizeMin = cfg.SizeMin;
            _sizeMax = cfg.SizeMax;
            Enabled = cfg.Enabled;
            _random = random;

            // Base ~8 seconds at speed 1.0; scaled inversely
            _duration = 8.0f / cfg.Speed;

            // Stagger first emission so emitters don't sync
            _nextEmissionTime = _random.NextSingle() * 1.0f;
        }

        public void AdvanceEmission(float currentTime)
        {
            if (!Enabled) return;

            if (currentTime >= _nextEmissionTime && ActiveBubbles.Count < MaxActiveBubbles)
            {
                // Spawn a new bubble
                float sizeRange = _sizeMax - _sizeMin;
                ActiveBubbles.Add(new Bubble
                {
                    SizeAt1080p = _sizeMin + sizeRange * _random.NextSingle(),
                    SwayPhase = _random.NextSingle() * MathF.Tau,
                    SpawnTime = currentTime,
                    Duration = _duration,
                });

                // Schedule next emission: random interval [0.3, 1.5] seconds
                _nextEmissionTime = currentTime + 0.3f + _random.NextSingle() * 1.2f;
            }
            else if (ActiveBubbles.Count >= MaxActiveBubbles)
            {
                // Cap reached — advance nextEmissionTime so we don't retry every frame
                _nextEmissionTime = currentTime + 0.3f;
            }
        }
    }

    // ── Bubble — individual particle ──────────────────────────────────────────

    private struct Bubble
    {
        public float SizeAt1080p;
        public float SwayPhase;
        public float SpawnTime;    // simulated seconds when this bubble was spawned
        public float Duration;     // total float time in seconds (8.0 / speed)
    }
}

/// <summary>
/// Loads the generated PNG assets and keeps them in memory.
/// </summary>
internal sealed class SpriteAtlas
{
    private static readonly Lazy<SpriteAtlas> LazyInstance =
        new(() => new SpriteAtlas(), isThreadSafe: true);

    private readonly List<FishSpriteSet> _fish = new();
    private Bitmap[] _bubbles = null!;               // sorted by visible diameter, ascending
    private float[] _bubbleDiameters = null!;        // precomputed visible diameters

    public static SpriteAtlas Instance => LazyInstance.Value;
    public string RootDirectory { get; }
    public float FrameRate { get; }
    public int SpeciesCount => _fish.Count;
    public Bitmap ReefLeft { get; }
    public Bitmap ReefRight { get; }

    private SpriteAtlas()
    {
        RootDirectory = LocateSpriteDirectory();
        string manifestPath = Path.Combine(RootDirectory, "manifest.json");
        string json = File.ReadAllText(manifestPath);

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        SpriteManifest manifest = JsonSerializer.Deserialize<SpriteManifest>(json, jsonOptions)
            ?? throw new InvalidDataException("Sprites/manifest.json is empty or invalid.");

        FrameRate = manifest.FrameRate > 0f ? manifest.FrameRate : 8f;
        LoadFishParallel(manifest);
        LoadBubbles(manifest);

        SpriteReefManifest reef = manifest.Reef
            ?? throw new InvalidDataException("The sprite manifest does not contain a reef section.");
        ReefLeft = LoadBitmapOptimized(reef.Left);
        ReefRight = LoadBitmapOptimized(reef.Right);
    }

    public FishSpriteSet GetSpecies(int speciesIndex)
    {
        if (_fish.Count == 0) throw new InvalidOperationException("No fish species were loaded.");
        speciesIndex = PositiveModulo(speciesIndex, _fish.Count);
        return _fish[speciesIndex];
    }

    public Bitmap GetFishFrame(int speciesIndex, int frameIndex)
    {
        FishSpriteSet species = GetSpecies(speciesIndex);
        if (species.FrameCount == 0) throw new InvalidOperationException($"Species '{species.Name}' has no frames.");
        frameIndex = PositiveModulo(frameIndex, species.FrameCount);
        return species.Frames[frameIndex];
    }

    /// <summary>Binary-search for the closest bubble sprite by precomputed visible diameter.</summary>
    public Bitmap GetBubbleForDiameter(float diameter)
    {
        int lo = 0, hi = _bubbleDiameters.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_bubbleDiameters[mid] < diameter) lo = mid + 1;
            else hi = mid;
        }
        // Check lo and lo-1 for the true closest
        if (lo > 0)
        {
            float diffLo = MathF.Abs(_bubbleDiameters[lo] - diameter);
            float diffPrev = MathF.Abs(_bubbleDiameters[lo - 1] - diameter);
            if (diffPrev <= diffLo) lo--;
        }
        return _bubbles[lo];
    }

    /// <summary>Load all fish species in parallel — each species' frames are loaded concurrently.</summary>
    private void LoadFishParallel(SpriteManifest manifest)
    {
        SpriteFishManifest[] entries = manifest.Fish ?? Array.Empty<SpriteFishManifest>();
        if (entries.Length == 0)
            throw new InvalidDataException("No fish entries were found in Sprites/manifest.json.");

        var loadedSpecies = new FishSpriteSet[entries.Length];

        Parallel.For(0, entries.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(entries.Length, Environment.ProcessorCount) }, i =>
        {
            SpriteFishManifest entry = entries[i];
            if (string.IsNullOrWhiteSpace(entry.Name)) return;
            if (string.IsNullOrWhiteSpace(entry.Directory))
                throw new InvalidDataException($"Fish '{entry.Name}' does not specify a directory.");

            int frameCount = Math.Max(1, entry.Frames);
            var frames = new Bitmap[frameCount];

            // Load frames sequentially per-species (I/O bound, sequential is fine on SSD)
            for (int frame = 0; frame < frameCount; frame++)
            {
                string relativePath = Path.Combine(entry.Directory, $"frame-{frame:00}.png");
                frames[frame] = LoadBitmapOptimized(relativePath);
            }

            float nominalScale = entry.NominalScale > 0f ? Math.Clamp(entry.NominalScale, 0.25f, 10f) : 1f;
            float speed = entry.Speed > 0f ? Math.Clamp(entry.Speed, 0.002f, 0.15f) : 0.02f;
            bool isStingray = string.Equals(entry.Movement, "stingray", StringComparison.OrdinalIgnoreCase);

            loadedSpecies[i] = new FishSpriteSet(entry.Name, entry.FacesRight, nominalScale, speed, isStingray, frames);
        });

        foreach (var species in loadedSpecies)
        {
            if (species != null) _fish.Add(species);
        }

        if (_fish.Count == 0)
            throw new InvalidDataException("No fish entries were loaded from Sprites/manifest.json.");
    }

    private void LoadBubbles(SpriteManifest manifest)
    {
        string[] paths = manifest.Bubbles ?? Array.Empty<string>();
        var loaded = new List<Bitmap>(paths.Length);

        foreach (string relativePath in paths)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) continue;
            loaded.Add(LoadBitmapOptimized(relativePath));
        }

        if (loaded.Count == 0)
            throw new InvalidDataException("No bubble entries were found in Sprites/manifest.json.");

        loaded.Sort((a, b) => a.Width.CompareTo(b.Width));
        _bubbles = loaded.ToArray();
        _bubbleDiameters = new float[_bubbles.Length];
        for (int i = 0; i < _bubbles.Length; i++)
            _bubbleDiameters[i] = Math.Max(1f, _bubbles[i].Width - 12f);
    }

    /// <summary>
    /// Optimized bitmap loader — uses Image.Clone() with target pixel format instead of
    /// a full Graphics draw pipeline. Clone() performs a near-native pixel-format conversion
    /// that is significantly faster than CreateGraphics + DrawImageUnscaled.
    /// </summary>
    private Bitmap LoadBitmapOptimized(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException("The sprite manifest contains an empty image path.");

        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Combine(RootDirectory, normalized));

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Required aquarium sprite was not found: {relativePath}", fullPath);

        // Clone into Format32bppPArgb — this is a native blit, not a GDI+ draw call
        using var source = new Bitmap(fullPath);
        Bitmap result = source.Clone(
            new Rectangle(0, 0, source.Width, source.Height),
            PixelFormat.Format32bppPArgb);

        if (result == null)
            throw new InvalidDataException($"Failed to convert sprite to PArgb format: {relativePath}");

        return result;
    }

    private static string LocateSpriteDirectory()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDirectory, "Sprites"),
            Path.Combine(Environment.CurrentDirectory, "Sprites"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "Sprites")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "Sprites"))
        };

        foreach (string candidate in candidates)
        {
            string manifestPath = Path.Combine(candidate, "manifest.json");
            if (File.Exists(manifestPath)) return Path.GetFullPath(candidate);
        }

        string expectedPath = Path.Combine(baseDirectory, "Sprites", "manifest.json");
        throw new DirectoryNotFoundException(
            "The aquarium sprite directory was not found. " +
            $"Expected the manifest at '{expectedPath}'.");
    }

    private static int PositiveModulo(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private sealed class SpriteManifest
    {
        public float FrameRate { get; set; } = 8f;
        public SpriteFishManifest[] Fish { get; set; } = Array.Empty<SpriteFishManifest>();
        public SpriteReefManifest? Reef { get; set; } = new();
        public string[] Bubbles { get; set; } = Array.Empty<string>();
    }

    private sealed class SpriteFishManifest
    {
        public string Name { get; set; } = string.Empty;
        public string Directory { get; set; } = string.Empty;
        public int Frames { get; set; } = 12;
        public bool FacesRight { get; set; }
        public float NominalScale { get; set; } = 1f;
        public float Speed { get; set; } = 0.02f;
        public string Movement { get; set; } = "fish";
    }

    private sealed class SpriteReefManifest
    {
        public string Left { get; set; } = string.Empty;
        public string Right { get; set; } = string.Empty;
    }
}

internal sealed class FishSpriteSet
{
    public string Name { get; }
    public bool FacesRight { get; }
    public float NominalScale { get; }
    public float Speed { get; }
    public bool IsStingray { get; }
    public IReadOnlyList<Bitmap> Frames { get; }
    public int FrameCount => Frames.Count;

    public FishSpriteSet(string name, bool facesRight, float nominalScale, float speed, bool isStingray, IReadOnlyList<Bitmap> frames)
    {
        Name = name; FacesRight = facesRight; NominalScale = nominalScale;
        Speed = speed; IsStingray = isStingray; Frames = frames;
    }
}
