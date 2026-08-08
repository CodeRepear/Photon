using System;
using SkiaSharp;

namespace Photon.Edit;

/// <summary>
/// All non-destructive color adjustments the user can apply in the editor.
/// All values are user-facing "neutral = 0" sliders.
/// </summary>
public sealed record AdjustmentState(
    double Brightness,   // -1.0 .. +1.0   (0 = neutral)
    double Contrast,     // -1.0 .. +1.0
    double Saturation,   // -1.0 .. +1.0
    double Vibrance,     // -1.0 .. +1.0   (gentler, "smart" saturation)
    double Highlights,   // -1.0 .. +1.0
    double Shadows,      // -1.0 .. +1.0
    double Warmth,       // -1.0 .. +1.0
    double Tint,         // -1.0 .. +1.0   (green .. magenta)
    double Sharpness,    //  0.0 .. +1.0
    double Exposure,     // -1.0 .. +1.0   (0 = neutral)
    double Clarity,      // -1.0 .. +1.0   (0 = neutral)
    double Vignette,     //  0.0 .. +1.0   (0 = none, 1 = max)
    double Grain)        //  0.0 .. +1.0   (0 = none, 1 = max)
{
    public static AdjustmentState Neutral { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public bool IsNeutral =>
        Brightness == 0 && Contrast == 0 && Saturation == 0 && Vibrance == 0 &&
        Highlights == 0 && Shadows == 0 && Warmth == 0 && Tint == 0 && Sharpness == 0 &&
        Exposure == 0 && Clarity == 0 && Vignette == 0 && Grain == 0;
}

/// <summary>
/// Renders an <see cref="AdjustmentState"/> onto an <see cref="SKBitmap"/>.
///
/// Pipeline (each stage is a small, independently-correct linear or
/// per-channel operation — this used to be one big matrix that silently
/// zeroed out the green channel any time saturation was left at 0; see
/// the comment on <see cref="BuildColorMatrix"/> for the postmortem):
///
///   1. Color matrix: exposure, brightness, contrast, saturation/vibrance,
///      warmth/tint. All achromatic terms (brightness/contrast) are added
///      identically to R/G/B so they can never introduce a color cast.
///   2. Tone curve (LUT): highlights/shadows. Applied identically to
///      R/G/B — a tone curve, not a per-channel gain, so it can't tint
///      the image either.
///   3. Sharpness/Clarity (convolution).
///   4. Vignette / Grain (post-process draw passes).
/// </summary>
public static class AdjustmentEngine
{
    // Rec. 709 luma weights, used for saturation/vibrance mixing.
    private const double LumR = 0.2126, LumG = 0.7152, LumB = 0.0722;

    /// <summary>
    /// Returns a new bitmap with the adjustments applied. Caller owns the
    /// returned bitmap and is responsible for disposing it. Source bitmap
    /// is not modified.
    /// </summary>
    public static SKBitmap Apply(SKBitmap source, AdjustmentState state)
    {
        if (state.IsNeutral) return Copy(source);

        var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Clear();
        using (var paint = BuildPaint(source, state))
            canvas.DrawBitmap(source, 0, 0, paint);

        if (state.Vignette > 0.01)
            ApplyVignette(canvas, source.Width, source.Height, state.Vignette);

        if (state.Grain > 0.01)
            ApplyGrain(canvas, source.Width, source.Height, state.Grain);

        return result;
    }

    /// <summary>
    /// Builds an <see cref="SKPaint"/> that applies the color adjustment chain
    /// (matrix + tone curve + optional sharpen/clarity convolution). Vignette
    /// and Grain are NOT included (they need a separate canvas draw pass) —
    /// use <see cref="Apply"/> for the full pipeline.
    /// </summary>
    public static SKPaint BuildPaint(SKBitmap source, AdjustmentState state)
    {
        var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };

        var matrix = BuildColorMatrix(state);
        SKColorFilter colorFilter = SKColorFilter.CreateColorMatrix(matrix);

        if (Math.Abs(state.Highlights) > 0.01 || Math.Abs(state.Shadows) > 0.01)
        {
            var toneCurve = SKColorFilter.CreateTable(BuildToneCurve(state.Highlights, state.Shadows));
            colorFilter = SKColorFilter.CreateCompose(toneCurve, colorFilter);
        }

        paint.ColorFilter = colorFilter;

        if (state.Sharpness > 0.01 || Math.Abs(state.Clarity) > 0.01)
        {
            var strength = Math.Clamp(state.Sharpness + Math.Max(0, state.Clarity) * 0.6, 0, 1);
            var kernel = BuildUnsharpKernel(strength);
            try
            {
                paint.ImageFilter = SKImageFilter.CreateMatrixConvolution(
                    new SKSizeI(3, 3), kernel, 1f, 0f,
                    new SKPointI(1, 1), SKShaderTileMode.Clamp, false);
            }
            catch { /* convolution unsupported on this backend — degrade gracefully */ }
        }

        return paint;
    }

    /// <summary>
    /// Builds the 4x5 color matrix for exposure / brightness / contrast /
    /// saturation+vibrance / warmth / tint.
    ///
    /// BUG THIS REPLACES: the previous version built the G row's own
    /// diagonal coefficient as <c>c * sat * sg * shadowGain * eg</c>, where
    /// <c>sg = (1 - sat) * lumG</c> is the *cross-channel* saturation blend
    /// weight — not an identity term. At neutral saturation, sat == 1, so
    /// sg == 0, and the green channel's diagonal silently became 0: green
    /// was multiplied almost entirely out of the image the instant any
    /// *other* slider (e.g. exposure) took the matrix off the fast
    /// "IsNeutral" path. That's why nudging any single slider turned the
    /// whole image magenta/purple — red and blue passed through, green did
    /// not. Below, every channel's own gain is built from a dedicated
    /// per-channel multiplier (gR/gG/gB) that is never zero unless the
    /// user explicitly cranks contrast/exposure to an extreme, and the
    /// saturation mix is the textbook luma-preserving formula applied
    /// symmetrically to all three rows.
    /// </summary>
    private static float[] BuildColorMatrix(AdjustmentState s)
    {
        // --- Saturation + vibrance combine into a single multiplier. ---
        // Vibrance is modeled as a softer saturation boost (a true vibrance
        // implementation would scale less on already-saturated pixels,
        // which needs a per-pixel shader rather than a static matrix — this
        // is a reasonable linear approximation).
        double sat = 1.0 + s.Saturation + s.Vibrance * 0.5;
        sat = Math.Clamp(sat, 0.0, 2.5);
        double keep = 1.0 - sat; // weight left on the "toward neutral" blend

        // Row R = R*(sat + keep*LumR) + G*(keep*LumG) + B*(keep*LumB)
        // Row G = R*(keep*LumR) + G*(sat + keep*LumG) + B*(keep*LumB)
        // Row B = R*(keep*LumR) + G*(keep*LumG) + B*(sat + keep*LumB)
        double satRR = sat + keep * LumR, satRG = keep * LumG, satRB = keep * LumB;
        double satGR = keep * LumR, satGG = sat + keep * LumG, satGB = keep * LumB;
        double satBR = keep * LumR, satBG = keep * LumG, satBB = sat + keep * LumB;

        // --- Exposure (multiplicative) + contrast (pivot at mid-gray). ---
        double exposureGain = Math.Pow(2.0, s.Exposure);
        double contrast = 1.0 + Math.Clamp(s.Contrast, -1, 1) * 0.8;   // gentler than the old 1.5
        double contrastOffset = 0.5 * (1.0 - contrast);

        // --- Brightness: simple achromatic add (applied to all channels equally). ---
        double brightnessOffset = s.Brightness * 0.3;

        double offset = brightnessOffset + contrastOffset;

        // --- Warmth (R up / B down) and Tint (G up / down), each its own
        // per-channel multiplier — never coupled to saturation, so it can
        // never be zeroed out by another slider. ---
        double warmR = 1.0 + s.Warmth * 0.22;
        double warmB = 1.0 - s.Warmth * 0.22;
        double tintG = 1.0 + s.Tint * 0.22;

        double gR = exposureGain * contrast * warmR;
        double gG = exposureGain * contrast * tintG;
        double gB = exposureGain * contrast * warmB;

        return new float[]
        {
            // R row
            (float)(gR * satRR), (float)(gR * satRG), (float)(gR * satRB), 0, (float)offset,
            // G row
            (float)(gG * satGR), (float)(gG * satGG), (float)(gG * satGB), 0, (float)offset,
            // B row
            (float)(gB * satBR), (float)(gB * satBG), (float)(gB * satBB), 0, (float)offset,
            // A row
            0, 0, 0, 1, 0,
        };
    }

    /// <summary>
    /// Builds a 256-entry per-channel lookup table applying Highlights/Shadows
    /// as a smooth tone curve. Identical for R, G and B, so — unlike the old
    /// per-channel-gain approach — it is structurally incapable of
    /// introducing a color cast. Positive Shadows lifts dark tones; positive
    /// Highlights brightens bright tones.
    /// </summary>
    private static byte[] BuildToneCurve(double highlights, double shadows)
    {
        var table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            double shadowWeight = Math.Pow(1.0 - t, 2);   // strongest near black, 0 near white
            double highlightWeight = Math.Pow(t, 2);      // strongest near white, 0 near black
            double delta = shadows * 0.35 * shadowWeight + highlights * 0.35 * highlightWeight;
            double newT = Math.Clamp(t + delta, 0.0, 1.0);
            table[i] = (byte)Math.Round(newT * 255.0);
        }
        return table;
    }

    /// <summary>
    /// Vignette: radial darkening from center. Drawn as a radial gradient overlay.
    /// </summary>
    public static void ApplyVignette(SKCanvas canvas, int w, int h, double strength)
    {
        float cx = w / 2f, cy = h / 2f;
        float radius = Math.Max(cx, cy) * 1.2f;
        float alpha = (float)(strength * 0.85);

        using var shader = SKShader.CreateTwoPointConicalGradient(
            new SKPoint(cx, cy), radius * 0.4f,
            new SKPoint(cx, cy), radius,
            new[]
            {
                new SKColor(0, 0, 0, 0),
                new SKColor(0, 0, 0, (byte)(alpha * 255)),
            },
            SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Multiply };
        canvas.DrawRect(0, 0, w, h, paint);
    }

    /// <summary>
    /// Film grain: pseudo-random noise overlay. Uses a deterministic pattern
    /// based on pixel position for a film-like look.
    /// </summary>
    public static void ApplyGrain(SKCanvas canvas, int w, int h, double strength)
    {
        float alpha = (float)(strength * 60); // 0..60 out of 255
        var rand = new Random(42); // deterministic for preview consistency

        int blockSize = 2;
        using var paint = new SKPaint();
        paint.BlendMode = SKBlendMode.Overlay;

        for (int y = 0; y < h; y += blockSize)
        {
            for (int x = 0; x < w; x += blockSize)
            {
                byte v = (byte)rand.Next(256);
                paint.Color = new SKColor(v, v, v, (byte)alpha);
                canvas.DrawRect(x, y, blockSize, blockSize, paint);
            }
        }
    }

    /// <summary>
    /// Unsharp mask kernel for sharpness + clarity.
    /// </summary>
    private static float[] BuildUnsharpKernel(double sharpness)
    {
        double s = Math.Clamp(sharpness, 0, 1) * 0.5;
        float center = (float)(1.0 + 4.0 * s);
        float side = (float)(-s);
        return new float[]
        {
            0,      side, 0,
            side,   center, side,
            0,      side, 0,
        };
    }

    private static SKBitmap Copy(SKBitmap src)
    {
        var copy = new SKBitmap(src.Width, src.Height, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(copy);
        canvas.DrawBitmap(src, 0, 0);
        return copy;
    }
}