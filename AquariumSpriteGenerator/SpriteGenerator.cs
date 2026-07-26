using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AquariumSpriteGenerator;

internal static class Program
{
    private const int ExpectedSourceWidth = 1339;
    private const int ExpectedSourceHeight = 800;

    /*
     * Continuous (unquantized) poses for smooth swimming animation.
     * 30 FPS animation rate — enough unique poses per cycle.
     */
    private const int PlaybackFrameRate = 30;
    private const int FramePadding = 28;

    private const byte MinimumVisibleAlpha = 16;

    private static readonly CreatureDefinition[] Creatures =
    [
        new(
            Name: "yellow-butterflyfish",
            SourceBounds: new Rectangle(218, 152, 154, 94),
            FacesRight: true,
            NominalScale: 1.15f,
            Speed: 0.024f,
            Style: AnimationStyle.NormalFish,
            FrameCount: 30,
            TailHingeX: 29f,
            TailAmplitude: 7f,
            TailCompression: 0.14f,
            BodyFlex: 1.2f,
            SideFin: null),

        new(
            Name: "stingray",
            SourceBounds: new Rectangle(511, 53, 226, 108),
            FacesRight: false,
            NominalScale: 1.25f,
            Speed: 0.014f,
            Style: AnimationStyle.Stingray,
            FrameCount: 40,
            TailHingeX: 0f,
            TailAmplitude: 0f,
            TailCompression: 0f,
            BodyFlex: 0f,
            SideFin: null),

        new(
            Name: "blue-triggerfish",
            SourceBounds: new Rectangle(538, 190, 238, 172),
            FacesRight: true,
            NominalScale: 1.20f,
            Speed: 0.020f,
            Style: AnimationStyle.Triggerfish,
            FrameCount: 40,
            TailHingeX: 61f,
            TailAmplitude: 8f,
            TailCompression: 0.16f,
            BodyFlex: 1.8f,
            SideFin: null),

        new(
            Name: "blue-tang",
            SourceBounds: new Rectangle(992, 201, 164, 96),
            FacesRight: false,
            NominalScale: 1.12f,
            Speed: 0.029f,
            Style: AnimationStyle.NormalFish,
            FrameCount: 30,
            TailHingeX: 132f,
            TailAmplitude: 7f,
            TailCompression: 0.15f,
            BodyFlex: 1.2f,
            SideFin: null),

        new(
            Name: "moorish-idol",
            SourceBounds: new Rectangle(333, 293, 194, 134),
            FacesRight: true,
            NominalScale: 1.15f,
            Speed: 0.025f,
            Style: AnimationStyle.NormalFish,
            FrameCount: 30,
            TailHingeX: 62f,
            TailAmplitude: 7f,
            TailCompression: 0.15f,
            BodyFlex: 1.4f,
            SideFin: null),

        new(
            Name: "orange-butterflyfish",
            SourceBounds: new Rectangle(596, 387, 172, 108),
            FacesRight: true,
            NominalScale: 1.15f,
            Speed: 0.021f,
            Style: AnimationStyle.NormalFish,
            FrameCount: 30,
            TailHingeX: 37f,
            TailAmplitude: 7f,
            TailCompression: 0.15f,
            BodyFlex: 1.3f,
            SideFin: null),

        new(
            Name: "clown-triggerfish",
            SourceBounds: new Rectangle(674, 500, 169, 91),
            FacesRight: false,
            NominalScale: 1.13f,
            Speed: 0.023f,
            Style: AnimationStyle.Triggerfish,
            FrameCount: 40,
            TailHingeX: 137f,
            TailAmplitude: 7f,
            TailCompression: 0.16f,
            BodyFlex: 1.5f,
            SideFin: null)
    ];

    public static int Main(string[] args)
    {
        string sourcePath = args.Length > 0
            ? args[0]
            : "sprites.png";

        string outputDirectory = args.Length > 1
            ? args[1]
            : "Sprites";

        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Source image not found:");
            Console.Error.WriteLine(Path.GetFullPath(sourcePath));
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "Usage: dotnet run --configuration Release -- " +
                "sprites.png Sprites");

