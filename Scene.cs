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

        graphics.CompositingQuality =
            CompositingQuality.HighSpeed;

        graphics.InterpolationMode =
            InterpolationMode.HighQualityBilinear;

        graphics.PixelOffsetMode =
            PixelOffsetMode.HighQuality;

        graphics.SmoothingMode =
            SmoothingMode.None;

        /*
         * Always begin with an opaque black frame. This is an inexpensive
         * safety guarantee even if an asset contains unexpected transparency.
         */
        graphics.CompositingMode =
            CompositingMode.SourceCopy;

        graphics.Clear(Color.Black);

        EnsureStaticBackground(clientSize);

        /*
         * The cached bitmap is now guaranteed opaque, so it may replace the
         * complete back buffer.
         */
        graphics.DrawImageUnscaled(
            _staticBackground!,
            0,
            0);

        graphics.CompositingMode =
            CompositingMode.SourceOver;

        GraphicsState state = graphics.Save();
        try
        {
            if (!_localScene)
            {
                graphics.SetClip(new Rectangle(0, 0, clientSize.Width, clientSize.Height), CombineMode.Replace);
                graphics.TranslateTransform(-viewportBounds.Left, -viewportBounds.Top, MatrixOrder.Append);
            }

            _world.DrawForeground(graphics, virtualBounds, viewportBounds, alpha: 1.0f);
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
    private readonly BubbleStream[] _bubbleStreams;
    private readonly float _speedMultiplier;

    public SharedAquarium(int seed, SettingsData settings)
    {
        SpriteAtlas atlas = SpriteAtlas.Instance;
        var random = new Random(seed);
        _speedMultiplier = Math.Clamp(settings.SpeedMultiplier, 0.25f, 3f);

        int speciesCount = atlas.SpeciesCount;
        _fish = new FishActor[speciesCount];

        for (int i = 0; i < speciesCount; i++)
        {
            FishSpriteSet species = atlas.GetSpecies(i);
            float speedVariation = 1.15f + random.NextSingle() * 0.50f;
            float effectiveSpeed = species.Speed * speedVariation;
            float swimDuration = 1.0f / effectiveSpeed;
            float restMultiplier = 0.8f + random.NextSingle() * 0.8f;
            float restDuration = swimDuration * restMultiplier;
            float cyclePeriod = swimDuration + restDuration;
            float baseOffset = (i / (float)speciesCount) * cyclePeriod;
            float entryOffset = baseOffset + (random.NextSingle() - 0.5f) * restDuration * 0.5f;

            // Narrower, larger size variation for bigger fish.
            float scaleVariation = 0.98f + random.NextSingle() * 0.22f;
            float depth = 0.42f + random.NextSingle() * 0.58f;
            int pathSeed = seed * 7919 + i * 104729 + random.Next(int.MaxValue);

            _fish[i] = new FishActor(i, entryOffset, cyclePeriod, swimDuration, effectiveSpeed,
                species.NominalScale * scaleVariation, depth, random.NextSingle() * MathF.Tau, pathSeed);
        }

        // Sort fish by depth once after construction — avoids per-frame LINQ allocation.
        Array.Sort(_fish, static (left, right) => left.Depth.CompareTo(right.Depth));

        // Exactly 4 bubble streams: 2 bottom-left corner, 2 bottom-right corner.
        // Burst-based: a few bubbles appear, float up, then a pause before the next burst.
        _bubbleStreams = new BubbleStream[4];
        // Bottom-left streams
        _bubbleStreams[0] = new BubbleStream(0.02f + random.NextSingle() * 0.06f, 0.80f + random.NextSingle() * 0.08f,
            1.0f, random.NextSingle() * 20f, 3 + random.Next(3),
            burstInterval: 2.5f + random.NextSingle() * 2.0f, bubblesPerBurst: 3 + random.Next(3));
        _bubbleStreams[1] = new BubbleStream(0.01f + random.NextSingle() * 0.07f, 0.78f + random.NextSingle() * 0.10f,
            1.0f, random.NextSingle() * 20f, 3 + random.Next(3),
            burstInterval: 3.0f + random.NextSingle() * 2.0f, bubblesPerBurst: 3 + random.Next(3));
        // Bottom-right streams
        _bubbleStreams[2] = new BubbleStream(0.92f + random.NextSingle() * 0.06f, 0.80f + random.NextSingle() * 0.08f,
            1.0f, random.NextSingle() * 20f, 3 + random.Next(3),
            burstInterval: 2.5f + random.NextSingle() * 2.0f, bubblesPerBurst: 3 + random.Next(3));
        _bubbleStreams[3] = new BubbleStream(0.91f + random.NextSingle() * 0.07f, 0.78f + random.NextSingle() * 0.10f,
            1.0f, random.NextSingle() * 20f, 3 + random.Next(3),
            burstInterval: 3.0f + random.NextSingle() * 2.0f, bubblesPerBurst: 3 + random.Next(3));
    }

    public void Advance(float elapsedSeconds)
    {
        lock (_advanceLock)
        {
            float absoluteTime = (float)_clock.Elapsed.TotalSeconds * _speedMultiplier;
            _prevSimTime = _currSimTime;
            _currSimTime = absoluteTime;
        }
    }

    /// <summary>Draw only fish and bubbles — called by Scene.Draw() after its per-scene background.</summary>
    public void DrawForeground(Graphics graphics, Rectangle virtualBounds, Rectangle viewportBounds, float alpha)
    {
        float prevSimTime, currSimTime;
        lock (_advanceLock)
        {
            prevSimTime = _prevSimTime;
            currSimTime = _currSimTime;
        }

        float time = prevSimTime + (currSimTime - prevSimTime) * alpha;
        float viewportHeight = viewportBounds.Height;
        float viewportScale = Math.Clamp(viewportHeight / 1080f, 0.72f, 1.75f);

        DrawFishLayer(graphics, virtualBounds, viewportScale, time, rearLayer: true);
        DrawFishLayer(graphics, virtualBounds, viewportScale, time, rearLayer: false);
        DrawBubbles(graphics, viewportBounds, viewportScale, time);
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
        // Reduced from 58/55% to 49/47%, cap from 1.5 to 1.35.
        float leftScale = MathF.Min(1.35f, worldBounds.Height * 0.49f / atlas.ReefLeft.Height);
        float rightScale = MathF.Min(1.35f, worldBounds.Height * 0.47f / atlas.ReefRight.Height);

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

            float swimAngle = MathF.Atan2(yDrift * world.Height, world.Width);

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

    private void DrawBubbles(Graphics graphics, Rectangle world, float viewportScale, float time)
    {
        float referenceHeight = 1080f;

        foreach (BubbleStream stream in _bubbleStreams)
        {
            // Burst timing: figure out which burst cycle we're in and how far through it is.
            float burstCycle = time / stream.BurstInterval;
            int burstIndex = (int)MathF.Floor(burstCycle);
            float burstProgress = burstCycle - burstIndex; // 0..1 within the current burst interval

            // Bubbles are released at the start of each burst and spread out over the first part of the interval.
            float releaseWindow = 0.25f; // first 25% of the burst interval is the "release" phase

            for (int i = 0; i < stream.BubblesPerBurst; i++)
            {
                // Each bubble in the burst is released at a slightly different time.
                float releaseTime = (float)i / (float)stream.BubblesPerBurst * releaseWindow;
                // Add per-stream phase offset so the 4 streams don't all burst at once.
                float adjustedProgress = burstProgress + stream.Phase / MathF.Tau * 0.1f;

                // Only show this bubble if the current burst progress has reached its release time.
                if (adjustedProgress < releaseTime) continue;

                // Float progress: how far this bubble has risen since its release.
                float timeSinceRelease = adjustedProgress - releaseTime;
                // Bubble floats up over ~1.5 seconds of burst interval.
                float floatDuration = 1.5f / stream.BurstInterval;
                float floatProgress = MathF.Min(timeSinceRelease / floatDuration, 1.0f);

                // If the bubble has fully exited (float progress > 1), skip it.
                if (floatProgress > 1.0f) continue;

                float sway = MathF.Sin(time * 1.75f + i * 2.13f + stream.Phase) * referenceHeight * 0.008f;

                float x = world.Left + stream.X * world.Width + sway;
                // Start from the reef middle (stream.Bottom) and float to the top (0).
                float startY = world.Top + stream.Bottom * world.Height;
                float y = startY - floatProgress * stream.Height * world.Height;

                // Bigger bubbles: base diameter ~1.8-2.8% of reference height, growing as they rise.
                float diameter = referenceHeight * (0.018f + (i % 3) * 0.006f)
                    * (0.85f + floatProgress * 0.35f) * viewportScale;
                // Fade in quickly, fade out near the top.
                float opacity = floatProgress < 0.1f
                    ? floatProgress / 0.1f * 0.6f
                    : floatProgress > 0.8f
                        ? (1.0f - floatProgress) / 0.2f * 0.6f
                        : 0.6f;

                DrawBubble(graphics, x, y, diameter, opacity);
            }
        }
    }

    private static void DrawBubble(Graphics graphics, float x, float y, float diameter, float opacity)
    {
        diameter = Math.Max(3f, diameter);
        Bitmap sprite = SpriteAtlas.Instance.GetBubbleForDiameter(diameter);

        GraphicsState state = graphics.Save();
        try
        {
            graphics.TranslateTransform(x - diameter * 0.5f, y - diameter * 0.5f, MatrixOrder.Append);
            graphics.ScaleTransform(diameter / sprite.Width, diameter / sprite.Height, MatrixOrder.Append);

            /*
             * Use the alpha already contained in the PNG. Do not allocate
             * a new ImageAttributes object for every bubble every frame.
             */
            graphics.DrawImage(
                sprite,
                new Rectangle(
                    0,
                    0,
                    sprite.Width,
                    sprite.Height));
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static void DrawFish(Graphics graphics, Bitmap sprite, float centerX, float centerY,
        float width, float height, bool flipHorizontally, float brightness, float opacity, float angle)
    {
        GraphicsState state = graphics.Save();
        try
        {
            graphics.TranslateTransform(centerX, centerY);
            if (angle != 0f) graphics.RotateTransform(angle * 180f / MathF.PI);
            if (flipHorizontally) graphics.ScaleTransform(-1f, 1f);

            var destination = new RectangleF(-width * 0.5f, -height * 0.5f, width, height);

            if (brightness >= 0.999f && opacity >= 0.999f)
            { graphics.DrawImage(sprite, destination); return; }

            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix
            {
                Matrix00 = brightness, Matrix11 = brightness, Matrix22 = brightness,
                Matrix33 = opacity, Matrix44 = 1f
            });

            graphics.DrawImage(sprite, Rectangle.Truncate(destination),
                0, 0, sprite.Width, sprite.Height, GraphicsUnit.Pixel, attributes);
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

    private readonly struct BubbleStream
    {
        public float X { get; }
        public float Bottom { get; }
        public float Height { get; }
        public float Phase { get; }
        public int Count { get; }
        public float BurstInterval { get; }
        public int BubblesPerBurst { get; }

        public BubbleStream(float x, float bottom, float height, float phase, int count,
            float burstInterval = 2.0f, int bubblesPerBurst = 4)
        {
            X = x; Bottom = bottom; Height = height; Phase = phase; Count = count;
            BurstInterval = burstInterval; BubblesPerBurst = bubblesPerBurst;
        }
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
    private readonly List<Bitmap> _bubbles = new();

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
        LoadFish(manifest);
        LoadBubbles(manifest);

        SpriteReefManifest reef = manifest.Reef
            ?? throw new InvalidDataException("The sprite manifest does not contain a reef section.");
        ReefLeft = LoadRequiredBitmap(reef.Left);
        ReefRight = LoadRequiredBitmap(reef.Right);
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

    public Bitmap GetBubbleForDiameter(float diameter)
    {
        if (_bubbles.Count == 0) throw new InvalidOperationException("No bubble sprites were loaded.");
        Bitmap closest = _bubbles[0];
        float closestDiff = MathF.Abs(GetVisibleBubbleDiameter(closest) - diameter);

        for (int i = 1; i < _bubbles.Count; i++)
        {
            float diff = MathF.Abs(GetVisibleBubbleDiameter(_bubbles[i]) - diameter);
            if (diff < closestDiff) { closest = _bubbles[i]; closestDiff = diff; }
        }
        return closest;
    }

    private void LoadFish(SpriteManifest manifest)
    {
        foreach (SpriteFishManifest entry in manifest.Fish ?? Array.Empty<SpriteFishManifest>())
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            if (string.IsNullOrWhiteSpace(entry.Directory))
                throw new InvalidDataException($"Fish '{entry.Name}' does not specify a directory.");

            int frameCount = Math.Max(1, entry.Frames);
            var frames = new List<Bitmap>(frameCount);

            try
            {
                for (int frame = 0; frame < frameCount; frame++)
                {
                    string relativePath = Path.Combine(entry.Directory, $"frame-{frame:00}.png");
                    frames.Add(LoadRequiredBitmap(relativePath));
                }
            }
            catch
            {
                foreach (Bitmap frame in frames) frame.Dispose();
                throw;
            }

            float nominalScale = entry.NominalScale > 0f ? Math.Clamp(entry.NominalScale, 0.25f, 10f) : 1f;
            float speed = entry.Speed > 0f ? Math.Clamp(entry.Speed, 0.002f, 0.15f) : 0.02f;
            bool isStingray = string.Equals(entry.Movement, "stingray", StringComparison.OrdinalIgnoreCase);

            _fish.Add(new FishSpriteSet(entry.Name, entry.FacesRight, nominalScale, speed, isStingray, frames));
        }

        if (_fish.Count == 0)
            throw new InvalidDataException("No fish entries were found in Sprites/manifest.json.");
    }

    private void LoadBubbles(SpriteManifest manifest)
    {
        foreach (string relativePath in manifest.Bubbles ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(relativePath)) continue;
            _bubbles.Add(LoadRequiredBitmap(relativePath));
        }

        if (_bubbles.Count == 0)
            throw new InvalidDataException("No bubble entries were found in Sprites/manifest.json.");

        _bubbles.Sort((left, right) => left.Width.CompareTo(right.Width));
    }

    private Bitmap LoadRequiredBitmap(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException("The sprite manifest contains an empty image path.");
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"Sprite paths must be relative: {relativePath}");

        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Combine(RootDirectory, normalized));

        string normalizedRoot = Path.GetFullPath(RootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Sprite path leaves the Sprites directory: {relativePath}");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Required aquarium sprite was not found: {relativePath}", fullPath);

        using var source = new Bitmap(fullPath);
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
        result.SetResolution(96f, 96f);

        using Graphics graphics = Graphics.FromImage(result);
        graphics.Clear(Color.Transparent);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImageUnscaled(source, 0, 0);
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

    private static float GetVisibleBubbleDiameter(Bitmap bitmap) => Math.Max(1f, bitmap.Width - 12f);

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
