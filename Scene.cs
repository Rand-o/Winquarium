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

    private bool _disposed;

    /// <summary>
    /// Creates an independent local aquarium.
    ///
    /// Retained for Control Panel preview mode, configuration previews, and
    /// development windows.
    /// </summary>
    public Scene(
        int seed,
        SettingsData settings,
        Size size)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _localScene = true;

        _virtualBounds = new Rectangle(
            Point.Empty,
            EnsureValidSize(size));

        _viewportBounds = _virtualBounds;

        _world = new SharedAquarium(
            seed,
            settings);
    }

    /// <summary>
    /// Creates a viewport into the shared virtual-desktop aquarium.
    ///
    /// Pass Screen.Bounds as viewportBounds.
    /// </summary>
    public Scene(
        int seed,
        SettingsData settings,
        Size size,
        Rectangle viewportBounds)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _localScene = false;

        _virtualBounds = SystemInformation.VirtualScreen;
        _viewportBounds = viewportBounds;

        if (_virtualBounds.Width <= 0 ||
            _virtualBounds.Height <= 0)
        {
            _virtualBounds = new Rectangle(
                Point.Empty,
                EnsureValidSize(size));
        }

        if (_viewportBounds.Width <= 0 ||
            _viewportBounds.Height <= 0)
        {
            _viewportBounds = new Rectangle(
                _virtualBounds.Location,
                EnsureValidSize(size));
        }

        lock (SharedWorldLock)
        {
            _sharedWorld ??= new SharedAquarium(
                seed,
                settings);

            _world = _sharedWorld;
        }
    }

    /// <summary>
    /// Explicit constructor useful for testing custom virtual-monitor layouts.
    /// </summary>
    public Scene(
        int seed,
        SettingsData settings,
        Rectangle virtualBounds,
        Rectangle viewportBounds)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _localScene = false;

        _virtualBounds = virtualBounds;
        _viewportBounds = viewportBounds;

        if (_virtualBounds.Width <= 0 ||
            _virtualBounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(virtualBounds),
                "Virtual bounds must have a positive size.");
        }

        if (_viewportBounds.Width <= 0 ||
            _viewportBounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewportBounds),
                "Viewport bounds must have a positive size.");
        }

        lock (SharedWorldLock)
        {
            _sharedWorld ??= new SharedAquarium(
                seed,
                settings);

            _world = _sharedWorld;
        }
    }

    /// <summary>
    /// Updates the monitor and virtual-desktop mapping after a display change.
    /// </summary>
    public void SetViewport(
        Rectangle virtualBounds,
        Rectangle viewportBounds)
    {
        if (virtualBounds.Width <= 0 ||
            virtualBounds.Height <= 0)
        {
            return;
        }

        if (viewportBounds.Width <= 0 ||
            viewportBounds.Height <= 0)
        {
            return;
        }

        _virtualBounds = virtualBounds;
        _viewportBounds = viewportBounds;
    }

    /// <summary>
    /// Retained for compatibility with the existing render loop.
    ///
    /// Animation uses one shared Stopwatch, so several monitor windows cannot
    /// accidentally advance the simulation several times per frame.
    /// </summary>
    public void Update(double deltaTime)
    {
    }

    public void Draw(
        Graphics graphics,
        Size clientSize)
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);

        ArgumentNullException.ThrowIfNull(graphics);

        if (clientSize.Width <= 0 ||
            clientSize.Height <= 0)
        {
            return;
        }

        Rectangle virtualBounds;
        Rectangle viewportBounds;

        if (_localScene)
        {
            virtualBounds = new Rectangle(
                0,
                0,
                clientSize.Width,
                clientSize.Height);

            viewportBounds = virtualBounds;
        }
        else
        {
            virtualBounds = _virtualBounds;
            viewportBounds = _viewportBounds;
        }

        graphics.ResetTransform();
        graphics.ResetClip();

        graphics.CompositingMode =
            CompositingMode.SourceOver;

        graphics.CompositingQuality =
            CompositingQuality.HighQuality;

        graphics.InterpolationMode =
            InterpolationMode.HighQualityBicubic;

        graphics.PixelOffsetMode =
            PixelOffsetMode.HighQuality;

        graphics.SmoothingMode =
            SmoothingMode.AntiAlias;

        graphics.Clear(Color.Black);

        GraphicsState state = graphics.Save();

        try
        {
            if (!_localScene)
            {
                // Convert virtual-desktop positions into this monitor's local
                // client coordinates.
                graphics.TranslateTransform(
                    -viewportBounds.Left,
                    -viewportBounds.Top);

                // Clip to the visible viewport so we don't waste time
                // rendering off-screen content on large / multi-monitor setups.
                using var clipRegion = new Region(
                    new Rectangle(0, 0, clientSize.Width, clientSize.Height));
                graphics.SetClip(clipRegion, CombineMode.Replace);
            }

            _world.Draw(
                graphics,
                virtualBounds,
                viewportBounds);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static Size EnsureValidSize(Size size)
    {
        return new Size(
            Math.Max(1, size.Width),
            Math.Max(1, size.Height));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // The sprite atlas and full-screen aquarium are shared by several
        // monitor windows and must remain alive until process termination.
    }
}

