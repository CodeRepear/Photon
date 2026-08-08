using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Photon.Edit;

/// <summary>
/// Named filter preset. Each is a curated set of adjustments plus
/// an optional extra color matrix for effects that can't be expressed
/// as simple slider values (e.g. sepia tint, fade lift).
/// </summary>
public sealed record FilterPreset(
    string Name,
    AdjustmentState Adjust,
    Func<float[]?>? ExtraColorMatrix = null);

/// <summary>
/// Catalog of built-in filter presets. The UI binds to this list.
/// Index 0 is always "Original" (neutral).
///
/// NOTE: presets are built with named arguments on purpose. AdjustmentState
/// is a record with 13 positional doubles — a purely positional
/// initializer list here is exactly the kind of thing that silently breaks
/// (wrong slider gets the wrong value) the next time a field is added or
/// reordered, the way Vibrance/Tint just were. Named args make that a
/// compile-time non-issue.
/// </summary>
public static class FilterPipeline
{
    private static AdjustmentState A(
        double brightness = 0, double contrast = 0, double saturation = 0, double vibrance = 0,
        double highlights = 0, double shadows = 0, double warmth = 0, double tint = 0,
        double sharpness = 0, double exposure = 0, double clarity = 0,
        double vignette = 0, double grain = 0) => new(
            Brightness: brightness, Contrast: contrast, Saturation: saturation, Vibrance: vibrance,
            Highlights: highlights, Shadows: shadows, Warmth: warmth, Tint: tint,
            Sharpness: sharpness, Exposure: exposure, Clarity: clarity,
            Vignette: vignette, Grain: grain);

    public static readonly IReadOnlyList<FilterPreset> Presets = new[]
    {
        // --- Baseline ---
        new FilterPreset("Original", AdjustmentState.Neutral),

        // --- Tone & Light ---
        new FilterPreset("Vivid",     A(brightness: 0.05, contrast: 0.20, saturation: 0.25, vibrance: 0.15, highlights: 0.10, shadows: 0.05, warmth: 0.05, sharpness: 0.10)),
        new FilterPreset("Bright",    A(brightness: 0.25, contrast: 0.05, saturation: 0.10, highlights: 0.15, shadows: 0.20, exposure: 0.15)),
        new FilterPreset("Dramatic",  A(brightness: -0.05, contrast: 0.35, saturation: 0.10, vibrance: 0.10, highlights: -0.30, shadows: 0.20, sharpness: 0.25, exposure: -0.05, vignette: 0.20)),
        new FilterPreset("High Key",  A(brightness: 0.20, contrast: -0.15, saturation: 0.05, highlights: 0.35, shadows: 0.30, exposure: 0.10)),
        new FilterPreset("Low Key",   A(brightness: -0.15, contrast: 0.25, saturation: 0.10, highlights: -0.20, shadows: -0.15, sharpness: 0.10, exposure: -0.10, vignette: 0.30)),

        // --- Color temperature ---
        new FilterPreset("Warm",      A(brightness: 0.05, contrast: 0.05, saturation: 0.10, shadows: 0.10, warmth: 0.30)),
        new FilterPreset("Cool",      A(brightness: 0.05, contrast: 0.05, saturation: 0.10, shadows: 0.10, warmth: -0.30)),
        new FilterPreset("Sunset",    A(brightness: 0.08, contrast: 0.15, saturation: 0.15, vibrance: 0.10, highlights: 0.10, warmth: 0.40, tint: 0.05, exposure: 0.05, vignette: 0.10)),
        new FilterPreset("Moonlight", A(brightness: -0.05, contrast: 0.10, saturation: -0.10, highlights: -0.10, shadows: 0.10, warmth: -0.35, sharpness: 0.15, exposure: -0.05, vignette: 0.05, grain: 0.05)),

        // --- Fade / Film looks ---
        new FilterPreset("Fade",      A(brightness: 0.15, contrast: -0.20, saturation: -0.25), ExtraColorMatrix: FadeMatrix),
        new FilterPreset("Matte",     A(brightness: 0.10, contrast: -0.25, saturation: -0.10, highlights: -0.10, shadows: 0.10), ExtraColorMatrix: MatteMatrix),
        new FilterPreset("Vintage",   A(brightness: 0.10, contrast: -0.10, saturation: -0.10, warmth: 0.20, vignette: 0.15, grain: 0.10), ExtraColorMatrix: SepiaMatrix),
        new FilterPreset("Chrome",    A(brightness: 0.05, contrast: 0.25, saturation: 0.15, vibrance: 0.10, highlights: 0.10, shadows: 0.10, sharpness: 0.20, vignette: 0.20)),
        new FilterPreset("Film",      A(brightness: 0.05, contrast: -0.10, saturation: -0.05, highlights: 0.05, shadows: 0.15, warmth: 0.10, vignette: 0.20, grain: 0.15), ExtraColorMatrix: FadeMatrix),

        // --- Black & White ---
        new FilterPreset("B&W",       A(contrast: 0.20, saturation: -1.0, highlights: 0.00, shadows: 0.00, sharpness: 0.15)),
        new FilterPreset("Noir",      A(brightness: -0.10, contrast: 0.35, saturation: -1.0, highlights: -0.20, shadows: 0.20, sharpness: 0.30, exposure: -0.05, vignette: 0.30, grain: 0.05)),
        new FilterPreset("Cinematic", A(brightness: -0.05, contrast: 0.30, saturation: 0.10, highlights: -0.15, shadows: 0.10, warmth: 0.15, sharpness: 0.15, exposure: -0.05, vignette: 0.15, grain: 0.05), ExtraColorMatrix: TealOrangeMatrix),
    };

