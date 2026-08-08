using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;

using Photon.Decode;

namespace Photon.Core;

/// <summary>
/// Generates, caches, and serves JPEG thumbnail files for every media item.
/// Thumbnails are stored on disk as <c>%LocalAppData%\Photon\ThumbCache\&lt;hash&gt;.jpg</c>
/// keyed by file path + last-write time so we can cheaply invalidate on edit.
/// </summary>
public sealed class ThumbnailEngine
{
    private readonly ILogger<ThumbnailEngine> _log;
    private readonly SemaphoreSlim _concurrencyLimiter = new(initialCount: 4);
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new();

    public ThumbnailEngine(ILogger<ThumbnailEngine> log) => _log = log;

    /// <summary>
    /// Returns the absolute path of the cached thumbnail JPEG, generating it
    /// on demand if missing or stale. Returns null on failure (corrupt file,
    /// unsupported format, IO error).
    /// </summary>
    public Task<string?> GetOrCreateAsync(string mediaPath, int targetPixelSize, CancellationToken ct = default)
    {
        var key = CacheKey(mediaPath);
        return _inflight.GetOrAdd(key, _ => GenerateAsync(mediaPath, targetPixelSize, key, ct));
    }

    private async Task<string?> GenerateAsync(string mediaPath, int size, string key, CancellationToken ct)
    {
        var cachePath = Path.Combine(AppPaths.ThumbCacheDir, key + ".jpg");

        // Fast path: cache hit and up-to-date.
        if (File.Exists(cachePath) && !IsStale(mediaPath, cachePath))
            return cachePath;

        await _concurrencyLimiter.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under lock to avoid duplicate work when multiple callers raced.
            if (File.Exists(cachePath) && !IsStale(mediaPath, cachePath))
                return cachePath;

            using var img = await DecoderFactory.DecodeForThumbnailAsync(mediaPath, size, ct).ConfigureAwait(false);
            if (img is null) return null;

            // Resize preserving aspect; fit inside a square of `size`.
            img.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(size, size),
                Mode = ResizeMode.Max,
                Sampler = KnownResamplers.Lanczos3,
            }));

            await using var fs = File.Create(cachePath);
            await img.SaveAsync(fs, new JpegEncoder { Quality = 82 }, ct).ConfigureAwait(false);
            return cachePath;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Thumbnail generation failed for {Path}", mediaPath);
            return null;
        }
        finally
        {
            _concurrencyLimiter.Release();
            _inflight.TryRemove(key, out _);
        }
    }

    private static string CacheKey(string mediaPath)
    {
        // Combine path + last-write to invalidate on edit.
        var lastWrite = File.GetLastWriteTimeUtc(mediaPath).Ticks;
        var raw = $"{mediaPath.ToLowerInvariant()}|{lastWrite}";
        // SHA256 first 16 bytes → 32 hex chars. Plenty to avoid collisions in a thumbnail cache.
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(raw);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    private static bool IsStale(string mediaPath, string cachePath)
    {
        try
        {
            return File.GetLastWriteTimeUtc(mediaPath) > File.GetLastWriteTimeUtc(cachePath);
        }
        catch { return true; }
    }

    /// <summary>Best-effort cache size sweep. Removes oldest files until under budget.</summary>
    public void TrimCache(long maxBytes)
    {
        try
        {
            var dir = new DirectoryInfo(AppPaths.ThumbCacheDir);
            if (!dir.Exists) return;
            var files = dir.GetFiles("*.jpg");
            long total = 0;
            foreach (var f in files) total += f.Length;
            if (total <= maxBytes) return;

            Array.Sort(files, static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            foreach (var f in files)
            {
                if (total <= maxBytes) break;
                total -= f.Length;
                try { f.Delete(); } catch { /* swallow */ }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TrimCache failed");
        }
    }
}