internal sealed class SharedAquarium
{
    private readonly Stopwatch _clock =
        Stopwatch.StartNew();

    private readonly FishActor[] _fish;
    private readonly BubbleStream[] _bubbleStreams;

    private readonly float _speedMultiplier;

    public SharedAquarium(
        int seed,
        SettingsData settings)
    {
        SpriteAtlas atlas = SpriteAtlas.Instance;
        var random = new Random(seed);

        _speedMultiplier = Math.Clamp(
            settings.SpeedMultiplier,
            0.25f,
            3f);

        // One fish per species — always exactly speciesCount fish.
        // Each fish swims across the screen, rests off-screen for a while,
        // then re-enters with a fresh random path.
        int speciesCount = atlas.SpeciesCount;
        _fish = new FishActor[speciesCount];

        for (int i = 0; i < speciesCount; i++)
        {
            FishSpriteSet species = atlas.GetSpecies(i);

            float speedVariation =
                1.15f + random.NextSingle() * 0.50f;

            float effectiveSpeed = species.Speed * speedVariation;

            // How long (in seconds) the fish takes to cross the visible
            // screen width (normalised distance = 1.0).
            float swimDuration = 1.0f / effectiveSpeed;

            // Rest 0.8–1.6x the swim duration so each fish is visible
            // roughly 40–55% of the time.  With 7 fish this keeps about
            // 3 on-screen at any moment.
            float restMultiplier = 0.8f + random.NextSingle() * 0.8f;
            float restDuration = swimDuration * restMultiplier;

            float cyclePeriod = swimDuration + restDuration;

            // Where in the cycle the fish begins entering the screen.
            // Spread evenly with jitter so they don't all start together.
            float baseOffset = (i / (float)speciesCount) * cyclePeriod;
            float entryOffset = baseOffset + (random.NextSingle() - 0.5f) * restDuration * 0.5f;

            float scaleVariation =
                0.86f + random.NextSingle() * 0.30f;

            float depth = 0.42f + random.NextSingle() * 0.58f;

            // PathSeed is combined with the swim index at draw time to
            // produce per-swim random Y, direction, and drift values.
            int pathSeed = seed * 7919 + i * 104729 + random.Next(int.MaxValue);

            _fish[i] = new FishActor(
                i,                                      // species
                entryOffset,                            // cycle phase offset (seconds)
                cyclePeriod,                            // total swim + rest period (seconds)
                swimDuration,                           // time spent swimming (seconds)
                effectiveSpeed,                         // normalised-distance per second
                species.NominalScale * scaleVariation,  // scale
                depth,                                  // depth layer
                random.NextSingle() * MathF.Tau,        // animation phase
                pathSeed);                              // deterministic path seed
        }

        // Exactly 4 bubble streams: 2 from the left reef, 2 from the right reef.
        _bubbleStreams = new BubbleStream[4];

        // --- 2 left reef streams (asymmetric positions) ---
        _bubbleStreams[0] = new BubbleStream(
            0.04f + random.NextSingle() * 0.04f,
            0.45f + random.NextSingle() * 0.10f,
            0.42f + random.NextSingle() * 0.15f,
            random.NextSingle() * 20f,
            4 + random.Next(4));

        _bubbleStreams[1] = new BubbleStream(
            0.08f + random.NextSingle() * 0.05f,
            0.40f + random.NextSingle() * 0.12f,
            0.38f + random.NextSingle() * 0.18f,
            random.NextSingle() * 20f,
            3 + random.Next(5));

        // --- 2 right reef streams (asymmetric positions) ---
        _bubbleStreams[2] = new BubbleStream(
            0.92f - random.NextSingle() * 0.04f,
            0.42f + random.NextSingle() * 0.12f,
            0.40f + random.NextSingle() * 0.16f,
            random.NextSingle() * 20f,
            4 + random.Next(4));

        _bubbleStreams[3] = new BubbleStream(
            0.88f - random.NextSingle() * 0.05f,
            0.48f + random.NextSingle() * 0.10f,
            0.35f + random.NextSingle() * 0.20f,
            random.NextSingle() * 20f,
            3 + random.Next(5));
    }

