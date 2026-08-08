using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Photon.Core;

namespace Photon.Views;

/// <summary>
/// Application settings page. Reads from / writes to <see cref="AppSettings"/>
/// via the singleton resolved from DI. Most settings take effect immediately
/// for the next library scan or page navigation.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private AppSettings? _settings;
    private MediaLibrary? _library;

    public SettingsPage()
    {
        this.InitializeComponent();
    }
    
    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _settings = App.GetService<AppSettings>();
        _library  = App.GetService<MediaLibrary>();

        // Set the text box to show the currently active cache directory
        CachePathBox.Text = AppPaths.ThumbCacheDir;

        // Library folders
        FolderList.ItemsSource = _settings.LibraryFolders.ToList();

        // Theme
        foreach (var rb in ThemeRadio.Children.OfType<RadioButton>())
        {
            if (rb.Tag as string == _settings.AppTheme) { rb.IsChecked = true; break; }
        }

        // Gallery
        for (int i = 0; i < ThumbSizeCombo.Items.Count; i++)
        {
            if (ThumbSizeCombo.Items[i] is ComboBoxItem item && item.Tag is string t && int.Parse(t) == _settings.ThumbnailSize)
            {
                ThumbSizeCombo.SelectedIndex = i; break;
            }
        }
        SubfoldersToggle.IsOn = _settings.IncludeSubfolders;
        VideoThumbsToggle.IsOn = true; // not currently persisted — placeholder for Phase 4

        // Performance
        BgThumbsToggle.IsOn  = _settings.BackgroundThumbs;
        GpuToggle.IsOn       = _settings.GPUAcceleration;
        PrefetchToggle.IsOn  = _settings.PrefetchAdjacent;
        CacheSlider.Value    = _settings.ThumbnailCacheGB;
        CacheLabel.Text      = $"{_settings.ThumbnailCacheGB:F1} GB";
    }

    private async void OnChangeCacheFolder(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        // Update settings and global paths
        _settings.CustomThumbCachePath = folder.Path;
        AppPaths.ThumbCacheDir = folder.Path;
        _settings.Save();
        
        AppPaths.EnsureLocalFolders(); // Ensure the new directory is actually created
        CachePathBox.Text = folder.Path;
    }

    private void OnResetCacheFolder(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        
        // Clear the custom path and revert to the default AppRoot
        _settings.CustomThumbCachePath = null;
        AppPaths.ThumbCacheDir = System.IO.Path.Combine(AppPaths.AppRoot, "ThumbCache");
        _settings.Save();
        
        AppPaths.EnsureLocalFolders();
        CachePathBox.Text = AppPaths.ThumbCacheDir;
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        // Reuse the same flow as in GalleryView.
        var gv = App.MainWindow.GetContentFrame().Content as GalleryView;
        // Simpler: pick directly here.
        _ = PickFolderAsync();
    }

    private async System.Threading.Tasks.Task PickFolderAsync()
    {
        if (_settings is null) return;
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        var current = _settings.LibraryFolders.ToList();
        if (!current.Any(p => string.Equals(p, folder.Path, StringComparison.OrdinalIgnoreCase)))
        {
            current.Add(folder.Path);
            _settings.LibraryFolders = current.ToArray();
            _settings.Save();
            FolderList.ItemsSource = current.ToList();
        }
    }

    private void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        if (sender is Button b && b.Tag is string path)
        {
            var current = _settings.LibraryFolders
                .Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            _settings.LibraryFolders = current;
            _settings.Save();
            FolderList.ItemsSource = current.ToList();
        }
    }

    private void OnThemeChecked(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        if (sender is RadioButton rb && rb.Tag is string tag)
        {
            _settings.AppTheme = tag;
            _settings.Save();
            // Apply immediately if root frame exists.
            if (App.MainWindow.Content is FrameworkElement root)
            {
                root.RequestedTheme = tag switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark"  => ElementTheme.Dark,
                    _       => ElementTheme.Default,
                };
            }
        }
    }

    private void OnThumbSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settings is null) return;
        if (ThumbSizeCombo.SelectedItem is ComboBoxItem item && item.Tag is string t && int.TryParse(t, out var v))
        {
            _settings.ThumbnailSize = v;
            _settings.Save();
        }
    }

    private void OnSubfoldersToggled(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.IncludeSubfolders = SubfoldersToggle.IsOn;
        _settings.Save();
    }

    private void OnVideoThumbsToggled(object sender, RoutedEventArgs e)
    {
        // Placeholder for Phase 4 — currently always-on.
    }

    private void OnBgThumbsToggled(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.BackgroundThumbs = BgThumbsToggle.IsOn;
        _settings.Save();
    }

    private void OnGpuToggled(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.GPUAcceleration = GpuToggle.IsOn;
        _settings.Save();
    }

    private void OnPrefetchToggled(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.PrefetchAdjacent = PrefetchToggle.IsOn;
        _settings.Save();
    }

    private void OnCacheChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_settings is null) return;
        var v = Math.Round(CacheSlider.Value, 1);
        _settings.ThumbnailCacheGB = v;
        _settings.Save();
        CacheLabel.Text = $"{v:F1} GB";
    }
}
