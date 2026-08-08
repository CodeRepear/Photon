using System;
using Windows.Storage;

namespace Photon.Models;

/// <summary>
/// Kind of media stored in a <see cref="MediaItem"/>.
/// </summary>
public enum MediaType
{
    Image,
    Animation,
    Video,
}

/// <summary>
/// Lightweight, immutable description of a single file the library has discovered.
/// Heavyweight pixel data (decoded bitmaps, thumbnails) is loaded lazily and
/// never embedded in this record so instances can flow freely across threads
/// and persist in the SQLite index without bloating memory.
/// </summary>
public sealed record MediaItem(
    string Path,
    string FileName,
    MediaType Type,
    ulong FileSize,
    DateTimeOffset DateCreated,
    DateTimeOffset DateModified,
    uint Width,
    uint Height,
    string Format,
    GeoCoord? Location = null,
    string? CameraMake = null,
    string? CameraModel = null,
    string? LensModel = null,
    int? IsoSpeed = null,
    double? ApertureFNumber = null,
    string? ExposureTime = null,
    double? FocalLength = null,
    bool IsFavorite = false)
{
    /// <summary>Short, human-friendly label like "4:032 × 3:024 · HEIC · 4.2 MB".</summary>
    public string DisplaySummary =>
        $"{Width:N0} × {Height:N0}  ·  {Format.ToUpperInvariant()}  ·  {FormatFileSize(FileSize)}";

    /// <summary>True if the file still exists on disk at the recorded path.</summary>
    public bool FileExists => System.IO.File.Exists(Path);

    /// <summary>Folder containing the file, for grouping.</summary>
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    /// <summary>Year-month bucket string (e.g. "2025-03") for date grouping.</summary>
    public string YearMonth => DateCreated.ToString("yyyy-MM");

    private static string FormatFileSize(ulong bytes) => bytes switch
    {
        < 1024UL                  => $"{bytes} B",
        < 1024UL * 1024           => $"{bytes / 1024.0:F1} KB",
        < 1024UL * 1024 * 1024    => $"{bytes / (1024.0 * 1024):F1} MB",
        _                          => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}

/// <summary>GPS coordinate pair recorded in EXIF metadata.</summary>
public sealed record GeoCoord(double Latitude, double Longitude);

/// <summary>
/// Non-destructive edit state captured while the user is in the editor.
/// Stored in memory only (not persisted) until the user exports.
/// </summary>
public sealed record EditState(
    double CropX,       // 0..1, relative to image
    double CropY,
    double CropWidth,   // 0..1
    double CropHeight,  // 0..1
    double RotationDeg, // -45..45
    string AspectRatioLabel); // "Free", "1:1", "4:3", "16:9", "3:2"