    public void Draw(
        Graphics graphics,
        Rectangle virtualBounds,
        Rectangle viewportBounds)
    {
        float time =
            (float)_clock.Elapsed.TotalSeconds *
            _speedMultiplier;

        // Use the viewport (current monitor) height so each monitor sizes
        // its own content independently.
        float viewportHeight = viewportBounds.Height;

        // Keep fish and bubble sizes anchored to a 720p baseline so they
        // don't blow up on large screens.  A 720p screen gets scale 1.0,
        // 1080p gets ~1.1, 1440p gets ~1.2, 4K gets ~1.4.
        float screenScale = MathF.Pow(
            viewportHeight / 720f, 0.25f);

        float referenceHeight = 720f;

        DrawWater(
            graphics,
            virtualBounds);

        DrawFishLayer(
            graphics,
            virtualBounds,
            referenceHeight,
            screenScale,
            time,
            rearLayer: true);

        DrawCornerReefs(
            graphics,
            SpriteAtlas.Instance,
            new RectangleF(
                virtualBounds.Left,
                virtualBounds.Top,
                virtualBounds.Width,
                virtualBounds.Height));

        DrawFishLayer(
            graphics,
            virtualBounds,
            referenceHeight,
            screenScale,
            time,
            rearLayer: false);

        // Bubbles render in front of everything
        DrawBubbles(
            graphics,
            virtualBounds,
            referenceHeight,
            screenScale,
            time);
    }

    private static void DrawWater(
        Graphics graphics,
        Rectangle world)
    {
        using var water = new LinearGradientBrush(
            world,
            Color.FromArgb(255, 0, 6, 12),
            Color.FromArgb(255, 0, 0, 2),
            LinearGradientMode.Vertical);

        graphics.FillRectangle(
            water,
            world);

        using var glowPath = new GraphicsPath();

        glowPath.AddEllipse(
            world.Left + world.Width * 0.12f,
            world.Top - world.Height * 0.66f,
            world.Width * 0.76f,
            world.Height * 1.24f);

        using var glow =
            new PathGradientBrush(glowPath)
            {
                CenterColor =
                    Color.FromArgb(17, 8, 34, 49),

                SurroundColors =
                [
                    Color.FromArgb(0, 0, 0, 0)
                ]
            };

        graphics.FillPath(
            glow,
            glowPath);
    }

    private static void DrawCornerReefs(
        Graphics graphics,
        SpriteAtlas atlas,
        RectangleF worldBounds)
    {
        // Scale reefs to screen height, gently — they grow on large
        // displays but stay capped so they don't dominate the scene.
        float leftScale =
            MathF.Min(
                1.5f,
                worldBounds.Height * 0.58f /
                atlas.ReefLeft.Height);

        float rightScale =
            MathF.Min(
                1.5f,
                worldBounds.Height * 0.55f /
                atlas.ReefRight.Height);

        float leftWidth =
            atlas.ReefLeft.Width *
            leftScale;

        float leftHeight =
            atlas.ReefLeft.Height *
            leftScale;

        float rightWidth =
            atlas.ReefRight.Width *
            rightScale;

        float rightHeight =
            atlas.ReefRight.Height *
            rightScale;

        RectangleF leftDestination = new(
            worldBounds.Left,
            worldBounds.Bottom - leftHeight,
            leftWidth,
            leftHeight);

        // Pin the right reef flush to the right edge of the screen
        RectangleF rightDestination = new(
            worldBounds.Right - rightWidth,
            worldBounds.Bottom - rightHeight,
            rightWidth,
            rightHeight);

        // Ensure rightDestination.Right never extends past worldBounds.Right
        if (rightDestination.Right > worldBounds.Right + 1f)
        {
            rightDestination.X = worldBounds.Right - rightWidth;
        }

        graphics.DrawImage(
            atlas.ReefLeft,
            leftDestination);

        graphics.DrawImage(
            atlas.ReefRight,
            rightDestination);
    }

