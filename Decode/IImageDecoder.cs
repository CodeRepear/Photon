using System;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;

namespace Photon.Decode;

/// <summary>
/// Decoded image payload. Carries the pixel data in both an ImageSharp
/// instance (for further processing) and a SkiaSharp bitmap (for GPU-backed
/// rendering in <see cref="Photon.Controls.ZoomCanvas"/>).
/// </summary>
public sealed class DecodedImage : IDisposable
{
    public required Image<Rgba32> SharpImage { get; init; }
    public required SKBitmap SkiaBitmap { get; init; }
    public int Width => SharpImage.Width;
    public int Height => SharpImage.Height;

    public void Dispose()
    {
        SharpImage.Dispose();
        SkiaBitmap.Dispose();
    }
}

/// <summary>
/// Animated frame sequence for GIF / animated WebP playback.
/// Each frame carries its own bitmap and the millisecond delay before the
/// next frame should be shown.
/// </summary>
public sealed record AnimatedFrame(SKBitmap Bitmap, int DelayMs);

/// <summary>
/// Common decoder surface for all image formats. Implementations live in this
/// namespace and are dispatched by <see cref="DecoderFactory"/>.
/// </summary>
public interface IImageDecoder
{
    ValueTask<DecodedImage> DecodeAsync(int targetWidth = 0, CancellationToken ct = default);
}

public interface IAnimatedImageDecoder
{
    ValueTask<IReadOnlyList<AnimatedFrame>> DecodeAllFramesAsync(CancellationToken ct = default);
}
