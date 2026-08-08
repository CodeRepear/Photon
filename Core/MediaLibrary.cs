using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using Photon.Models;

namespace Photon.Core;

/// <summary>
/// Scans monitored folders for image / video files, builds an in-memory index,
/// and keeps it live via <see cref="FileSystemWatcher"/>. The index is the
/// single source of truth for the gallery UI. SQLite persistence is wired but
/// currently used only for favorites (Phase 4 will expand on it).
/// </summary>
public sealed class MediaLibrary
{
    public event EventHandler<MediaLibraryChangedEventArgs>? ItemsChanged;

    private readonly ILogger<MediaLibrary> _log;
    private readonly MetadataReader _metadata;
    private readonly AppSettings _settings;

    private readonly ConcurrentDictionary<string, MediaItem> _itemsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public IReadOnlyCollection<MediaItem> AllItems => _itemsByPath.Values.ToList();

    public MediaLibrary(ILogger<MediaLibrary> log, MetadataReader metadata, AppSettings settings)
    {
        _log = log;
        _metadata = metadata;
        _settings = settings;
    }

    /// <summary>Scan all configured library folders and start live-watching them.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_settings.LibraryFolders.Length == 0)
        {
            // Default to the user's Pictures folder on first launch.
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            _settings.LibraryFolders = new[] { pictures };
            _settings.Save();
        }

        await ScanAsync(ct).ConfigureAwait(false);
        StartWatching();
    }

    /// <summary>Read every supported file under each library folder.</summary>
    public async Task ScanAsync(CancellationToken ct = default)
    {
        await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _itemsByPath.Clear();

            var allFiles = new List<string>(capacity: 1024);
            foreach (var root in _settings.LibraryFolders)
            {
                if (!Directory.Exists(root))
                {
                    _log.LogWarning("Library folder missing: {Root}", root);
                    continue;
                }

                var enumOpts = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = _settings.IncludeSubfolders,
                    ReturnSpecialDirectories = false,
                };

                foreach (var file in Directory.EnumerateFiles(root, "*.*", enumOpts))
                {
                    if (FormatRegistry.SupportedReadExtensions.Contains(
                            FormatRegistry.GetExtension(file))
                        || FormatRegistry.VideoExtensions.Contains(
                            FormatRegistry.GetExtension(file)))
                    {
                        allFiles.Add(file);
                    }
                }
            }

            // Parallel enrich with metadata. Limit concurrency to 8 to avoid trashing disk.
            await Parallel.ForEachAsync(allFiles,
                new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
                async (file, token) =>
                {
                    var item = await BuildItemAsync(file, token).ConfigureAwait(false);
                    if (item is not null) _itemsByPath.TryAdd(file, item);
                }).ConfigureAwait(false);

            RaiseChanged();
            _log.LogInformation("Scan complete: {Count} items indexed", _itemsByPath.Count);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>Build a <see cref="MediaItem"/> from disk + metadata.</summary>
    private async Task<MediaItem?> BuildItemAsync(string path, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;

            var ext = FormatRegistry.GetExtension(path);
            var type = FormatRegistry.Classify(path) ?? MediaType.Image;
            var format = FormatRegistry.GetFormatLabel(path);

            // Width/Height/Camera fields via MetadataExtractor.
            // Run on a background thread because MetadataExtractor is synchronous.
            var (loc, make, model, lens, iso, aperture, exposure, focal) =
                await Task.Run(() => _metadata.ReadCameraFields(path), ct).ConfigureAwait(false);

            // Try to read width/height from the image header without decoding pixels.
            uint width = 0, height = 0;
            try
            {
                if (type != MediaType.Video)
                {
                    // For formats ImageSharp can't identify (HEIC/AVIF/JXL/RAW/PSD),
                    // route through MagickDecoder.IdentifyAsync.
                    if (FormatRegistry.MagickDecodedExtensions.Contains(ext))
                    {
                        var dims = await Photon.Decode.MagickDecoder.IdentifyAsync(path, ct).ConfigureAwait(false);
                        if (dims is { } d)
                        {
                            width = (uint)d.W;
                            height = (uint)d.H;
                        }
                    }
                    else
                    {
                        var imgInfo = await SixLabors.ImageSharp.Image.IdentifyAsync(path, ct).ConfigureAwait(false);
                        if (imgInfo is not null)
                        {
                            width = (uint)imgInfo.Width;
                            height = (uint)imgInfo.Height;
                        }
                    }
                }
            }
            catch
            {
                // Non-decodable (corrupt or unsupported) — that's fine; we'll fill in via shell later.
            }

            return new MediaItem(
                Path: path,
                FileName: System.IO.Path.GetFileName(path),
                Type: type,
                FileSize: (ulong)info.Length,
                DateCreated: info.CreationTimeUtc,
                DateModified: info.LastWriteTimeUtc,
                Width: width,
                Height: height,
                Format: format,
                Location: loc,
                CameraMake: make,
                CameraModel: model,
                LensModel: lens,
                IsoSpeed: iso,
                ApertureFNumber: aperture,
                ExposureTime: exposure,
                FocalLength: focal);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to build item for {Path}", path);
            return null;
        }
    }

    /// <summary>Attach a <see cref="FileSystemWatcher"/> to every library folder.</summary>
    private void StartWatching()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();

        foreach (var root in _settings.LibraryFolders)
        {
            if (!Directory.Exists(root)) continue;

            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = _settings.IncludeSubfolders,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            w.Created += (_, e) => OnFileChanged(e.FullPath, change: WatcherChangeTypes.Created);
            w.Deleted += (_, e) => OnFileChanged(e.FullPath, change: WatcherChangeTypes.Deleted);
            w.Changed += (_, e) => OnFileChanged(e.FullPath, change: WatcherChangeTypes.Changed);
            w.Renamed += (_, e) =>
            {
                OnFileChanged(e.OldFullPath, change: WatcherChangeTypes.Deleted);
                OnFileChanged(e.FullPath,   change: WatcherChangeTypes.Created);
            };
            w.Error += (_, e) => _log.LogError(e.GetException(), "FileSystemWatcher error on {Root}", root);

            _watchers.Add(w);
        }
    }

    private void OnFileChanged(string path, WatcherChangeTypes change)
    {
        // Debounce / re-validate on a background thread, then surface via event.
        _ = Task.Run(async () =>
        {
            await Task.Delay(150).ConfigureAwait(false); // coalesce burst writes

            var ext = FormatRegistry.GetExtension(path);
            bool supported = FormatRegistry.SupportedReadExtensions.Contains(ext)
                          || FormatRegistry.VideoExtensions.Contains(ext);
            if (!supported) return;

            if (change == WatcherChangeTypes.Deleted)
            {
                _itemsByPath.TryRemove(path, out _);
            }
            else
            {
                var item = await BuildItemAsync(path, CancellationToken.None).ConfigureAwait(false);
                if (item is not null) _itemsByPath[path] = item;
            }

            RaiseChanged();
        });
    }

    private void RaiseChanged()
        => ItemsChanged?.Invoke(this, new MediaLibraryChangedEventArgs(_itemsByPath.Values.ToList()));
}

public sealed class MediaLibraryChangedEventArgs : EventArgs
{
    public IReadOnlyList<MediaItem> Items { get; }
    public MediaLibraryChangedEventArgs(IReadOnlyList<MediaItem> items) => Items = items;
}