    private void DrawFishLayer(
        Graphics graphics,
        Rectangle world,
        float referenceHeight,
        float screenScale,
        float time,
        bool rearLayer)
    {
        SpriteAtlas atlas = SpriteAtlas.Instance;

        IEnumerable<FishActor> ordered =
            _fish.OrderBy(fish => fish.Depth);

        foreach (FishActor fish in ordered)
        {
            bool isRear = fish.Depth < 0.42f;
            if (isRear != rearLayer)
                continue;

            // --- Determine where the fish is in its swim/rest cycle ---
            // cycleTime runs from 0 to CyclePeriod, repeated.
            float cycleTime = PositiveModulo(
                time - fish.EntryOffset, fish.CyclePeriod);

            // During [0, SwimDuration] the fish is swimming across screen.
            // During [SwimDuration, CyclePeriod] the fish is resting off-screen.
            if (cycleTime >= fish.SwimDuration)
                continue; // fish is off-screen, skip drawing

            // Which swim number is this? Used to pick per-swim path params.
            int swimIndex = (int)MathF.Floor(
                (time - fish.EntryOffset) / fish.CyclePeriod);
            if (swimIndex < 0) swimIndex = 0;

            // --- Per-swim path parameters (deterministic from seed) ---
            int rngSeed = fish.PathSeed + swimIndex * 92821;
            float r0 = HashFloat(rngSeed);
            float r1 = HashFloat(rngSeed + 1);
            float r2 = HashFloat(rngSeed + 2);

            bool movesRight = r0 < 0.5f;
            float baseY = 0.10f + r1 * 0.55f;
            float yDrift = (r2 - 0.5f) * 0.18f;

            FishSpriteSet species = atlas.GetSpecies(fish.Species);

            float margin = species.IsStingray ? 0.19f : 0.14f;

            // Normalised progress 0..1 across the visible screen.
            // The fish enters at -margin and exits at 1+margin.
            float swimProgress = cycleTime / fish.SwimDuration;

            float normalizedX = movesRight
                ? -margin + swimProgress * (1f + margin * 2f)
                : 1f + margin - swimProgress * (1f + margin * 2f);

            // Skip if fully off-screen (safety check).
            if (normalizedX < -margin - 0.01f || normalizedX > 1f + margin + 0.01f)
                continue;

            // --- Vertical position: bob + drift ---
            float bobFrequency = species.IsStingray ? 0.40f : 0.72f;
            float bobAmount = species.IsStingray ? 0.008f : 0.012f;

            float bob = MathF.Sin(time * bobFrequency + fish.Phase) * bobAmount
                      + MathF.Sin(time * 0.21f + fish.Phase * 1.7f) * 0.007f;

            float yAngle = yDrift * swimProgress;

            float x = world.Left + normalizedX * world.Width;
            float y = world.Top + (baseY + bob + yAngle) * world.Height;

            // --- Size ---
            float depthScale = 0.70f + fish.Depth * 0.40f;
            float baseWidth = species.IsStingray ? 0.27f : 0.20f;
            float width = referenceHeight * baseWidth * fish.Scale * depthScale * screenScale;

            // --- Animation frame ---
            float framePosition = time * atlas.FrameRate
                                + fish.Phase / MathF.Tau * species.FrameCount;
            int frameIndex = (int)MathF.Floor(framePosition);

            Bitmap sprite = atlas.GetFishFrame(fish.Species, frameIndex);

            float aspectRatio = sprite.Height / (float)Math.Max(1, sprite.Width);
            float height = width * aspectRatio;

            bool flipHorizontally = species.FacesRight != movesRight;

            float opacity = isRear ? 0.70f : 1f;
            float brightness = isRear ? 0.78f : 1f;

            // Swim angle: the sprite tilts to match the drift direction.
            // yDrift is a small fraction of screen height, so we convert it
            // to a pixel-angle relative to the fish width.
            float swimAngle = MathF.Atan2(
                yDrift * world.Height,
                world.Width);

            DrawFish(graphics, sprite, x, y, width, height,
                flipHorizontally, brightness, opacity, swimAngle);
        }
    }

