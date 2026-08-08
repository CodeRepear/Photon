using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SkiaSharp;

namespace Photon.Decode;

/// <summary>
/// ImageSharp-backed decoder for the common format set: JPEG, PNG, BMP, TIFF,
/// GIF (single-frame), WebP. Animated GIFs are routed through
/// <see cref="AnimatedGifDecoder"/> instead.
/// </summary>
public sealed class ImageSharpDecoder : IImageDecoder
{
    private readonly string _path;
    public ImageSharpDecoder(string path) => _path = path;

    public async ValueTask<DecodedImage> DecodeAsync(int targetWidth = 0, CancellationToken ct = default)
    {
        var img = await Image.LoadAsync<Rgba32>(_path, ct).ConfigureAwait(false);

        if (targetWidth > 0 && img.Width > targetWidth)
        {
            var ratio = (double)targetWidth / img.Width;
            var targetHeight = (int)Math.Round(img.Height * ratio);
            img.Mutate(ctx => ctx.Resize(targetWidth, targetHeight, KnownResamplers.Lanczos3));
        }

        var skia = ToSkiaBitmap(img);
        return new DecodedImage { SharpImage = img, SkiaBitmap = skia };
    }

    /// <summary>
    /// Decodes a thumbnail-sized image (max <paramref name="maxDimension"/> on the
    /// long edge) and returns the ImageSharp instance only — no SkiaSharp bitmap.
    /// Used by <see cref="Photon.Core.ThumbnailEngine"/> to avoid double-allocating.
    /// </summary>
    public static async Task<Image<Rgba32>?> DecodeThumbnailAsync(string path, int maxDimension, CancellationToken ct)
    {
        try
        {
            var img = await Image.LoadAsync<Rgba32>(path, ct).ConfigureAwait(false);
            if (img.Width > maxDimension || img.Height > maxDimension)
            {
                img.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(maxDimension, maxDimension),
                    Mode = ResizeMode.Max,
                    Sampler = KnownResamplers.Lanczos3,
                }));
            }
            return img;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Copies ImageSharp RGBA pixels into an SkiaSharp bitmap (same memory layout).</summary>
    private static SKBitmap ToSkiaBitmap(Image<Rgba32> img)
    {
        var bmp = new SKBitmap(img.Width, img.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        img.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var rowBytes = MemoryMarshal.AsBytes(row);
                using var pixmap = new SKPixmap(bmp.Info, bmp.GetPixels(), bmp.RowBytes);
                var target = pixmap.GetPixelSpan<byte>();
                var offset = y * bmp.RowBytes;
                rowBytes.CopyTo(target.Slice(offset));
            }
        });
        return bmp;
    }
}
