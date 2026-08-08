using System;
using System.Collections.Generic;
using System.IO;

namespace Photon.Core;

/// <summary>
/// Format capability map. Drives the gallery filter ("Show only WebP"),
/// the "Save As" dropdowns, and the decoder dispatch in <see cref="Photon.Decode.DecoderFactory"/>.
/// </summary>
public static class FormatRegistry
{
    /// <summary>Extensions (lowercase, no leading dot) considered images.</summary>
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "bmp", "tif", "tiff", "gif", "webp", "ico",
        "heic", "heif", "avif", "jxl", "psd",
        // RAW
        "cr2", "cr3", "nef", "arw", "dng", "orf", "rw2", "raf", "pef", "sr2",
    };

    /// <summary>Extensions treated as animated images (need frame timer playback).</summary>
    public static readonly HashSet<string> AnimatedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "gif",
    };

    /// <summary>Extensions routed to MediaPlayerElement for video playback.</summary>
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mov", "mkv", "avi", "webm", "m4v", "3gp", "wmv", "flv",
    };

    /// <summary>Extensions supported for read in this build (Phase 1-4).</summary>
    public static readonly HashSet<string> SupportedReadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Common formats via ImageSharp
        "jpg", "jpeg", "png", "bmp", "tif", "tiff", "gif", "webp",
        // HEIC / AVIF / JXL / PSD via Magick.NET
        "heic", "heif", "avif", "jxl", "psd","ico",
        // RAW via Magick.NET's dcraw delegate
        "cr2", "cr3", "nef", "arw", "dng", "orf", "rw2", "raf", "pef", "sr2",
    };

    /// <summary>Extensions decoded via Magick.NET (everything ImageSharp can't handle on Windows).</summary>
    public static readonly HashSet<string> MagickDecodedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "heic", "heif", "avif", "jxl", "psd","ico",
        "cr2", "cr3", "nef", "arw", "dng", "orf", "rw2", "raf", "pef", "sr2",
    };

    /// <summary>Extensions supported for save/export.</summary>
    public static readonly HashSet<string> SupportedWriteExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "webp", "bmp",
    };

    /// <summary>Extract extension (no dot, lowercase) from a path or extension string.</summary>
    public static string GetExtension(string pathOrExt)
    {
        if (string.IsNullOrEmpty(pathOrExt)) return string.Empty;
        int dot = pathOrExt.LastIndexOf('.');
        if (dot < 0) return pathOrExt.ToLowerInvariant();
        return pathOrExt.AsSpan(dot + 1).ToString().ToLowerInvariant();
    }

    /// <summary>Map an extension to a normalized format label for display.</summary>
    public static string GetFormatLabel(string extensionOrPath)
    {
        var ext = GetExtension(extensionOrPath);
        return ext switch
        {
            "jpg" or "jpeg" => "JPEG",
            "png"            => "PNG",
            "bmp"            => "BMP",
            "tif" or "tiff"  => "TIFF",
            "ico"            => "ICO",
            "gif"            => "GIF",
            "webp"           => "WebP",
            "heic"           => "HEIC",
            "heif"           => "HEIF",
            "avif"           => "AVIF",
            "jxl"            => "JXL",
            "psd"            => "PSD",
            "cr2"            => "CR2",
            "cr3"            => "CR3",
            "nef"            => "NEF",
            "arw"            => "ARW",
            "dng"            => "DNG",
            "orf"            => "ORF",
            "rw2"            => "RW2",
            "raf"            => "RAF",
            "pef"            => "PEF",
            "sr2"            => "SR2",
            "mp4"            => "MP4",
            "mov"            => "MOV",
            "mkv"            => "MKV",
            "avi"            => "AVI",
            "webm"           => "WebM",
            _                => ext.ToUpperInvariant(),
        };
    }

    /// <summary>Classify a file by extension into Image / Animation / Video / Unknown.</summary>
    public static Models.MediaType? Classify(string extensionOrPath)
    {
        var ext = GetExtension(extensionOrPath);
        if (string.IsNullOrEmpty(ext)) return null;
        if (AnimatedImageExtensions.Contains(ext)) return Models.MediaType.Animation;
        if (ImageExtensions.Contains(ext))         return Models.MediaType.Image;
        if (VideoExtensions.Contains(ext))         return Models.MediaType.Video;
        return null;
    }
}