    /// <summary>
    /// Simple integer-to-float hash returning a value in [0, 1).
    /// </summary>
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

    private void DrawBubbles(
        Graphics graphics,
        Rectangle world,
        float referenceHeight,
        float screenScale,
        float time)
    {
        foreach (BubbleStream stream in _bubbleStreams)
        {
            for (int i = 0;
                 i < stream.Count;
                 i++)
            {
                float streamOffset =
                    i /
                    (float)stream.Count;

                float progress =
                    PositiveModulo(
                        time *
                        (0.083f + i * 0.004f) +
                        stream.Phase +
                        streamOffset,
                        1f);

                float sway =
                    MathF.Sin(
                        time * 1.75f +
                        i * 2.13f +
                        stream.Phase) *
                    referenceHeight *
                    0.006f;

                float x =
                    world.Left +
                    stream.X *
                    world.Width +
                    sway;

                float bottom =
                    world.Top +
                    stream.Bottom *
                    world.Height;

                float y =
                    bottom -
                    progress *
                    stream.Height *
                    world.Height;

                float diameter =
                    referenceHeight *
                    (0.006f +
                     (i % 3) * 0.0028f) *
                    (0.78f +
                     progress * 0.39f) *
                    1.5f *
                    screenScale;

                float opacity =
                    0.43f +
                    progress * 0.28f;

                DrawBubble(
                    graphics,
                    x,
                    y,
                    diameter,
                    opacity);
            }
        }
    }

    private static void DrawBubble(
        Graphics graphics,
        float x,
        float y,
        float diameter,
        float opacity)
    {
        diameter = Math.Max(
            3f,
            diameter);

        Bitmap sprite =
            SpriteAtlas.Instance
                .GetBubbleForDiameter(diameter);

        var destination = new Rectangle(
            (int)MathF.Round(
                x - diameter * 0.5f),

            (int)MathF.Round(
                y - diameter * 0.5f),

            Math.Max(
                1,
                (int)MathF.Round(diameter)),

            Math.Max(
                1,
                (int)MathF.Round(diameter)));

        using var attributes =
            new ImageAttributes();

        var colorMatrix =
            new ColorMatrix
            {
                Matrix00 = 1f,
                Matrix11 = 1f,
                Matrix22 = 1f,

                Matrix33 = Math.Clamp(
                    opacity,
                    0f,
                    1f),

                Matrix44 = 1f
            };

        attributes.SetColorMatrix(
            colorMatrix);

        graphics.DrawImage(
            sprite,
            destination,
            0,
            0,
            sprite.Width,
            sprite.Height,
            GraphicsUnit.Pixel,
            attributes);
    }

