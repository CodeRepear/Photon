using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Photon.Core;
using Photon.Edit;
using Photon.Models;

namespace Photon.Views;

/// <summary>
/// Main gallery page with a justified (Google Photos-style) layout.
/// Rows are built in code-behind for reliable sizing control.
/// Single-click opens the full-screen viewer.
/// </summary>
public sealed partial class GalleryView : Page
{
    private MediaLibrary? _library;
    private ThumbnailEngine? _thumbs;
    private AppSettings? _settings;
    private List<MediaItem> _allItems = new();
    private string _searchText = string.Empty;
    private bool _initialized;

    // Thumbnail LRU cache
    private readonly Dictionary<string, BitmapImage> _thumbCache = new();
    private readonly LinkedList<string> _thumbLru = new();
    private const int MaxCachedThumbs = 300;

    // Track container width for justified layout
    private double _containerWidth = 900;

    // Map: item Path → the Image element in the visual tree (for async thumb binding)
    private readonly Dictionary<string, Image> _imageElements = new(StringComparer.OrdinalIgnoreCase);

    // Debounce timer for resize (single instance, reused)
    private DispatcherTimer? _resizeTimer;
    private bool _resizeTimerInitialized;

    // Incremented on every rebuild; LoadThumbsAsync checks this to abort stale loads
    private int _rebuildVersion;

    public GalleryView()
    {
        this.InitializeComponent();
        this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_library == null)
        {
            _library  = App.GetService<Core.MediaLibrary>();
            _thumbs   = App.GetService<Core.ThumbnailEngine>();
            _settings = App.GetService<Core.AppSettings>();
        }

        // Always re-subscribe to events when the page becomes visible
        _library.ItemsChanged -= OnLibraryChanged;
        _library.ItemsChanged += OnLibraryChanged;

        // 2. If the page is already cached and built, stop here (instant load)
        if (_initialized) return; 

        // Sync combos to saved settings
        SelectByTag(SortCombo,  _settings?.SortOrder);
        SelectByTag(GroupCombo, _settings?.GalleryGroupBy);

        _initialized = true;

