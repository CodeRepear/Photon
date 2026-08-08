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
using Photon.Models;

namespace Photon.Views;

/// <summary>
/// Shows only video files from the library with landscape cards.
/// Built with code-behind layout (no data templates) for reliability.
/// </summary>
public sealed partial class VideosPage : Page
{
    private MediaLibrary? _library;
    private ThumbnailEngine? _thumbs;
    private AppSettings? _settings;
    private List<MediaItem> _allItems = new();
    private string _searchText = string.Empty;
    private bool _initialized;
    private double _containerWidth = 900;

    private DispatcherTimer? _resizeTimer;
    private bool _resizeTimerInitialized;
    private int _rebuildVersion;

    private readonly Dictionary<string, BitmapImage> _thumbCache = new();
    private readonly LinkedList<string> _thumbLru = new();
    private const int MaxCachedThumbs = 200;
    private readonly Dictionary<string, Image> _imageElements = new(StringComparer.OrdinalIgnoreCase);

    public VideosPage()
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

        _library.ItemsChanged -= OnLibraryChanged;
        _library.ItemsChanged += OnLibraryChanged;

        if (_initialized) return;

        _initialized = true;

        if (_library.AllItems.Count == 0)
        {
            _ = _library.InitializeAsync();
        }
        else
        {
            Rebuild();
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_library is not null) _library.ItemsChanged -= OnLibraryChanged;
    }

    private void OnLibraryChanged(object? sender, MediaLibraryChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(() => Rebuild());

    private void OnScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double newWidth = e.NewSize.Width - 32;
        if (Math.Abs(newWidth - _containerWidth) > 20 && newWidth > 100)
        {
            _containerWidth = newWidth;
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

    private void Rebuild()
    {
        if (!_initialized || _library is null) return;

        var videoItems = (_library.AllItems ?? Array.Empty<MediaItem>())
            .Where(i => i.Type == MediaType.Video)
            .ToList();

        // Search
        var filtered = string.IsNullOrWhiteSpace(_searchText)
            ? videoItems
            : videoItems.Where(i => i.FileName.Contains(_searchText, StringComparison.OrdinalIgnoreCase)).ToList();

        // Sort
        var sortTag = (SortCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "DateDesc";
        _allItems = SortItems(filtered, sortTag);

        EmptyState.Visibility = _allItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        VideoRoot.Visibility = _allItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        _rebuildVersion++;
        BuildVisualTree();
        _ = LoadThumbsAsync();
    }

    private void BuildVisualTree()
    {
        VideoRoot.Children.Clear();
        _imageElements.Clear();

        if (_allItems.Count == 0) return;

        // Group by date
        var groups = _allItems.GroupBy(i => i.DateCreated.ToString("MMMM yyyy")).OrderByDescending(g => g.Key);

        foreach (var group in groups)
        {
            // Header
            VideoRoot.Children.Add(new TextBlock
            {
                Text = group.Key,
                Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
                Margin = new Thickness(0, 0, 0, 8),
            });

            // Grid of video cards (3 columns)
            int cols = Math.Max(1, (int)(_containerWidth / 220));
            int idx = 0;
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };

            foreach (var item in group)
            {
                double cardW = (_containerWidth - (cols - 1) * 8) / cols;
                double cardH = cardW * 9.0 / 16.0;

                var card = new Grid
                {
                    Width = cardW,
                    Height = cardH,
                    CornerRadius = new CornerRadius(8),
                    Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(2),
                    Tag = item,
                };

                var img = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                card.Children.Add(img);
                _imageElements[item.Path] = img;

                // Play icon overlay
                var playBorder = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(8),
                    CornerRadius = new CornerRadius(20),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = 0.5 },
                };
                playBorder.Child = new TextBlock { Text = "▶", FontSize = 24, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                card.Children.Add(playBorder);

                // Filename label
                var labelBorder = new Border
                {
                    VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(2, 0, 2, 2),
                    Padding = new Thickness(6, 3, 6, 3),
                };
                labelBorder.Background = new SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = 0.6 };
                labelBorder.Child = new TextBlock { Text = item.FileName, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
                card.Children.Add(labelBorder);

                card.Tapped += (s, a) => OpenViewer(item);
                ToolTipService.SetToolTip(card, item.FileName);

                rowPanel.Children.Add(card);
                idx++;

                if (idx >= cols)
                {
                    VideoRoot.Children.Add(rowPanel);
                    idx = 0;
                    rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
                }
            }
            if (rowPanel.Children.Count > 0)
                VideoRoot.Children.Add(rowPanel);

            VideoRoot.Children.Add(new Border { Height = 16 });
        }
    }

    private async Task LoadThumbsAsync()
    {
        if (_thumbs is null || _settings is null) return;
        int pixelSize = _settings.ThumbnailPixelSize;
        int myVersion = _rebuildVersion;

        foreach (var item in _allItems)
        {
            if (_rebuildVersion != myVersion) return;
            if (!_imageElements.ContainsKey(item.Path)) continue;
            try
            {
                var path = await _thumbs.GetOrCreateAsync(item.Path, pixelSize, CancellationToken.None);
                if (_rebuildVersion != myVersion) return;
                if (path is null) continue;
                var bmp = GetCachedThumb(item.Path, path);
                var capturedPath = item.Path;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_rebuildVersion != myVersion) return;
                    if (_imageElements.TryGetValue(capturedPath, out var el))
                        el.Source = bmp;
                });
            }
            catch { }
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

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _searchText = sender.Text ?? string.Empty;
        if (_initialized) Rebuild();
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized) Rebuild();
    }

    private void OpenViewer(MediaItem item)
    {
        App.MainWindow.GetContentFrame().Navigate(typeof(ViewerPage),
            new ViewerNavigationPayload(item, _allItems),
            new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
    }

    private static List<MediaItem> SortItems(List<MediaItem> items, string sortTag) => sortTag switch
    {
        "DateAsc"  => items.OrderBy(i => i.DateCreated).ToList(),
        "NameAsc"  => items.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
        "SizeDesc" => items.OrderByDescending(i => i.FileSize).ToList(),
        _          => items.OrderByDescending(i => i.DateCreated).ToList(),
    };
}
