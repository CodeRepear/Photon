using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SkiaSharp;

namespace Photon.Decode;

/// <summary>
/// Enumerates every frame of an animated GIF and returns it as a list of
/// <see cref="AnimatedFrame"/> records. The viewer's animation timer cycles
/// through these in order, advancing by <c>DelayMs</c> each tick.
/// </summary>
public sealed class AnimatedGifDecoder : IAnimatedImageDecoder
{
    private readonly string _path;
    public AnimatedGifDecoder(string path) => _path = path;

    public async ValueTask<IReadOnlyList<AnimatedFrame>> DecodeAllFramesAsync(CancellationToken ct = default)
    {
        using var img = await Image.LoadAsync<Rgba32>(_path, ct).ConfigureAwait(false);

        var frames = new List<AnimatedFrame>(img.Frames.Count);
        var frameMetadata = img.Frames.RootFrame.Metadata.GetGifMetadata();
        var root = img.Frames.RootFrame;

        // Composite each frame onto a fresh canvas because GIF frames can be partial.
        using var canvas = new Image<Rgba32>(img.Width, img.Height);
        for (int i = 0; i < img.Frames.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var srcFrame = img.Frames.CloneFrame(i);
            // Naive: just copy the frame pixels (works for most GIFs where each
            // frame is full-frame; for partial frames a proper compositor is
            // needed — left as a Phase 4 enhancement).
            canvas.Mutate(ctx => ctx.DrawImage(srcFrame, new SixLabors.ImageSharp.Point(0, 0), 1f));
            srcFrame.Dispose();

            var gifMeta = img.Frames[i].Metadata.GetGifMetadata();
            int delayMs = (int)Math.Round(gifMeta.FrameDelay * 10.0); // 1/100 s → ms
            if (delayMs < 20) delayMs = 100; // guard against 0-delay frames that would spin too fast

            var bmp = ToSkiaBitmap(canvas);
            frames.Add(new AnimatedFrame(bmp, delayMs));
        }

        return frames;
    }

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
