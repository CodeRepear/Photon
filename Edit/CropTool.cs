using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Photon.Edit;

/// <summary>
/// Pure-math crop helper. Computes the crop rectangle in normalized image
/// coordinates (0..1) given the user's drag interaction, constrains it to a
/// chosen aspect ratio, and renders the visual overlay (handles + dimmed
/// outside region) into an <see cref="SKBitmap"/> that the viewer lays over
/// the image.
/// </summary>
public static class CropTool
{
    /// <summary>Aspect ratio presets offered in the editor. Label → (w,h) ratio.</summary>
    public static readonly IReadOnlyDictionary<string, (double W, double H)> AspectRatios = new Dictionary<string, (double, double)>
    {
        ["Free"] = (0, 0),   // unconstrained
        ["1:1"]  = (1, 1),
        ["4:3"]  = (4, 3),
        ["3:2"]  = (3, 2),
        ["16:9"] = (16, 9),
        ["3:4"]  = (3, 4),   // portrait
        ["2:3"]  = (2, 3),
    };

    /// <summary>
    /// Adjust a draft crop rect (also in 0..1) to match the chosen aspect ratio,
    /// preserving the larger dimension the user picked. Returns clamped to image.
    /// </summary>
    public static (double X, double Y, double W, double H) ConstrainToAspect(
        double x, double y, double w, double h, string aspectLabel, int imageWidth, int imageHeight)
    {
        if (!AspectRatios.TryGetValue(aspectLabel, out var ratio)) return (x, y, w, h);
        if (ratio.W == 0 || ratio.H == 0) return (x, y, w, h);

        // Ratio in normalized image space must account for non-square pixels.
        double imageAspect = (double)imageWidth / imageHeight;
        double targetAspect = ratio.W / ratio.H;
        double normalizedTarget = targetAspect / imageAspect;

        // Keep the larger of w/h, resize the other.
        if (w / h > normalizedTarget)
        {
            // width is leading
            h = w / normalizedTarget;
        }
        else
        {
            w = h * normalizedTarget;
        }

        // Re-clamp into the image bounds.
        if (x + w > 1) x = 1 - w;
        if (y + h > 1) y = 1 - h;
        if (x < 0) { x = 0; w = Math.Min(w, 1); }
        if (y < 0) { y = 0; h = Math.Min(h, 1); }

        return (x, y, w, h);
    }

    /// <summary>
    /// Renders the crop overlay: dim the area outside the rect and draw 8
    /// handles (4 corners + 4 edges). Result is an SKBitmap with the same
    /// dimensions as the source image and premultiplied alpha.
    /// </summary>
    public static SKBitmap BuildOverlay(int imageWidth, int imageHeight,
        double x, double y, double w, double h)
    {
        var info = new SKImageInfo(imageWidth, imageHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bmp = new SKBitmap(info);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear();

        // Dim outside the crop rect — fill four rectangles around it.
        using var dim = new SKPaint { Color = new SKColor(0, 0, 0, 140) };
        int ix = (int)(x * imageWidth);
        int iy = (int)(y * imageHeight);
        int iw = (int)(w * imageWidth);
        int ih = (int)(h * imageHeight);

        canvas.DrawRect(0, 0, imageWidth, iy, dim);                                  // top
        canvas.DrawRect(0, iy + ih, imageWidth, imageHeight - (iy + ih), dim);       // bottom
        canvas.DrawRect(0, iy, ix, ih, dim);                                         // left
        canvas.DrawRect(ix + iw, iy, imageWidth - (ix + iw), ih, dim);               // right

        // Crop border + handles.
        using var border = new SKPaint
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(2, imageWidth / 500f),
            IsAntialias = true,
        };
        canvas.DrawRect(ix, iy, iw, ih, border);

        float handle = Math.Max(8f, imageWidth / 100f);
        DrawHandle(canvas, ix,         iy,         handle);
        DrawHandle(canvas, ix + iw,    iy,         handle);
        DrawHandle(canvas, ix,         iy + ih,    handle);
        DrawHandle(canvas, ix + iw,    iy + ih,    handle);
        DrawHandle(canvas, ix + iw / 2, iy,         handle);
        DrawHandle(canvas, ix + iw / 2, iy + ih,    handle);
        DrawHandle(canvas, ix,         iy + ih / 2, handle);
        DrawHandle(canvas, ix + iw,    iy + ih / 2, handle);

        return bmp;
    }

    private static void DrawHandle(SKCanvas canvas, float cx, float cy, float size)
    {
        using var fill = new SKPaint
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        using var stroke = new SKPaint
        {
            Color = SKColors.Black,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = true,
        };
        var rect = new SKRect(cx - size / 2, cy - size / 2, cx + size / 2, cy + size / 2);
        canvas.DrawRect(rect, fill);
        canvas.DrawRect(rect, stroke);
    }

    /// <summary>
    /// Crops and rotates a source <see cref="SKBitmap"/> according to the
    /// given normalized crop rect (0..1) and rotation degrees. Caller owns
    /// the returned bitmap.
    /// </summary>
    public static SKBitmap Apply(SKBitmap source, double x, double y, double w, double h, double rotationDeg)
    {
        int sx = (int)(x * source.Width);
        int sy = (int)(y * source.Height);
        int sw = (int)(w * source.Width);
        int sh = (int)(h * source.Height);

        var cropped = new SKBitmap(new SKImageInfo(sw, sh, source.ColorType, source.AlphaType));
        using (var canvas = new SKCanvas(cropped))
        {
            canvas.Clear();
            canvas.DrawBitmap(source, new SKRect(sx, sy, sx + sw, sy + sh), new SKRect(0, 0, sw, sh));
        }

        if (Math.Abs(rotationDeg) < 0.01) return cropped;

        // Rotate around center; expand canvas to fit rotated rectangle.
        float rad = (float)(rotationDeg * Math.PI / 180.0);
        float cos = Math.Abs(MathF.Cos(rad));
        float sin = Math.Abs(MathF.Sin(rad));
        int newW = (int)(sw * cos + sh * sin);
        int newH = (int)(sw * sin + sh * cos);

        var rotated = new SKBitmap(new SKImageInfo(newW, newH, source.ColorType, source.AlphaType));
        using (var canvas = new SKCanvas(rotated))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(newW / 2f, newH / 2f);
            canvas.RotateDegrees((float)rotationDeg);
            canvas.Translate(-sw / 2f, -sh / 2f);
            canvas.DrawBitmap(cropped, 0, 0);
        }
        cropped.Dispose();
        return rotated;
    }
}
