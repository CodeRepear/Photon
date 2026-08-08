using System;
using System.Collections.Generic;
using System.IO;
using MetadataExtractor;
using Microsoft.Extensions.Logging;

namespace Photon.Core;

/// <summary>
/// Reads EXIF / IPTC / XMP metadata using the MetadataExtractor library and
/// projects it onto the <see cref="Models.MediaItem"/> record. The returned
/// dictionary is also consumed directly by the EXIF panel in the viewer.
/// </summary>
public sealed class MetadataReader
{
    private readonly ILogger<MetadataReader> _log;
    public MetadataReader(ILogger<MetadataReader> log) => _log = log;

    /// <summary>Flat, ordered list of "Directory.Tag → Value" rows for display.</summary>
    public sealed record MetadataRow(string Directory, string Tag, string Value);

    /// <summary>
    /// Returns a flat list of every readable metadata row, sorted by directory
    /// then tag. Empty rows (null or whitespace values) are skipped.
    /// </summary>
    public IReadOnlyList<MetadataRow> ReadFlat(string path)
    {
        var rows = new List<MetadataRow>(64);
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(path);
            foreach (var dir in directories)
            {
                foreach (var tag in dir.Tags)
                {
                    var val = tag.Description;
                    if (string.IsNullOrWhiteSpace(val)) continue;
                    rows.Add(new MetadataRow(dir.Name, tag.Name, val));
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read metadata from {Path}", path);
        }
        return rows;
    }

    /// <summary>
    /// Returns the camera/GPS-related fields as a structured
    /// <see cref="Models.MediaItem"/> patch. Use it to enrich the bare index entry.
    /// </summary>
    public (
        Models.GeoCoord? Location,
        string? CameraMake,
        string? CameraModel,
        string? LensModel,
        int? Iso,
        double? ApertureFNumber,
        string? ExposureTime,
        double? FocalLength
    ) ReadCameraFields(string path)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(path);

            string? make = null, model = null, lens = null, exposure = null;
            int? iso = null;
            double? aperture = null, focal = null;
            double? lat = null, lng = null;

            foreach (var dir in directories)
            {
                foreach (var tag in dir.Tags)
                {
                    if (string.IsNullOrWhiteSpace(tag.Description)) continue;
                    var name = tag.Name;
                    var val = tag.Description;
                    switch (name)
                    {
                        case "Make":                  make     = val; break;
                        case "Model":                 model    = val; break;
                        case "Lens Model":            lens     = val; break;
                        case "ISO":                   iso      = ParseInt(val); break;
                        case "F-Number":              aperture = ParseDouble(val); break;
                        case "Exposure Time":         exposure = val; break;
                        case "Focal Length":          focal    = ParseDouble(val); break;
                        case "GPS Latitude":          lat      = ParseGps(val); break;
                        case "GPS Longitude":         lng      = ParseGps(val); break;
                    }
                }
            }

            Models.GeoCoord? loc = (lat.HasValue && lng.HasValue)
                ? new Models.GeoCoord(lat.Value, lng.Value)
                : null;

            return (loc, make, model, lens, iso, aperture, exposure, focal);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read camera fields from {Path}", path);
            return default;
        }
    }

    private static int? ParseInt(string s)
        => int.TryParse(s.Split(' ', '/')[0], out var v) ? v : null;

    private static double? ParseDouble(string s)
        => double.TryParse(s.Split(' ', '/')[0], out var v) ? v : null;

    private static double? ParseGps(string s)
    {
        // MetadataExtractor formats as "12° 34' 56.78\"" — strip everything but digits and dot.
        var sb = new System.Text.StringBuilder();
        bool seenDot = false;
        foreach (var ch in s)
        {
            if (char.IsDigit(ch)) sb.Append(ch);
            else if (ch == '.' && !seenDot) { sb.Append(ch); seenDot = true; }
            else if (ch == '°' || ch == '\'' || ch == '"') sb.Append(' ');
        }
        // Crude: take first numeric chunk as degrees. Good enough for sort/filter.
        return double.TryParse(sb.ToString().Trim(), out var v) ? v : null;
    }
}