    private static void DrawFish(
        Graphics graphics,
        Bitmap sprite,
        float centerX,
        float centerY,
        float width,
        float height,
        bool flipHorizontally,
        float brightness,
        float opacity,
        float angle)
    {
        GraphicsState state =
            graphics.Save();

        try
        {
            graphics.TranslateTransform(
                centerX,
                centerY);

            if (angle != 0f)
            {
                graphics.RotateTransform(
                    angle * (180f / MathF.PI));
            }

            if (flipHorizontally)
            {
                graphics.ScaleTransform(
                    -1f,
                    1f);
            }

            var destination =
                new RectangleF(
                    -width * 0.5f,
                    -height * 0.5f,
                    width,
                    height);

            if (brightness >= 0.999f &&
                opacity >= 0.999f)
            {
                graphics.DrawImage(
                    sprite,
                    destination);

                return;
            }

            using var attributes =
                new ImageAttributes();

            var colorMatrix =
                new ColorMatrix
                {
                    Matrix00 = brightness,
                    Matrix11 = brightness,
                    Matrix22 = brightness,
                    Matrix33 = opacity,
                    Matrix44 = 1f
                };

            attributes.SetColorMatrix(
                colorMatrix);

            graphics.DrawImage(
                sprite,
                Rectangle.Round(destination),
                0,
                0,
                sprite.Width,
                sprite.Height,
                GraphicsUnit.Pixel,
                attributes);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static void DrawSprite(
        Graphics graphics,
        Bitmap sprite,
        RectangleF destination,
        float brightness,
        float opacity)
    {
        if (brightness >= 0.999f &&
            opacity >= 0.999f)
        {
            graphics.DrawImage(
                sprite,
                destination);

            return;
        }

        using var attributes =
            new ImageAttributes();

        var colorMatrix =
            new ColorMatrix
            {
                Matrix00 = brightness,
                Matrix11 = brightness,
                Matrix22 = brightness,
                Matrix33 = opacity,
                Matrix44 = 1f
            };

        attributes.SetColorMatrix(
            colorMatrix);

        graphics.DrawImage(
            sprite,
            Rectangle.Round(destination),
            0,
            0,
            sprite.Width,
            sprite.Height,
            GraphicsUnit.Pixel,
            attributes);
    }

    private static float PositiveModulo(
        float value,
        float modulus)
    {
        float result =
            value % modulus;

        return result < 0f
            ? result + modulus
            : result;
    }

    private readonly struct FishActor
    {
        public int Species { get; }

        /// <summary>
        /// Offset in seconds into the cycle where the fish begins entering.
        /// </summary>
        public float EntryOffset { get; }

        /// <summary>
        /// Total cycle period = swimDuration + restDuration (seconds).
        /// </summary>
        public float CyclePeriod { get; }

        /// <summary>
        /// How many seconds the fish spends swimming across the screen
        /// (not including the rest gap).
        /// </summary>
        public float SwimDuration { get; }

        /// <summary>
        /// Normalised-screen-distance travelled per second.
        /// </summary>
        public float Speed { get; }

        public float Scale { get; }
        public float Depth { get; }

        /// <summary>
        /// Phase offset for the tail-wag animation.
        /// </summary>
        public float Phase { get; }

        /// <summary>
        /// Seed combined with the swim index to deterministically produce
        /// per-swim path parameters (Y, direction, drift).
        /// </summary>
        public int PathSeed { get; }

        public FishActor(
            int species,
            float entryOffset,
            float cyclePeriod,
            float swimDuration,
            float speed,
            float scale,
            float depth,
            float phase,
            int pathSeed)
        {
            Species = species;
            EntryOffset = entryOffset;
            CyclePeriod = cyclePeriod;
            SwimDuration = swimDuration;
            Speed = speed;
            Scale = scale;
            Depth = depth;
            Phase = phase;
            PathSeed = pathSeed;
        }
    }

    private readonly struct BubbleStream
    {
        public float X { get; }
        public float Bottom { get; }
        public float Height { get; }
        public float Phase { get; }
        public int Count { get; }

        public BubbleStream(
            float x,
            float bottom,
            float height,
            float phase,
            int count)
        {
            X = x;
            Bottom = bottom;
            Height = height;
            Phase = phase;
            Count = count;
        }
    }
}

/// <summary>
/// Loads the generated PNG assets and keeps them in memory.
///
/// The atlas is included in Scene.cs so no additional C# source file is needed.
/// </summary>
internal sealed class SpriteAtlas
{
    private static readonly Lazy<SpriteAtlas> LazyInstance =
        new(
            () => new SpriteAtlas(),
            isThreadSafe: true);

    private readonly List<FishSpriteSet> _fish =
        new();

    private readonly List<Bitmap> _bubbles =
        new();

    public static SpriteAtlas Instance =>
        LazyInstance.Value;

    public string RootDirectory { get; }

    public float FrameRate { get; }

    public int SpeciesCount =>
        _fish.Count;

    public Bitmap ReefLeft { get; }
    public Bitmap ReefRight { get; }

    private SpriteAtlas()
    {
        RootDirectory =
            LocateSpriteDirectory();

        string manifestPath =
            Path.Combine(
                RootDirectory,
                "manifest.json");

        string json =
            File.ReadAllText(
                manifestPath);

        var jsonOptions =
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

        SpriteManifest manifest =
            JsonSerializer.Deserialize<SpriteManifest>(
                json,
                jsonOptions)
            ?? throw new InvalidDataException(
                "Sprites/manifest.json is empty or invalid.");

        FrameRate = manifest.FrameRate > 0f
            ? manifest.FrameRate
            : 8f;

        LoadFish(manifest);
        LoadBubbles(manifest);

        SpriteReefManifest reef =
            manifest.Reef
            ?? throw new InvalidDataException(
                "The sprite manifest does not contain a reef section.");

        ReefLeft =
            LoadRequiredBitmap(
                reef.Left);

        ReefRight =
            LoadRequiredBitmap(
                reef.Right);
    }

    public FishSpriteSet GetSpecies(
        int speciesIndex)
    {
        if (_fish.Count == 0)
        {
            throw new InvalidOperationException(
                "No fish species were loaded.");
        }

        speciesIndex =
            PositiveModulo(
                speciesIndex,
                _fish.Count);

        return _fish[speciesIndex];
    }

    public Bitmap GetFishFrame(
        int speciesIndex,
        int frameIndex)
    {
        FishSpriteSet species =
            GetSpecies(speciesIndex);

        if (species.FrameCount == 0)
        {
            throw new InvalidOperationException(
                $"Species '{species.Name}' has no frames.");
        }

        frameIndex =
            PositiveModulo(
                frameIndex,
                species.FrameCount);

        return species.Frames[frameIndex];
    }

    public Bitmap GetBubbleForDiameter(
        float diameter)
    {
        if (_bubbles.Count == 0)
        {
            throw new InvalidOperationException(
                "No bubble sprites were loaded.");
        }

        Bitmap closest =
            _bubbles[0];

        float closestDifference =
            MathF.Abs(
                GetVisibleBubbleDiameter(closest) -
                diameter);

        for (int i = 1;
             i < _bubbles.Count;
             i++)
        {
            Bitmap candidate =
                _bubbles[i];

            float difference =
                MathF.Abs(
                    GetVisibleBubbleDiameter(candidate) -
                    diameter);

            if (difference >= closestDifference)
                continue;

            closest = candidate;
            closestDifference = difference;
        }

        return closest;
    }

    private void LoadFish(
        SpriteManifest manifest)
    {
        foreach (SpriteFishManifest entry
                 in manifest.Fish ??
                    Array.Empty<SpriteFishManifest>())
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                continue;

            if (string.IsNullOrWhiteSpace(entry.Directory))
            {
                throw new InvalidDataException(
                    $"Fish '{entry.Name}' does not specify a directory.");
            }

            int frameCount =
                Math.Max(
                    1,
                    entry.Frames);

            var frames =
                new List<Bitmap>(frameCount);

            try
            {
                for (int frame = 0;
                     frame < frameCount;
                     frame++)
                {
                    string relativePath =
                        Path.Combine(
                            entry.Directory,
                            $"frame-{frame:00}.png");

                    frames.Add(
                        LoadRequiredBitmap(
                            relativePath));
                }
            }
            catch
            {
                foreach (Bitmap frame in frames)
                    frame.Dispose();

                throw;
            }

            float nominalScale =
                entry.NominalScale > 0f
                    ? Math.Clamp(
                        entry.NominalScale,
                        0.25f,
                        10f)
                    : 1f;

            float speed =
                entry.Speed > 0f
                    ? Math.Clamp(
                        entry.Speed,
                        0.002f,
                        0.15f)
                    : 0.02f;

            bool isStingray =
                string.Equals(
                    entry.Movement,
                    "stingray",
                    StringComparison.OrdinalIgnoreCase);

            _fish.Add(
                new FishSpriteSet(
                    entry.Name,
                    entry.FacesRight,
                    nominalScale,
                    speed,
                    isStingray,
                    frames));
        }

        if (_fish.Count == 0)
        {
            throw new InvalidDataException(
                "No fish entries were found in Sprites/manifest.json.");
        }
    }

    private void LoadBubbles(
        SpriteManifest manifest)
    {
        foreach (string relativePath
                 in manifest.Bubbles ??
                    Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            _bubbles.Add(
                LoadRequiredBitmap(
                    relativePath));
        }

        if (_bubbles.Count == 0)
        {
            throw new InvalidDataException(
                "No bubble entries were found in Sprites/manifest.json.");
        }

        _bubbles.Sort(
            (left, right) =>
                left.Width.CompareTo(
                    right.Width));
    }

    private Bitmap LoadRequiredBitmap(
        string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidDataException(
                "The sprite manifest contains an empty image path.");
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException(
                $"Sprite paths must be relative: {relativePath}");
        }

        string normalized =
            relativePath.Replace(
                '/',
                Path.DirectorySeparatorChar);

        string fullPath =
            Path.GetFullPath(
                Path.Combine(
                    RootDirectory,
                    normalized));

        string normalizedRoot =
            Path.GetFullPath(
                    RootDirectory)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Sprite path leaves the Sprites directory: {relativePath}");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Required aquarium sprite was not found: {relativePath}",
                fullPath);
        }

        // Clone into memory so the original PNG is not left locked.
        using var source =
            new Bitmap(fullPath);

        var result =
            new Bitmap(
                source.Width,
                source.Height,
                PixelFormat.Format32bppPArgb);

        result.SetResolution(
            96f,
            96f);

        using Graphics graphics =
            Graphics.FromImage(result);

        graphics.Clear(
            Color.Transparent);

        graphics.CompositingMode =
            CompositingMode.SourceCopy;

        graphics.CompositingQuality =
            CompositingQuality.HighQuality;

        graphics.InterpolationMode =
            InterpolationMode.HighQualityBicubic;

        graphics.PixelOffsetMode =
            PixelOffsetMode.HighQuality;

        graphics.DrawImageUnscaled(
            source,
            0,
            0);

        return result;
    }

    private static string LocateSpriteDirectory()
    {
        string baseDirectory =
            AppContext.BaseDirectory;

        string[] candidates =
        {
            Path.Combine(
                baseDirectory,
                "Sprites"),

            Path.Combine(
                Environment.CurrentDirectory,
                "Sprites"),

            Path.GetFullPath(
                Path.Combine(
                    baseDirectory,
                    "..",
                    "..",
                    "..",
                    "Sprites")),

            Path.GetFullPath(
                Path.Combine(
                    baseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "Sprites"))
        };

        foreach (string candidate in candidates)
        {
            string manifestPath =
                Path.Combine(
                    candidate,
                    "manifest.json");

            if (File.Exists(manifestPath))
            {
                return Path.GetFullPath(
                    candidate);
            }
        }

        string expectedPath =
            Path.Combine(
                baseDirectory,
                "Sprites",
                "manifest.json");

        throw new DirectoryNotFoundException(
            "The aquarium sprite directory was not found. " +
            $"Expected the manifest at '{expectedPath}'.");
    }

    private static float GetVisibleBubbleDiameter(
        Bitmap bitmap)
    {
        // The generator adds approximately six transparent pixels on each edge.
        return Math.Max(
            1f,
            bitmap.Width - 12f);
    }

    private static int PositiveModulo(
        int value,
        int modulus)
    {
        int result =
            value % modulus;

        return result < 0
            ? result + modulus
            : result;
    }

    private sealed class SpriteManifest
    {
        public float FrameRate { get; set; } =
            8f;

        public SpriteFishManifest[] Fish { get; set; } =
            Array.Empty<SpriteFishManifest>();

        public SpriteReefManifest? Reef { get; set; } =
            new();

        public string[] Bubbles { get; set; } =
            Array.Empty<string>();
    }

    private sealed class SpriteFishManifest
    {
        public string Name { get; set; } =
            string.Empty;

        public string Directory { get; set; } =
            string.Empty;

        public int Frames { get; set; } =
            12;

        public bool FacesRight { get; set; }

        public float NominalScale { get; set; } =
            1f;

        public float Speed { get; set; } =
            0.02f;

        public string Movement { get; set; } =
            "fish";
    }

    private sealed class SpriteReefManifest
    {
        public string Left { get; set; } =
            string.Empty;

        public string Right { get; set; } =
            string.Empty;
    }
}

internal sealed class FishSpriteSet
{
    public string Name { get; }

    /// <summary>
    /// Direction represented by the unmirrored generated PNG.
    /// </summary>
    public bool FacesRight { get; }

    public float NominalScale { get; }

    public float Speed { get; }

    public bool IsStingray { get; }

    public IReadOnlyList<Bitmap> Frames { get; }

    public int FrameCount =>
        Frames.Count;

    public FishSpriteSet(
        string name,
        bool facesRight,
        float nominalScale,
        float speed,
        bool isStingray,
        IReadOnlyList<Bitmap> frames)
    {
        Name = name;
        FacesRight = facesRight;
        NominalScale = nominalScale;
        Speed = speed;
        IsStingray = isStingray;
        Frames = frames;
    }
}