            return 1;
        }

        try
        {
            using var loaded = new Bitmap(sourcePath);
            using Bitmap sourceBitmap = ConvertToArgb(loaded);

            Console.WriteLine();
            Console.WriteLine(
                $"Loaded source: {sourceBitmap.Width}x{sourceBitmap.Height}");

            if (sourceBitmap.Width != ExpectedSourceWidth ||
                sourceBitmap.Height != ExpectedSourceHeight)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine(
                    $"This generator expects " +
                    $"{ExpectedSourceWidth}x{ExpectedSourceHeight}.");

                Console.Error.WriteLine(
                    $"Actual image size: " +
                    $"{sourceBitmap.Width}x{sourceBitmap.Height}");

                return 1;
            }

            PixelBuffer source =
                PixelBuffer.FromBitmap(sourceBitmap);

            RecreateOutputDirectory(outputDirectory);

            Console.WriteLine();
            Console.WriteLine("Generating retro fish animations...");

            foreach (CreatureDefinition creature in Creatures)
            {
                ExportCreature(
                    source,
                    creature,
                    outputDirectory);
            }

            Console.WriteLine();
            Console.WriteLine("Generating reef sprites...");

            ExportReefs(
                source,
                outputDirectory);

            Console.WriteLine();
            Console.WriteLine("Generating bubbles...");

            ExportBubbles(outputDirectory);
            ExportManifest(outputDirectory);
            ExportReadme(outputDirectory);

            Console.WriteLine();
            Console.WriteLine("Completed:");
            Console.WriteLine(Path.GetFullPath(outputDirectory));
            Console.WriteLine();

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Generation failed:");
            Console.Error.WriteLine(exception);
            Console.Error.WriteLine();

            return 1;
        }
    }

    private static void ExportCreature(
        PixelBuffer source,
        CreatureDefinition definition,
        string outputDirectory)
    {
        PixelBuffer extracted = ExtractSubject(
            source,
            definition.SourceBounds,
            keepLargestComponent: true);

        PixelBuffer? finLayer = null;
        PixelBuffer bodyLayer = extracted.Clone();

        if (definition.SideFin is not null)
        {
            byte[] finMask = CreatePolygonMask(
                extracted.Width,
                extracted.Height,
                definition.SideFin.Points);

            finLayer = CreateMaskedLayer(
                extracted,
                finMask);

            /*
             * Remove the fixed side fin and reconstruct the covered body.
             * This prevents a stationary duplicate from remaining beneath
             * the animated fin.
             */
            bodyLayer = RemoveFinAndReconstructBody(
                extracted,
                finMask,
                definition.FacesRight);
        }

        string creatureDirectory = Path.Combine(
            outputDirectory,
            "Fish",
            definition.Name);

        Directory.CreateDirectory(creatureDirectory);

        for (int frameIndex = 0;
             frameIndex < definition.FrameCount;
             frameIndex++)
        {
            float phase =
                frameIndex /
                (float)definition.FrameCount *
                MathF.Tau;

            PixelBuffer frame = definition.Style switch
            {
                AnimationStyle.NormalFish =>
                    RenderNormalFishFrame(
                        bodyLayer,
                        finLayer,
                        definition,
                        phase),

                AnimationStyle.Triggerfish =>
                    RenderTriggerfishFrame(
                        bodyLayer,
                        finLayer,
                        definition,
                        phase),

                AnimationStyle.Stingray =>
                    RenderStingrayFrame(
                        bodyLayer,
                        phase),

                _ => throw new InvalidOperationException(
                    "Unknown animation style.")
            };

            ClearLowAlpha(
                frame,
                MinimumVisibleAlpha);

            using Bitmap bitmap = frame.ToBitmap();

            bitmap.Save(
                Path.Combine(
                    creatureDirectory,
                    $"frame-{frameIndex:00}.png"),
                ImageFormat.Png);
        }

        /*
         * Preview uses the center pose rather than an extreme pose.
         */
        PixelBuffer preview = definition.Style switch
        {
            AnimationStyle.NormalFish =>
                RenderNormalFishFrame(
                    bodyLayer,
                    finLayer,
                    definition,
                    0f),

            AnimationStyle.Triggerfish =>
                RenderTriggerfishFrame(
                    bodyLayer,
                    finLayer,
                    definition,
                    0f),

            AnimationStyle.Stingray =>
                RenderStingrayFrame(
                    bodyLayer,
                    0f),

            _ => throw new InvalidOperationException()
        };

        using (Bitmap previewBitmap = preview.ToBitmap())
        {
            previewBitmap.Save(
                Path.Combine(
                    creatureDirectory,
                    "preview.png"),
                ImageFormat.Png);
        }

        Console.WriteLine(
            $"Generated {definition.FrameCount,2} poses: " +
            definition.Name);
    }

    private static PixelBuffer RenderNormalFishFrame(
        PixelBuffer body,
        PixelBuffer? fin,
        CreatureDefinition definition,
        float phase)
    {
        float stroke = MathF.Sin(phase);

        PixelBuffer result = WarpFishBody(
            body,
            definition,
            stroke,
            triggerWave: false,
            phase);

        if (fin is not null &&
            definition.SideFin is not null)
        {
            DrawFlappingFin(
                result,
                fin,
                definition.SideFin,
                phase + 0.35f);
        }

        return result;
    }

    private static PixelBuffer RenderTriggerfishFrame(
        PixelBuffer body,
        PixelBuffer? fin,
        CreatureDefinition definition,
        float phase)
    {
        float stroke = MathF.Sin(phase);

        PixelBuffer result = WarpFishBody(
            body,
            definition,
            stroke,
            triggerWave: true,
            phase);

        if (fin is not null &&
            definition.SideFin is not null)
        {
            DrawFlappingFin(
                result,
                fin,
                definition.SideFin,
                phase + 0.45f);
        }

        return result;
    }

    private static PixelBuffer WarpFishBody(
        PixelBuffer source,
        CreatureDefinition definition,
        float stroke,
        bool triggerWave,
        float phase)
    {
        int outputWidth =
            source.Width + FramePadding * 2;

        int outputHeight =
            source.Height + FramePadding * 2;

        PixelBuffer output = new(
            outputWidth,
            outputHeight);

        float hingeX =
            definition.TailHingeX;

        float rearDistance = definition.FacesRight
            ? Math.Max(1f, hingeX)
            : Math.Max(
                1f,
                source.Width - 1f - hingeX);

        float centerY =
            source.Height * 0.52f;

        for (int destinationY = 0;
             destinationY < outputHeight;
             destinationY++)
        {
            for (int destinationX = 0;
                 destinationX < outputWidth;
                 destinationX++)
            {
                float localX =
                    destinationX - FramePadding;

                float localY =
                    destinationY - FramePadding;

                float rearProgress = definition.FacesRight
                    ? (hingeX - localX) / rearDistance
                    : (localX - hingeX) / rearDistance;

                rearProgress = Math.Clamp(
                    rearProgress,
                    0f,
                    1f);

                float flexibleProgress =
                    SmoothStep(rearProgress);

                /*
                 * The complete rear body bends continuously. No tail piece
                 * is cut out, so there is no rotating seam at the tail root.
                 */
                float tailOffsetY =
                    stroke *
                    definition.TailAmplitude *
                    flexibleProgress *
                    flexibleProgress;

                float bodyFlexOffset =
                    stroke *
                    definition.BodyFlex *
                    MathF.Sin(
                        flexibleProgress *
                        MathF.PI);

                float compression =
                    1f -
                    definition.TailCompression *
                    MathF.Abs(stroke) *
                    flexibleProgress;

                compression = Math.Max(
                    compression,
                    0.68f);

                float sourceX;

                if (rearProgress > 0f)
                {
                    sourceX =
                        hingeX +
                        (localX - hingeX) /
                        compression;
                }
                else
                {
                    sourceX = localX;
                }

                float sourceY =
                    localY -
                    tailOffsetY -
                    bodyFlexOffset;

                if (triggerWave)
                {
                    float normalizedX =
                        sourceX /
                        Math.Max(
                            1f,
                            source.Width - 1f);

                    float edgeDistance =
                        MathF.Abs(
                            sourceY - centerY) /
                        Math.Max(
                            1f,
                            source.Height * 0.5f);

                    /*
                     * Only pixels close to the upper and lower silhouette
                     * receive the traveling triggerfish fin wave.
                     */
                    float edgeWeight = SmoothStep(
                        Math.Clamp(
                            (edgeDistance - 0.42f) / 0.48f,
                            0f,
                            1f));

                    float rearBias = definition.FacesRight
                        ? 1f - normalizedX
                        : normalizedX;

                    rearBias = SmoothStep(
                        Math.Clamp(
                            rearBias * 1.45f,
                            0f,
                            1f));

                    float wave = MathF.Sin(
                        phase * 2f -
                        normalizedX *
                        MathF.Tau *
                        1.35f);

                    sourceY -=
                        wave *
                        3.8f *
                        edgeWeight *
                        rearBias;
                }

                Color32 sampled =
                    source.SampleBilinear(
                        sourceX,
                        sourceY);

                output.Set(
                    destinationX,
                    destinationY,
                    sampled);
            }
        }

        return output;
    }

    private static void DrawFlappingFin(
        PixelBuffer destination,
        PixelBuffer fin,
        SideFinDefinition definition,
        float phase)
    {
        float stroke = MathF.Sin(phase);
        float cosine = MathF.Cos(phase);

        /*
         * A side fin becomes narrow when it turns edge-on.
         */
        float scaleX =
            0.80f +
            0.20f *
            MathF.Abs(cosine);

        float scaleY =
            0.18f +
            0.82f *
            MathF.Abs(cosine);

        float angleDegrees =
            stroke *
            definition.MaximumAngle;

        float angle =
            angleDegrees *
            MathF.PI /
            180f;

        float cosineAngle =
            MathF.Cos(angle);

        float sineAngle =
            MathF.Sin(angle);

        float pivotX =
            definition.Pivot.X;

        float pivotY =
            definition.Pivot.Y;

        float offsetX =
            FramePadding +
            stroke * 2f;

        float offsetY =
            FramePadding +
            MathF.Abs(stroke) * 1.5f;

        for (int y = 0;
             y < destination.Height;
             y++)
        {
            for (int x = 0;
                 x < destination.Width;
                 x++)
            {
                float transformedX =
                    x - offsetX - pivotX;

                float transformedY =
                    y - offsetY - pivotY;

                /*
                 * Inverse rotation.
                 */
                float rotatedX =
                    transformedX * cosineAngle +
                    transformedY * sineAngle;

                float rotatedY =
                    -transformedX * sineAngle +
                    transformedY * cosineAngle;

                float sourceX =
                    rotatedX /
                    scaleX +
                    pivotX;

                float sourceY =
                    rotatedY /
                    scaleY +
                    pivotY;

                Color32 finPixel =
                    fin.SampleBilinear(
                        sourceX,
                        sourceY);

                if (finPixel.A == 0)
                {
                    continue;
                }

                /*
                 * Slight darkening during the inward stroke helps the
                 * compressed pose read as the reverse side of the fin.
                 */
                float lighting =
                    0.80f +
                    0.20f *
                    MathF.Abs(cosine);

                finPixel = finPixel.WithBrightness(
                    lighting);

                destination.Blend(
                    x,
                    y,
                    finPixel);
            }
        }
    }

    private static PixelBuffer RenderStingrayFrame(
        PixelBuffer source,
        float phase)
    {
        int outputWidth =
            source.Width + FramePadding * 2;

        int outputHeight =
            source.Height + FramePadding * 2;

        PixelBuffer output = new(
            outputWidth,
            outputHeight);

        /*
         * The ray body is around the left half of this crop. The long thin
         * tail extends to the right and should not flap like a second wing.
         */
        const float bodyCenterX = 102f;
        const float bodyCenterY = 63f;
        const float wingEndX = 158f;

        float stroke = MathF.Sin(phase);

        float projectedWingScale =
            1f -
            0.18f *
            MathF.Abs(stroke);

        for (int destinationY = 0;
             destinationY < outputHeight;
             destinationY++)
        {
            for (int destinationX = 0;
                 destinationX < outputWidth;
                 destinationX++)
            {
                float localX =
                    destinationX - FramePadding;

                float localY =
                    destinationY - FramePadding;

                float tailProtection = 1f -
                    SmoothStep(
                        Math.Clamp(
                            (localX - 135f) /
                            Math.Max(
                                1f,
                                wingEndX - 135f),
                            0f,
                            1f));

                float horizontalDistance =
                    MathF.Abs(
                        localX - bodyCenterX);

                float verticalDistance =
                    MathF.Abs(
                        localY - bodyCenterY);

                float tipProgress =
                    Math.Clamp(
                        horizontalDistance / 94f,
                        0f,
                        1f);

                float verticalWingProgress =
                    Math.Clamp(
                        verticalDistance / 54f,
                        0f,
                        1f);

                float wingWeight =
                    SmoothStep(
                        tipProgress) *
                    SmoothStep(
                        verticalWingProgress) *
                    tailProtection;

                /*
                 * The apparent wing height contracts on the upstroke. Both
                 * wings remain connected to the complete ray silhouette.
                 */
                float scale =
                    1f -
                    (1f - projectedWingScale) *
                    wingWeight;

                scale = Math.Max(
                    scale,
                    0.70f);

                float sourceY =
                    bodyCenterY +
                    (localY - bodyCenterY) /
                    scale;

                /*
                 * A delayed wave travels from the center toward the broad
                 * edges. Upper and lower edges curl in opposite directions.
                 */
                float delayedPhase =
                    phase -
                    tipProgress *
                    1.35f;

                float edgeDirection =
                    localY < bodyCenterY
                        ? -1f
                        : 1f;

                float curl =
                    MathF.Sin(delayedPhase) *
                    7f *
                    wingWeight *
                    edgeDirection;

                sourceY -= curl;

                /*
                 * Very small fore/aft flex prevents the ray from looking
                 * like a vertically scaled sticker.
                 */
                float sourceX =
                    localX -
                    MathF.Sin(
                        delayedPhase + 0.65f) *
                    1.8f *
                    wingWeight;

                Color32 sampled =
                    source.SampleBilinear(
                        sourceX,
                        sourceY);

                output.Set(
                    destinationX,
                    destinationY,
                    sampled);
            }
        }

        return output;
    }

    private static PixelBuffer RemoveFinAndReconstructBody(
        PixelBuffer source,
        byte[] mask,
        bool facesRight)
    {
        PixelBuffer result =
            source.Clone();

        int preferredDirection =
            facesRight
                ? 1
                : -1;

        for (int y = 0;
             y < source.Height;
             y++)
        {
            for (int x = 0;
                 x < source.Width;
                 x++)
            {
                int position =
                    y * source.Width + x;

                if (mask[position] < 24)
                {
                    continue;
                }

                Color32 replacement =
                    FindBodyReplacement(
                        source,
                        mask,
                        x,
                        y,
                        preferredDirection);

                float maskAlpha =
                    mask[position] / 255f;

                Color32 original =
                    source.Get(x, y);

                result.Set(
                    x,
                    y,
                    Color32.Lerp(
                        original,
                        replacement,
                        maskAlpha));
            }
        }

        return result;
    }

    private static Color32 FindBodyReplacement(
        PixelBuffer source,
        byte[] mask,
        int x,
        int y,
        int preferredDirection)
    {
        for (int distance = 2;
             distance <= 24;
             distance++)
        {
            int preferredX =
                x +
                distance *
                preferredDirection;

            if (preferredX >= 0 &&
                preferredX < source.Width)
            {
                int position =
                    y * source.Width +
                    preferredX;

                Color32 candidate =
                    source.Get(
                        preferredX,
                        y);

                if (mask[position] < 16 &&
                    candidate.A >= MinimumVisibleAlpha)
                {
                    return candidate;
                }
            }

            int oppositeX =
                x -
                distance *
                preferredDirection;

            if (oppositeX >= 0 &&
                oppositeX < source.Width)
            {
                int position =
                    y * source.Width +
                    oppositeX;

                Color32 candidate =
                    source.Get(
                        oppositeX,
                        y);

                if (mask[position] < 16 &&
                    candidate.A >= MinimumVisibleAlpha)
                {
                    return candidate;
                }
            }
        }

        /*
         * Vertical fallback for narrow body regions.
         */
        for (int distance = 2;
             distance <= 18;
             distance++)
        {
            int upperY =
                y - distance;

            if (upperY >= 0)
            {
                int position =
                    upperY * source.Width + x;

                Color32 candidate =
                    source.Get(
                        x,
                        upperY);

                if (mask[position] < 16 &&
                    candidate.A >= MinimumVisibleAlpha)
                {
                    return candidate;
                }
            }

            int lowerY =
                y + distance;

            if (lowerY < source.Height)
            {
                int position =
                    lowerY * source.Width + x;

                Color32 candidate =
                    source.Get(
                        x,
                        lowerY);

                if (mask[position] < 16 &&
                    candidate.A >= MinimumVisibleAlpha)
                {
                    return candidate;
                }
            }
        }

        return source.Get(x, y);
    }

    private static PixelBuffer CreateMaskedLayer(
        PixelBuffer source,
        byte[] mask)
    {
        PixelBuffer result = new(
            source.Width,
            source.Height);

        for (int y = 0;
             y < source.Height;
             y++)
        {
            for (int x = 0;
                 x < source.Width;
                 x++)
            {
                int position =
                    y * source.Width + x;

                Color32 color =
                    source.Get(x, y);

                color.A = (byte)(
                    color.A *
                    mask[position] /
                    255);

                if (color.A <
                    MinimumVisibleAlpha)
                {
                    color = Color32.Transparent;
                }

                result.Set(
                    x,
                    y,
                    color);
            }
        }

        return result;
    }

    private static byte[] CreatePolygonMask(
        int width,
        int height,
        PointF[] points)
    {
        using var bitmap = new Bitmap(
            width,
            height,
            PixelFormat.Format32bppArgb);

        using (Graphics graphics =
               Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);

            graphics.SmoothingMode =
                SmoothingMode.AntiAlias;

            graphics.PixelOffsetMode =
                PixelOffsetMode.HighQuality;

            using var path =
                new GraphicsPath();

            path.AddClosedCurve(
                points,
                tension: 0.05f);

            using var brush =
                new SolidBrush(Color.White);

            graphics.FillPath(
                brush,
                path);
        }

        PixelBuffer buffer =
            PixelBuffer.FromBitmap(bitmap);

        byte[] result =
            new byte[width * height];

        for (int y = 0;
             y < height;
             y++)
        {
            for (int x = 0;
                 x < width;
                 x++)
            {
                result[y * width + x] =
                    buffer.Get(x, y).A;
            }
        }

        return result;
    }

    private static PixelBuffer ExtractSubject(
        PixelBuffer source,
        Rectangle bounds,
        bool keepLargestComponent)
    {
        if (bounds.Left < 0 ||
            bounds.Top < 0 ||
            bounds.Right > source.Width ||
            bounds.Bottom > source.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bounds),
                $"Crop {bounds} is outside the source image.");
        }

        PixelBuffer result =
            source.Crop(bounds);

        RemoveBorderConnectedBackground(result);

        if (keepLargestComponent)
        {
            KeepLargestOpaqueComponent(result);
        }

        RemoveDarkEdgeFringe(result);

        ClearLowAlpha(
            result,
            MinimumVisibleAlpha);

        return result;
    }

    private static void RemoveBorderConnectedBackground(
        PixelBuffer image)
    {
        int width = image.Width;
        int height = image.Height;
        int count = width * height;

        bool[] visited =
            new bool[count];

        Queue<int> queue = new();

        void TryAdd(int x, int y)
        {
            if (x < 0 ||
                y < 0 ||
                x >= width ||
                y >= height)
            {
                return;
            }

            int position =
                y * width + x;

            if (visited[position])
            {
                return;
            }

            visited[position] = true;

            if (!IsBackground(
                    image.Get(x, y)))
            {
                return;
            }

            queue.Enqueue(position);
        }

        for (int x = 0;
             x < width;
             x++)
        {
            TryAdd(x, 0);
            TryAdd(x, height - 1);
        }

        for (int y = 1;
             y < height - 1;
             y++)
        {
            TryAdd(0, y);
            TryAdd(width - 1, y);
        }

        while (queue.Count > 0)
        {
            int position =
                queue.Dequeue();

            int x =
                position % width;

            int y =
                position / width;

            image.Set(
                x,
                y,
                Color32.Transparent);

            TryAdd(x - 1, y);
            TryAdd(x + 1, y);
            TryAdd(x, y - 1);
            TryAdd(x, y + 1);
        }
    }

    private static bool IsBackground(
        Color32 color)
    {
        if (color.A == 0)
            return true;

        int maximum = Math.Max(
            color.R,
            Math.Max(color.G, color.B));

        int minimum = Math.Min(
            color.R,
            Math.Min(color.G, color.B));

        int chroma = maximum - minimum;

        // Only border-connected pixels are removed, so dark outlines
        // inside fish remain intact.
        return maximum <= 32 && chroma <= 24;
    }

    private static void KeepLargestOpaqueComponent(
        PixelBuffer image)
    {
        int width = image.Width;
        int height = image.Height;
        int count = width * height;

        bool[] visited =
            new bool[count];

        List<int>? largest = null;
        Queue<int> queue = new();

        for (int start = 0;
             start < count;
             start++)
        {
            int startX =
                start % width;

            int startY =
                start / width;

            if (visited[start] ||
                image.Get(startX, startY).A <
                MinimumVisibleAlpha)
            {
                continue;
            }

            List<int> component = [];

            visited[start] = true;
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                int position =
                    queue.Dequeue();

                component.Add(position);

                int x =
                    position % width;

                int y =
                    position / width;

                for (int offsetY = -1;
                     offsetY <= 1;
                     offsetY++)
                {
                    for (int offsetX = -1;
                         offsetX <= 1;
                         offsetX++)
                    {
                        if (offsetX == 0 &&
                            offsetY == 0)
                        {
                            continue;
                        }

                        int nextX =
                            x + offsetX;

                        int nextY =
                            y + offsetY;

                        if (nextX < 0 ||
                            nextY < 0 ||
                            nextX >= width ||
                            nextY >= height)
                        {
                            continue;
                        }

                        int nextPosition =
                            nextY * width +
                            nextX;

                        if (visited[nextPosition] ||
                            image.Get(
                                nextX,
                                nextY).A <
                            MinimumVisibleAlpha)
                        {
                            continue;
                        }

                        visited[nextPosition] = true;
                        queue.Enqueue(nextPosition);
                    }
                }
            }

            if (largest is null ||
                component.Count > largest.Count)
            {
                largest = component;
            }
        }

        if (largest is null)
        {
            return;
        }

        bool[] keep =
            new bool[count];

        foreach (int position in largest)
        {
            keep[position] = true;
        }

        for (int y = 0;
             y < height;
             y++)
        {
            for (int x = 0;
                 x < width;
                 x++)
            {
                if (!keep[y * width + x])
                {
                    image.Set(
                        x,
                        y,
                        Color32.Transparent);
                }
            }
        }
    }

    private static void RemoveDarkEdgeFringe(
        PixelBuffer image)
    {
        PixelBuffer original = image.Clone();

        for (int y = 1; y < image.Height - 1; y++)
        {
            for (int x = 1; x < image.Width - 1; x++)
            {
                Color32 color = original.Get(x, y);

                if (color.A == 0)
                    continue;

                bool touchesTransparency =
                    original.Get(x - 1, y).A == 0 ||
                    original.Get(x + 1, y).A == 0 ||
                    original.Get(x, y - 1).A == 0 ||
                    original.Get(x, y + 1).A == 0;

                if (!touchesTransparency)
                    continue;

                int maximum = Math.Max(
                    color.R,
                    Math.Max(color.G, color.B));

                if (maximum > 42)
                    continue;

                float darkness =
                    Math.Clamp((42f - maximum) / 42f, 0f, 1f);

                color.A = (byte)(color.A * (1f - darkness * 0.85f));

                if (color.A < 16)
                    color = Color32.Transparent;

                image.Set(x, y, color);
            }
        }
    }

    private static void ClearLowAlpha(
        PixelBuffer image,
        byte threshold)
    {
        for (int y = 0;
             y < image.Height;
             y++)
        {
            for (int x = 0;
                 x < image.Width;
                 x++)
            {
                Color32 color =
                    image.Get(x, y);

                if (color.A >= threshold)
                {
                    continue;
                }

                image.Set(
                    x,
                    y,
                    Color32.Transparent);
            }
        }
    }

    private static void ExportReefs(
        PixelBuffer source,
        string outputDirectory)
    {
        string directory = Path.Combine(
            outputDirectory,
            "Reef");

        Directory.CreateDirectory(directory);

        PixelBuffer left = ExtractSubject(
            source,
            new Rectangle(
                0,
                250,
                680,
                550),
            keepLargestComponent: true);

        PixelBuffer right = ExtractSubject(
            source,
            new Rectangle(
                775,
                172,
                564,
                628),
            keepLargestComponent: true);

        using Bitmap leftBitmap =
            left.ToBitmap();

        using Bitmap rightBitmap =
            right.ToBitmap();

        leftBitmap.Save(
            Path.Combine(
                directory,
                "reef-left.png"),
            ImageFormat.Png);

        rightBitmap.Save(
            Path.Combine(
                directory,
                "reef-right.png"),
            ImageFormat.Png);

        Console.WriteLine("Generated reef-left.png");
        Console.WriteLine("Generated reef-right.png");
    }

    private static void ExportBubbles(
        string outputDirectory)
    {
        string directory = Path.Combine(
            outputDirectory,
            "Bubbles");

        Directory.CreateDirectory(directory);

        int[] sizes =
        [
            12,
            18,
            26,
            36,
            52
        ];

        foreach (int diameter in sizes)
        {
            using Bitmap bitmap =
                CreateBubble(diameter);

            bitmap.Save(
                Path.Combine(
                    directory,
                    $"bubble-{diameter}.png"),
                ImageFormat.Png);
        }

        Console.WriteLine(
            $"Generated {sizes.Length} bubbles");
    }

    private static Bitmap CreateBubble(
        int diameter)
    {
        int size =
            diameter + 12;

        Bitmap result = new(
            size,
            size,
            PixelFormat.Format32bppArgb);

        using Graphics graphics =
            Graphics.FromImage(result);

        graphics.Clear(Color.Transparent);

        graphics.SmoothingMode =
            SmoothingMode.AntiAlias;

        RectangleF bounds = new(
            6,
            6,
            diameter,
            diameter);

        using var outline = new Pen(
            Color.FromArgb(
                165,
                150,
                225,
                255),
            Math.Max(
                1.2f,
                diameter * 0.055f));

        graphics.DrawEllipse(
            outline,
            bounds);

        using var highlight =
            new SolidBrush(
                Color.FromArgb(
                    220,
                    245,
                    255,
                    255));

        graphics.FillEllipse(
            highlight,
            bounds.Left +
            diameter * 0.20f,
            bounds.Top +
            diameter * 0.18f,
            Math.Max(
                2f,
                diameter * 0.15f),
            Math.Max(
                2f,
                diameter * 0.09f));

        return result;
    }

    private static void ExportManifest(
        string outputDirectory)
    {
        var manifest = new
        {
            sourceSize = new
            {
                width = ExpectedSourceWidth,
                height = ExpectedSourceHeight
            },

            frameRate = PlaybackFrameRate,

            animationMode = "retro-cel-deformation",

            fish = Creatures.Select(
                creature => new
                {
                    name = creature.Name,
                    directory =
                        $"Fish/{creature.Name}",
                    frames =
                        creature.FrameCount,
                    frameRate =
                        PlaybackFrameRate,
                    facesRight =
                        creature.FacesRight,
                    nominalScale =
                        creature.NominalScale,
                    speed =
                        creature.Speed,
                    movement =
                        creature.Style ==
                        AnimationStyle.Stingray
                            ? "stingray"
                            : "fish"
                })
                .ToArray(),

            reef = new
            {
                left =
                    "Reef/reef-left.png",
                right =
                    "Reef/reef-right.png"
            },

            bubbles = new[]
            {
                "Bubbles/bubble-12.png",
                "Bubbles/bubble-18.png",
                "Bubbles/bubble-26.png",
                "Bubbles/bubble-36.png",
                "Bubbles/bubble-52.png"
            }
        };

        File.WriteAllText(
            Path.Combine(
                outputDirectory,
                "manifest.json"),
            JsonSerializer.Serialize(
                manifest,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
    }

    private static void ExportReadme(
        string outputDirectory)
    {
        File.WriteAllText(
            Path.Combine(
                outputDirectory,
                "README.md"),
            $"""
            # Aquarium sprites

            Source resolution: {ExpectedSourceWidth}x{ExpectedSourceHeight}
            Playback rate: {PlaybackFrameRate} FPS

            Animation method:

            - Complete rear-body deformation instead of detached tail rotation.
            - Continuous (unquantized) poses for smooth swimming animation.
            - Perspective-compressed side-fin strokes.
            - Traveling dorsal/anal edge waves on triggerfish.
            - Whole-silhouette stingray wing stroke and delayed tip curl.
            - Transparent padding keeps all animated poses aligned.

            The generator does not modify sprites.png.
            """);
    }

    private static Bitmap ConvertToArgb(
        Bitmap source)
    {
        Bitmap result = new(
            source.Width,
            source.Height,
            PixelFormat.Format32bppArgb);

        using Graphics graphics =
            Graphics.FromImage(result);

        graphics.DrawImage(
            source,
            new Rectangle(
                0,
                0,
                result.Width,
                result.Height));

        return result;
    }

    private static void RecreateOutputDirectory(
        string outputDirectory)
    {
        if (Directory.Exists(outputDirectory))
        {
            Directory.Delete(
                outputDirectory,
                recursive: true);
        }

        Directory.CreateDirectory(outputDirectory);

        Directory.CreateDirectory(
            Path.Combine(
                outputDirectory,
                "Fish"));

        Directory.CreateDirectory(
            Path.Combine(
                outputDirectory,
                "Reef"));

        Directory.CreateDirectory(
            Path.Combine(
                outputDirectory,
                "Bubbles"));
    }

    private static float SmoothStep(
        float value)
    {
        value = Math.Clamp(
            value,
            0f,
            1f);

        return value *
               value *
               (3f - 2f * value);
    }

    private static PointF P(
        float x,
        float y)
    {
        return new PointF(x, y);
    }

    private static SideFinDefinition Fin(
        float pivotX,
        float pivotY,
        float maximumAngle,
        PointF[] points)
    {
        return new SideFinDefinition(
            new PointF(
                pivotX,
                pivotY),
            maximumAngle,
            points);
    }

    private enum AnimationStyle
    {
        NormalFish,
        Triggerfish,
        Stingray
    }

    private sealed record CreatureDefinition(
        string Name,
        Rectangle SourceBounds,
        bool FacesRight,
        float NominalScale,
        float Speed,
        AnimationStyle Style,
        int FrameCount,
        float TailHingeX,
        float TailAmplitude,
        float TailCompression,
        float BodyFlex,
        SideFinDefinition? SideFin);

    private sealed record SideFinDefinition(
        PointF Pivot,
        float MaximumAngle,
        PointF[] Points);

    private struct Color32
    {
        public byte B;
        public byte G;
        public byte R;
        public byte A;

        public static Color32 Transparent =>
            new()
            {
                B = 0,
                G = 0,
                R = 0,
                A = 0
            };

        public readonly Color32 WithBrightness(
            float brightness)
        {
            return new Color32
            {
                B = (byte)Math.Clamp(
                    B * brightness,
                    0f,
                    255f),

                G = (byte)Math.Clamp(
                    G * brightness,
                    0f,
                    255f),

                R = (byte)Math.Clamp(
                    R * brightness,
                    0f,
                    255f),

                A = A
            };
        }

        public static Color32 Lerp(
            Color32 first,
            Color32 second,
            float amount)
        {
            amount = Math.Clamp(
                amount,
                0f,
                1f);

            return new Color32
            {
                B = (byte)(
                    first.B +
                    (second.B - first.B) *
                    amount),

                G = (byte)(
                    first.G +
                    (second.G - first.G) *
                    amount),

                R = (byte)(
                    first.R +
                    (second.R - first.R) *
                    amount),

                A = (byte)(
                    first.A +
                    (second.A - first.A) *
                    amount)
            };
        }
    }

    private sealed class PixelBuffer
    {
        private readonly byte[] pixels;

        public int Width { get; }

        public int Height { get; }

        public PixelBuffer(
            int width,
            int height)
        {
            Width = width;
            Height = height;

            pixels =
                new byte[width * height * 4];
        }

        private PixelBuffer(
            int width,
            int height,
            byte[] pixels)
        {
            Width = width;
            Height = height;
            this.pixels = pixels;
        }

        public static PixelBuffer FromBitmap(
            Bitmap bitmap)
        {
            Rectangle bounds = new(
                0,
                0,
                bitmap.Width,
                bitmap.Height);

            BitmapData data = bitmap.LockBits(
                bounds,
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                int rowBytes =
                    bitmap.Width * 4;

                byte[] packed =
                    new byte[
                        bitmap.Width *
                        bitmap.Height *
                        4];

                for (int y = 0;
                     y < bitmap.Height;
                     y++)
                {
                    IntPtr sourcePointer =
                        IntPtr.Add(
                            data.Scan0,
                            y * data.Stride);

                    Marshal.Copy(
                        sourcePointer,
                        packed,
                        y * rowBytes,
                        rowBytes);
                }

                return new PixelBuffer(
                    bitmap.Width,
                    bitmap.Height,
                    packed);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        public Bitmap ToBitmap()
        {
            Bitmap bitmap = new(
                Width,
                Height,
                PixelFormat.Format32bppArgb);

            Rectangle bounds = new(
                0,
                0,
                Width,
                Height);

            BitmapData data = bitmap.LockBits(
                bounds,
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                int rowBytes =
                    Width * 4;

                for (int y = 0;
                     y < Height;
                     y++)
                {
                    IntPtr destinationPointer =
                        IntPtr.Add(
                            data.Scan0,
                            y * data.Stride);

                    Marshal.Copy(
                        pixels,
                        y * rowBytes,
                        destinationPointer,
                        rowBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            bitmap.SetResolution(
                96f,
                96f);

            return bitmap;
        }

        public PixelBuffer Clone()
        {
            return new PixelBuffer(
                Width,
                Height,
                (byte[])pixels.Clone());
        }

        public PixelBuffer Crop(
            Rectangle bounds)
        {
            PixelBuffer result = new(
                bounds.Width,
                bounds.Height);

            for (int y = 0;
                 y < bounds.Height;
                 y++)
            {
                for (int x = 0;
                     x < bounds.Width;
                     x++)
                {
                    result.Set(
                        x,
                        y,
                        Get(
                            bounds.X + x,
                            bounds.Y + y));
                }
            }

            return result;
        }

        public Color32 Get(
            int x,
            int y)
        {
            if (x < 0 ||
                y < 0 ||
                x >= Width ||
                y >= Height)
            {
                return Color32.Transparent;
            }

            int index =
                (y * Width + x) * 4;

            return new Color32
            {
                B = pixels[index + 0],
                G = pixels[index + 1],
                R = pixels[index + 2],
                A = pixels[index + 3]
            };
        }

        public void Set(
            int x,
            int y,
            Color32 color)
        {
            if (x < 0 ||
                y < 0 ||
                x >= Width ||
                y >= Height)
            {
                return;
            }

            int index =
                (y * Width + x) * 4;

            pixels[index + 0] = color.B;
            pixels[index + 1] = color.G;
            pixels[index + 2] = color.R;
            pixels[index + 3] = color.A;
        }

        public Color32 SampleBilinear(
            float x,
            float y)
        {
            if (x < -1f ||
                y < -1f ||
                x > Width ||
                y > Height)
            {
                return Color32.Transparent;
            }

            int x0 =
                (int)MathF.Floor(x);

            int y0 =
                (int)MathF.Floor(y);

            int x1 =
                x0 + 1;

            int y1 =
                y0 + 1;

            float amountX =
                x - x0;

            float amountY =
                y - y0;

            Color32 upper = Color32.Lerp(
                Get(x0, y0),
                Get(x1, y0),
                amountX);

            Color32 lower = Color32.Lerp(
                Get(x0, y1),
                Get(x1, y1),
                amountX);

            return Color32.Lerp(
                upper,
                lower,
                amountY);
        }

        public void Blend(
            int x,
            int y,
            Color32 source)
        {
            if (source.A == 0 ||
                x < 0 ||
                y < 0 ||
                x >= Width ||
                y >= Height)
            {
                return;
            }

            Color32 destination =
                Get(x, y);

            float sourceAlpha =
                source.A / 255f;

            float destinationAlpha =
                destination.A / 255f;

            float outputAlpha =
                sourceAlpha +
                destinationAlpha *
                (1f - sourceAlpha);

            if (outputAlpha <= 0f)
            {
                Set(
                    x,
                    y,
                    Color32.Transparent);

                return;
            }

            byte BlendChannel(
                byte sourceChannel,
                byte destinationChannel)
            {
                float value =
                    (sourceChannel *
                     sourceAlpha +
                     destinationChannel *
                     destinationAlpha *
                     (1f - sourceAlpha)) /
                    outputAlpha;

                return (byte)Math.Clamp(
                    value,
                    0f,
                    255f);
            }

            Set(
                x,
                y,
                new Color32
                {
                    B = BlendChannel(
                        source.B,
                        destination.B),

                    G = BlendChannel(
                        source.G,
                        destination.G),

                    R = BlendChannel(
                        source.R,
                        destination.R),

                    A = (byte)Math.Clamp(
                        outputAlpha * 255f,
                        0f,
                        255f)
                });
        }
    }
}