    public static IEnumerable<string> PresetNames
    {
        get { foreach (var p in Presets) yield return p.Name; }
    }

    public static FilterPreset ByName(string name)
    {
        foreach (var p in Presets) if (p.Name == name) return p;
        return Presets[0];
    }

    /// <summary>
    /// Renders the preset onto a source bitmap.
    /// </summary>
    public static SKBitmap Apply(SKBitmap source, FilterPreset preset, AdjustmentState? baseAdjust = null)
    {
        var combined = baseAdjust is null ? preset.Adjust : Combine(baseAdjust, preset.Adjust);
        var adjusted = AdjustmentEngine.Apply(source, combined);

        if (preset.ExtraColorMatrix is null) return adjusted;

        var matrix = preset.ExtraColorMatrix();
        if (matrix is null) return adjusted;

        var result = new SKBitmap(adjusted.Width, adjusted.Height, adjusted.ColorType, adjusted.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Clear();
        using var paint = new SKPaint
        {
            IsAntialias = true,
            ColorFilter = SKColorFilter.CreateColorMatrix(matrix),
        };
        canvas.DrawBitmap(adjusted, 0, 0, paint);
        adjusted.Dispose();
        return result;
    }

    /// <summary>Stack two adjustment states, clamping to valid ranges.</summary>
    public static AdjustmentState Combine(AdjustmentState? baseA, AdjustmentState presetA)
    {
        if (baseA is null) return presetA;
        var b = baseA;
        return new AdjustmentState(
            Brightness: Clamp(b.Brightness + presetA.Brightness),
            Contrast:   Clamp(b.Contrast   + presetA.Contrast),
            Saturation: Clamp(b.Saturation + presetA.Saturation),
            Vibrance:   Clamp(b.Vibrance   + presetA.Vibrance),
            Highlights: Clamp(b.Highlights + presetA.Highlights),
            Shadows:    Clamp(b.Shadows    + presetA.Shadows),
            Warmth:     Clamp(b.Warmth     + presetA.Warmth),
            Tint:       Clamp(b.Tint       + presetA.Tint),
            Sharpness:  Math.Clamp(b.Sharpness + presetA.Sharpness, 0, 1),
            Exposure:   Clamp(b.Exposure   + presetA.Exposure),
            Clarity:    Clamp(b.Clarity    + presetA.Clarity),
            Vignette:   Math.Clamp(b.Vignette  + presetA.Vignette,  0, 1),
            Grain:      Math.Clamp(b.Grain     + presetA.Grain,     0, 1));
    }

    private static double Clamp(double v) => Math.Clamp(v, -1, 1);

    // ----- Extra color matrices -----

    private static float[] FadeMatrix() => new[]
    {
        0.95f, 0,     0,     0, 0.04f,
        0,     0.95f, 0,     0, 0.04f,
        0,     0,     0.95f, 0, 0.04f,
        0,     0,     0,     1, 0,
    };

    private static float[] MatteMatrix() => new[]
    {
        0.88f, 0,     0,     0, 0.08f,
        0,     0.88f, 0,     0, 0.08f,
        0,     0,     0.88f, 0, 0.08f,
        0,     0,     0,     1, 0,
    };

    private static float[] SepiaMatrix() => new[]
    {
        0.393f, 0.769f, 0.189f, 0, 0,
        0.349f, 0.686f, 0.168f, 0, 0,
        0.272f, 0.534f, 0.131f, 0, 0,
        0,      0,      0,      1, 0,
    };

    /// <summary>Teal-orange cinematic color grade.</summary>
    private static float[] TealOrangeMatrix() => new[]
    {
        1.10f, 0.05f, 0.00f, 0, 0,
        0.00f, 1.00f, 0.05f, 0, 0,
       -0.10f, 0.05f, 1.10f, 0, 0,
        0,     0,     0,     1, 0,
    };
}