        if (_library.AllItems.Count == 0)
        {
            // 3. First launch: scan disk AND start background file watchers
            _ = _library.InitializeAsync();
        }
        else
        {
            Rebuild();
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_library is not null)
            _library.ItemsChanged -= OnLibraryChanged;
    }

    private void OnScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double newWidth = e.NewSize.Width - 32;
        if (Math.Abs(newWidth - _containerWidth) > 20 && newWidth > 100)
        {
            _containerWidth = newWidth;
            // Debounce: reuse a single timer to avoid leaking handlers
            if (!_resizeTimerInitialized)
            {
                _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _resizeTimer.Tick += (_, _) =>
                {
                    _resizeTimer?.Stop();
                    if (_initialized) { _rebuildVersion++; BuildVisualTree(); _ = LoadThumbsAsync(); }
                };
                _resizeTimerInitialized = true;
            }
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }
    }

    // ----- Drag & Drop -----

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to library";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            return;
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count == 0 || _settings is null) return;

        var firstLib = _settings.LibraryFolders.FirstOrDefault();
        if (string.IsNullOrEmpty(firstLib)) return;

        foreach (var item in items)
        {
            if (item is Windows.Storage.StorageFolder folder)
            {
                var current = _settings.LibraryFolders.ToList();
                if (!current.Any(p => string.Equals(p, folder.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    current.Add(folder.Path);
                    _settings.LibraryFolders = current.ToArray();
                    _settings.Save();
                }
            }
            else if (item is Windows.Storage.StorageFile file)
            {
                var ext = System.IO.Path.GetExtension(file.Name).TrimStart('.').ToLowerInvariant();
                if (!FormatRegistry.SupportedReadExtensions.Contains(ext) &&
                    !FormatRegistry.VideoExtensions.Contains(ext)) continue;

                try
                {
                    var destFolder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(firstLib);
                    await file.CopyAsync(destFolder, file.Name, Windows.Storage.NameCollisionOption.GenerateUniqueName);
                }
                catch { /* best-effort */ }
            }
        }

        if (_library is not null) await _library.ScanAsync();
    }

    // ----- Data flow -----

    private void OnLibraryChanged(object? sender, MediaLibraryChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(() => Rebuild());


    private void Rebuild()
    {
        if (!_initialized || _library is null) return;

        _allItems = (_library.AllItems ?? Array.Empty<MediaItem>()).ToList();

        // Search filter
        var filtered = string.IsNullOrWhiteSpace(_searchText)
            ? _allItems
            : _allItems.Where(i => i.FileName.Contains(_searchText, StringComparison.OrdinalIgnoreCase)).ToList();

        // Sort
        var sortTag = (SortCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "DateDesc";
        filtered = SortItems(filtered, sortTag);

        _allItems = filtered; // store filtered+sorted for viewer navigation

        EmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GalleryRoot.Visibility = filtered.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        _rebuildVersion++;
        BuildVisualTree();
        _ = LoadThumbsAsync();
    }
    private async void ShowAddToAlbumDialog(MediaItem item)
    {
        var db = App.GetService<LibraryDatabase>();
        var albums = db.ListAlbums();

        if (albums.Count == 0)
        {
            await new ContentDialog {
                XamlRoot = this.XamlRoot, Title = "No Albums",
                Content = "Create an album in the Albums tab first.",
                CloseButtonText = "OK"
            }.ShowAsync();
            return;
        }

        var listView = new ListView {
            ItemsSource = albums,
            DisplayMemberPath = "Name",
            SelectionMode = ListViewSelectionMode.Single,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var dialog = new ContentDialog {
            XamlRoot = this.XamlRoot,
            Title = "Add to Album",
            Content = listView,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && listView.SelectedItem is AlbumRecord selected)
        {
            db.AddToAlbum(selected.Id, item.Path);
        }
    }

    // ----- Justified layout builder -----

    /// <summary>
    /// Builds the entire gallery visual tree from scratch.
    /// Each group gets a header, then justified rows of photo cards.
    /// </summary>
    private void BuildVisualTree()
    {
        GalleryRoot.Children.Clear();
        _imageElements.Clear();

        if (_allItems.Count == 0) return;

        var groupTag = (GroupCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Date";
        var groups = GroupBy(_allItems, groupTag);

        foreach (var group in groups)
        {
            // Group header
            var headerGrid = new Grid
            {
                Margin = new Thickness(0, 0, 0, 8),
            };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleBlock = new TextBlock
            {
                Text = group.Key,
                Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            };
            Grid.SetColumn(titleBlock, 0);
            headerGrid.Children.Add(titleBlock);

            var countBlock = new TextBlock
            {
                Text = $"{group.Count()} item{(group.Count() == 1 ? "" : "s")}",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            };
            Grid.SetColumn(countBlock, 1);
            headerGrid.Children.Add(countBlock);

            GalleryRoot.Children.Add(headerGrid);

            // Build justified rows for this group
            var items = group.ToList();
            int start = 0;
            const double targetHeight = 180;
            const double spacing = 4;

            while (start < items.Count)
            {
                double totalAr = 0;
                int end = start;

                // Greedily add items until row would be too wide
                while (end < items.Count)
                {
                    double ar = GetAspectRatio(items[end]);
                    double testWidth = (totalAr + ar) * targetHeight + (end - start) * spacing;
                    if (testWidth > _containerWidth && end > start)
                        break;
                    totalAr += ar;
                    end++;
                }

                // Compute final row height to fill container width
                int count = end - start;
                double finalHeight;
                if (count == 1)
                {
                    var ar = GetAspectRatio(items[start]);
                    double w = Math.Min(ar * targetHeight, _containerWidth);
                    finalHeight = w / ar;
                    finalHeight = Math.Max(80, Math.Min(targetHeight * 1.8, finalHeight));
                }
                else
                {
                    double availWidth = _containerWidth - (count - 1) * spacing;
                    finalHeight = availWidth / totalAr;
                    finalHeight = Math.Max(targetHeight * 0.5, Math.Min(targetHeight * 1.5, finalHeight));
                }

                // Create horizontal row StackPanel
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = spacing,
                    Margin = new Thickness(0, 0, 0, spacing),
                };

                for (int i = start; i < end; i++)
                {
                    var item = items[i];
                    double ar = GetAspectRatio(item);
                    double w = ar * finalHeight;

                    var card = CreatePhotoCard(item, w, finalHeight);
                    row.Children.Add(card);
                }

                GalleryRoot.Children.Add(row);
                start = end;
            }

            // Spacing after each group
            GalleryRoot.Children.Add(new Border { Height = 16 });
        }
    }

    private Grid CreatePhotoCard(MediaItem item, double width, double height)
    {
        var card = new Grid
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            Tag = item, // store reference for click handlers
        };

        var img = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        card.Children.Add(img);

        // Track the Image element for async thumb loading
        _imageElements[item.Path] = img;

        // Video badge
        if (item.Type == MediaType.Video)
        {
            var badge = new Border
            {
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(4, 4, 0, 0),
                Padding = new Thickness(4, 1, 4, 1),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = 0.6 },
            };
            badge.Child = new TextBlock { Text = "▶", FontSize = 10, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            card.Children.Add(badge);
        }

        // Context menu
        card.RightTapped += OnItemRightTapped;
        var flyout = new MenuFlyout();
        var openItem = new MenuFlyoutItem { Text = "Open" };
        openItem.Click += (s, a) => OpenViewer(item);
        var revealItem = new MenuFlyoutItem { Text = "Open in folder" };
        revealItem.Click += (s, a) =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.Path}\""); } catch { }
        };
        var copyItem = new MenuFlyoutItem { Text = "Copy path" };
        copyItem.Click += (s, a) =>
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(item.Path);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        };
        var addToAlbumItem = new MenuFlyoutItem { Text = "Add to album..." };
        addToAlbumItem.Click += (s, a) => ShowAddToAlbumDialog(item);
        
        flyout.Items.Add(openItem);
        flyout.Items.Add(revealItem);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(copyItem);
        flyout.Items.Add(addToAlbumItem);
        card.ContextFlyout = flyout;

        // Click to open viewer
        card.Tapped += (s, a) => OpenViewer(item);
        card.DoubleTapped += (s, a) => OpenViewer(item);

        // Tooltip
        ToolTipService.SetToolTip(card, item.FileName);

        return card;
    }

    private static double GetAspectRatio(MediaItem item)
    {
        if (item.Width > 0 && item.Height > 0)
        {
            var ratio = (double)item.Width / item.Height;
            return Math.Max(0.3, Math.Min(3.0, ratio));
        }
        return 1.0;
    }

    private static IEnumerable<IGrouping<string, MediaItem>> GroupBy(List<MediaItem> items, string groupTag)
    {
        if (groupTag == "None")
            return items.GroupBy(_ => "All");

        return groupTag switch
        {
            "Folder" => items.GroupBy(i => i.Folder),
            "Format" => items.GroupBy(i => i.Format),
            _        => items.GroupBy(i => i.DateCreated.ToString("MMMM yyyy")),
        };
    }

    // ----- Thumbnail loading -----

    private async Task LoadThumbsAsync()
    {
        if (_thumbs is null || _settings is null) return;
        int pixelSize = _settings.ThumbnailPixelSize;
        int myVersion = _rebuildVersion;

        foreach (var item in _allItems)
        {
            // Abort if a newer rebuild has been triggered
            if (_rebuildVersion != myVersion) return;
            if (!_imageElements.ContainsKey(item.Path)) continue;

            try
            {
                var path = await _thumbs.GetOrCreateAsync(item.Path, pixelSize, CancellationToken.None);
                // Re-check after async gap
                if (_rebuildVersion != myVersion) return;
                if (path is null) continue;

                var bmp = GetCachedThumb(item.Path, path);

                // Marshal to UI thread to set the Image source
                var capturedPath = item.Path;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_rebuildVersion != myVersion) return;
                    if (_imageElements.TryGetValue(capturedPath, out var imgEl))
                        imgEl.Source = bmp;
                });
            }
            catch { /* swallow */ }
        }
    }

    private BitmapImage GetCachedThumb(string sourcePath, string thumbPath)
    {
        if (_thumbCache.TryGetValue(sourcePath, out var cached))
        {
            _thumbLru.Remove(sourcePath);
            _thumbLru.AddFirst(sourcePath);
            return cached;
        }

        var bmp = new BitmapImage(new Uri($"file:///{thumbPath}"));
        _thumbCache[sourcePath] = bmp;
        _thumbLru.AddFirst(sourcePath);

        while (_thumbCache.Count > MaxCachedThumbs)
        {
            var oldest = _thumbLru.Last!.Value;
            _thumbLru.RemoveLast();
            _thumbCache.Remove(oldest);
        }
        return bmp;
    }

    // ----- Command bar handlers -----

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _searchText = sender.Text ?? string.Empty;
        if (_initialized) Rebuild();
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (_settings is not null && SortCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _settings.SortOrder = tag;
            _settings.Save();
        }
        Rebuild();
    }

    private void OnGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (_settings is not null && GroupCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _settings.GalleryGroupBy = tag;
            _settings.Save();
        }
        Rebuild();
    }

    private async void OnRescanClick(object sender, RoutedEventArgs e)
    {
        if (_library is null) return;
        await _library.ScanAsync();
    }

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
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
        }

        if (_library is not null) await _library.ScanAsync();
    }

    private async void OnBatchOpsClick(object sender, RoutedEventArgs e)
    {
        if (_settings is null || _allItems.Count == 0) return;

        var converter = App.GetService<ConversionPipeline>();
        var dialog = new BatchOpsDialog(_allItems, converter, _settings)
        {
            XamlRoot = this.XamlRoot,
        };
        await dialog.ShowAsync();

        if (_library is not null) await _library.ScanAsync();
    }

    // ----- Item interaction -----

    private void OnItemRightTapped(object sender, RightTappedRoutedEventArgs e) { /* flyout handles it */ }

    private void OnContextOpen(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is MediaItem item)
            OpenViewer(item);
    }

    private void OnContextReveal(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is MediaItem item)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.Path}\""); } catch { }
        }
    }

    private void OnContextCopyPath(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is MediaItem item)
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(item.Path);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
    }

    private void OpenViewer(MediaItem item)
    {
        App.MainWindow.GetContentFrame().Navigate(typeof(ViewerPage),
            new ViewerNavigationPayload(item, _allItems),
            new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
    }

    private static void SelectByTag(ComboBox combo, string? tag)
    {
        if (string.IsNullOrEmpty(tag)) { combo.SelectedIndex = 0; return; }
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag is string t && t == tag)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static List<MediaItem> SortItems(List<MediaItem> items, string sortTag) => sortTag switch
    {
        "DateAsc"  => items.OrderBy(i => i.DateCreated).ToList(),
        "NameAsc"  => items.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
        "SizeDesc" => items.OrderByDescending(i => i.FileSize).ToList(),
        _          => items.OrderByDescending(i => i.DateCreated).ToList(),
    };
}
