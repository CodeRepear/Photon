using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace Photon.Edit;

/// <summary>
/// Compression / size-reduction tool. Re-encodes an image at a target quality
/// and optionally a target file size (binary-searches the JPEG quality until
/// the output is under the size budget). Optionally downscales the image
/// and strips metadata. Mirrors the spec in <c>Idea.md</c> §F8.
/// </summary>
public sealed class CompressionTool
{
    public sealed record Options(
        long? MaxFileSizeBytes,   // null = no size target, just use Quality
        int Quality,              // 0..100 — starting point when size-binning
        int? MaxWidth,
        int? MaxHeight,
        bool StripMetadata,
        bool ReplaceOriginal,     // if false, append "_compressed" to filename
        string OutputFormat);     // "JPEG" / "PNG" / "WebP"

    public sealed record Result(
        string OutputPath,
        long OriginalBytes,
        long CompressedBytes,
        int FinalQuality,
        int OutputWidth,
        int OutputHeight);

    /// <summary>Compress a single file. Output is written next to the source.</summary>
    public async Task<Result> CompressAsync(string sourcePath, Options opts, CancellationToken ct = default)
    {
        var info = new FileInfo(sourcePath);
        long originalBytes = info.Length;

        using var src = SKBitmap.Decode(sourcePath);
        if (src is null) throw new InvalidOperationException("Cannot decode source image");

        // 1) Optional resize.
        var working = MaybeResize(src, opts.MaxWidth, opts.MaxHeight);
        try
        {
            // 2) Pick output path.
            string outPath = opts.ReplaceOriginal
                ? sourcePath
                : Path.Combine(
                    Path.GetDirectoryName(sourcePath)!,
                    Path.GetFileNameWithoutExtension(sourcePath) + "_compressed" +
                    GetExtensionFor(opts.OutputFormat));

            // 3) Encode — either fixed quality, or binary-search for target size.
            int finalQuality = opts.Quality;
            byte[]? encodedBytes;

            if (opts.MaxFileSizeBytes is not null)
            {
                encodedBytes = EncodeToTargetSize(working, opts.OutputFormat,
                    opts.MaxFileSizeBytes.Value, opts.Quality, out finalQuality);
            }
            else
            {
                encodedBytes = EncodeAtQuality(working, opts.OutputFormat, opts.Quality);
            }

            await File.WriteAllBytesAsync(outPath, encodedBytes, ct).ConfigureAwait(false);

            return new Result(
                OutputPath: outPath,
                OriginalBytes: originalBytes,
                CompressedBytes: encodedBytes.Length,
                FinalQuality: finalQuality,
                OutputWidth: working.Width,
                OutputHeight: working.Height);
        }
        finally
        {
            if (!ReferenceEquals(working, src)) working.Dispose();
        }
    }

    // ----- internals -----

    private static byte[] EncodeAtQuality(SKBitmap bmp, string fmt, int quality)
    {
        using var img = SKImage.FromBitmap(bmp);
        var skfmt = ParseFormat(fmt);
        using var data = img.Encode(skfmt, quality);
        return data.ToArray();
    }

    /// <summary>
    /// Binary-search the JPEG quality between 30 and the user's initial quality
    /// until the output is at or under the size budget. Returns the smallest
    /// quality that meets the budget (or the lowest tried if none fit).
    /// </summary>
    private static byte[] EncodeToTargetSize(SKBitmap bmp, string fmt, long maxBytes, int startQuality, out int finalQuality)
    {
        // First try the user's preferred quality — if it already fits, ship it.
        var attempt = EncodeAtQuality(bmp, fmt, startQuality);
        if (attempt.Length <= maxBytes)
        {
            finalQuality = startQuality;
            return attempt;
        }

        // Binary search between 30 and startQuality.
        int lo = 30, hi = startQuality;
        byte[] best = attempt;
        int bestQ = startQuality;

        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            attempt = EncodeAtQuality(bmp, fmt, mid);
            if (attempt.Length <= maxBytes)
            {
                best = attempt;
                bestQ = mid;
                lo = mid + 1; // try higher quality
            }
            else
            {
                hi = mid - 1;
            }
        }

        // If even quality 30 doesn't fit, fall back to that (caller may need to resize).
        if (best.Length > maxBytes)
        {
            attempt = EncodeAtQuality(bmp, fmt, 30);
            best = attempt;
            bestQ = 30;
        }

        finalQuality = bestQ;
        return best;
    }

    private static SKEncodedImageFormat ParseFormat(string fmt) => fmt.ToUpperInvariant() switch
    {
        "JPEG" or "JPG" => SKEncodedImageFormat.Jpeg,
        "PNG"            => SKEncodedImageFormat.Png,
        "WEBP"           => SKEncodedImageFormat.Webp,
        _ => SKEncodedImageFormat.Jpeg,
    };

    private static string GetExtensionFor(string fmt) => fmt.ToUpperInvariant() switch
    {
        "JPEG" or "JPG" => ".jpg",
        "PNG"  => ".png",
        "WEBP" => ".webp",
        _ => ".jpg",
    };

    private static SKBitmap MaybeResize(SKBitmap src, int? maxW, int? maxH)
    {
        if (maxW is null && maxH is null) return src;

        int newW = src.Width;
        int newH = src.Height;
        if (maxW is not null && newW > maxW.Value)
        {
            double r = (double)maxW.Value / newW;
            newW = maxW.Value;
            newH = (int)Math.Round(newH * r);
        }
        if (maxH is not null && newH > maxH.Value)
        {
            double r = (double)maxH.Value / newH;
            newH = maxH.Value;
            newW = (int)Math.Round(newW * r);
        }
        if (newW == src.Width && newH == src.Height) return src;

        var resized = new SKBitmap(newW, newH, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(resized);
        canvas.Clear();
        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.High,
        };
        canvas.DrawBitmap(src, new SKRect(0, 0, newW, newH), paint);
        return resized;
    }
}
