using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Photon.Decode;

/// <summary>
/// Routes a file path to the right <see cref="IImageDecoder"/> or
/// <see cref="IAnimatedImageDecoder"/> based on extension. The fast path uses
/// the extension; a future enhancement would inspect magic bytes when the
/// extension is missing or misleading.
/// </summary>
public static class DecoderFactory
{
    public static IImageDecoder Create(string path)
    {
        var ext = Core.FormatRegistry.GetExtension(path);

        // Magick.NET handles everything ImageSharp can't on Windows:
        // HEIC, AVIF, JXL, PSD, RAW formats.
        if (Core.FormatRegistry.MagickDecodedExtensions.Contains(ext))
        {
            return new MagickDecoder(path);
        }

        return ext switch
        {
            "jpg" or "jpeg" or "png" or "bmp" or "tif" or "tiff" or "webp"
                => new ImageSharpDecoder(path),
            "gif"
                => new ImageSharpDecoder(path), // single-frame display; AnimatedGifDecoder used explicitly when needed
            _ => throw new NotSupportedException($"No decoder registered for .{ext}"),
        };
    }

    public static IAnimatedImageDecoder? CreateAnimated(string path)
    {
        var ext = Core.FormatRegistry.GetExtension(path);
        return ext switch
        {
            "gif" => new AnimatedGifDecoder(path),
            _     => null,
        };
    }

    /// <summary>
    /// Convenience entry point used by <see cref="Core.ThumbnailEngine"/>.
    /// Returns an ImageSharp image sized for the requested thumbnail dimension,
    /// or null if the file is unreadable. Routes through Magick.NET for
    /// formats ImageSharp can't read, and extracts video frames via the
    /// Windows Shell thumbnail cache for video files.
    /// </summary>
    public static async Task<Image<Rgba32>?> DecodeForThumbnailAsync(string path, int maxDimension, CancellationToken ct)
    {
        var ext = Core.FormatRegistry.GetExtension(path);

        // Video files: extract a frame via the Windows Shell thumbnail cache.
        if (Core.FormatRegistry.VideoExtensions.Contains(ext))
        {
            return await DecodeVideoThumbnailAsync(path, maxDimension, ct).ConfigureAwait(false);
        }

        // Magick-decoded formats: read full image, downscale, return as ImageSharp.
        if (Core.FormatRegistry.MagickDecodedExtensions.Contains(ext))
        {
            try
            {
                var dec = new MagickDecoder(path);
                using var decoded = await dec.DecodeAsync(maxDimension, ct).ConfigureAwait(false);
                return decoded.SharpImage.CloneAs<Rgba32>();
            }
            catch { return null; }
        }

        return await ImageSharpDecoder.DecodeThumbnailAsync(path, maxDimension, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts a thumbnail frame from a video file using the Windows Shell
    /// thumbnail cache. Returns null if extraction fails.
    /// </summary>
    private static async Task<Image<Rgba32>?> DecodeVideoThumbnailAsync(string path, int maxDimension, CancellationToken ct)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(ct);
            using var thumbnail = await file.GetThumbnailAsync(
                ThumbnailMode.VideosView,
                (uint)maxDimension,
                ThumbnailOptions.None).AsTask(ct);

            if (thumbnail is null || thumbnail.Size == 0) return null;

            using var stream = thumbnail.AsStreamForRead();
            return await Image.LoadAsync<Rgba32>(stream, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
