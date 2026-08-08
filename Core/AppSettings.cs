using System;
using System.IO;
using System.Text.Json;

namespace Photon.Core;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string[] LibraryFolders { get; set; } = Array.Empty<string>();
    public bool IncludeSubfolders { get; set; } = true;
    public string AppTheme { get; set; } = "System";     
    public int ThumbnailSize { get; set; } = 1;            
    public string GalleryGroupBy { get; set; } = "Date";       
    public string SortOrder { get; set; } = "DateDesc";   
    public double ThumbnailCacheGB { get; set; } = 4.0;
    public bool BackgroundThumbs { get; set; } = true;
    public bool GPUAcceleration { get; set; } = true;
    public bool PrefetchAdjacent { get; set; } = true;
    public int SlideshowInterval { get; set; } = 5;            
    public string DefaultConvertFormat { get; set; } = "JPEG";
    public int DefaultJPEGQuality { get; set; } = 90;
    public bool AISubjectDetect { get; set; } = true;         

    // ADDED: The property belongs here in the data model
    public string? CustomThumbCachePath { get; set; }

    public int ThumbnailPixelSize => ThumbnailSize switch
    {
        0 => 80, 1 => 120, 2 => 180, 3 => 240, _ => 120,
    };

    private static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Photon", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var path = SettingsFilePath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    // Apply custom path on startup
                    if (!string.IsNullOrWhiteSpace(loaded.CustomThumbCachePath))
                        AppPaths.ThumbCacheDir = loaded.CustomThumbCachePath;
                        
                    return loaded;
                }
            }
        }
        catch { }

        return new AppSettings();
    }

    public void Save()
    {
        var path = SettingsFilePath;
        var dir = System.IO.Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }
}