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
/// Shows only the user's favorited photos. Code-behind layout for reliability.
/// </summary>
public sealed partial class FavoritesPage : Page
{
    private MediaLibrary? _library;
    private ThumbnailEngine? _thumbs;
    private LibraryDatabase? _db;
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

    public FavoritesPage()
    {
        this.InitializeComponent();
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _library  = App.GetService<MediaLibrary>();
        _thumbs   = App.GetService<ThumbnailEngine>();
        _db       = App.GetService<LibraryDatabase>();
        _settings = App.GetService<AppSettings>();

        if (_library is not null)
        {
            _library.ItemsChanged -= OnLibraryChanged;
            _library.ItemsChanged += OnLibraryChanged;
        }

        _initialized = true;
        _ = RefreshAsync();
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

    private async Task RefreshAsync()
    {
        if (_library is null) return;
        await _library.ScanAsync();
        Rebuild();
    }

    private void Rebuild()
    {
        if (!_initialized || _library is null || _db is null) return;

        var favPaths = _db.LoadAllFavoritePaths();
        var favItems = (_library.AllItems ?? Array.Empty<MediaItem>())
            .Where(i => favPaths.Contains(i.Path))
            .ToList();

        var filtered = string.IsNullOrWhiteSpace(_searchText)
            ? favItems
            : favItems.Where(i => i.FileName.Contains(_searchText, StringComparison.OrdinalIgnoreCase)).ToList();

        var sortTag = (SortCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "DateDesc";
        _allItems = SortItems(filtered, sortTag);

        EmptyState.Visibility = _allItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FavRoot.Visibility = _allItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        _rebuildVersion++;
        BuildVisualTree();
        _ = LoadThumbsAsync();
    }

    private void BuildVisualTree()
    {
        FavRoot.Children.Clear();
        _imageElements.Clear();

        if (_allItems.Count == 0) return;

        var groups = _allItems.GroupBy(i => i.DateCreated.ToString("MMMM yyyy")).OrderByDescending(g => g.Key);

        foreach (var group in groups)
        {
            FavRoot.Children.Add(new TextBlock
            {
                Text = group.Key,
                Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
                Margin = new Thickness(0, 0, 0, 8),
            });

            // Justified rows
            var items = group.ToList();
            int start = 0;
            const double targetHeight = 180;
            const double spacing = 4;

            while (start < items.Count)
            {
                double totalAr = 0;
                int end = start;

                while (end < items.Count)
                {
                    double ar = GetAspectRatio(items[end]);
                    double testWidth = (totalAr + ar) * targetHeight + (end - start) * spacing;
                    if (testWidth > _containerWidth && end > start)
                        break;
                    totalAr += ar;
                    end++;
                }

                int count = end - start;
                double finalHeight;

                if (count == 1)
                {
                    var ar = GetAspectRatio(items[start]);
                    double w = Math.Min(ar * targetHeight, _containerWidth);
                    finalHeight = Math.Max(80, Math.Min(targetHeight * 1.8, w / ar));
                }
                else
                {
                    double availWidth = _containerWidth - (count - 1) * spacing;
                    finalHeight = Math.Max(targetHeight * 0.5, Math.Min(targetHeight * 1.5, availWidth / totalAr));
                }

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

                    var card = CreateCard(item, w, finalHeight);
                    row.Children.Add(card);
                }

                FavRoot.Children.Add(row);
                start = end;
            }

            FavRoot.Children.Add(new Border { Height = 16 });
        }
    }

    private Grid CreateCard(MediaItem item, double width, double height)
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
            Tag = item,
        };

        var img = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        card.Children.Add(img);
        _imageElements[item.Path] = img;

        // Star badge
        var badge = new Border
        {
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 4, 0),
            Padding = new Thickness(4, 1, 4, 1),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Gold) { Opacity = 0.9 },
        };
        var starText = new TextBlock { Text = "★", FontSize = 10, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gold) };
        badge.Child = starText;
        card.Children.Add(badge);

        card.Tapped += (s, a) => OpenViewer(item);
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
        "DateAsc" => items.OrderBy(i => i.DateCreated).ToList(),
        "NameAsc" => items.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
        _         => items.OrderByDescending(i => i.DateCreated).ToList(),
    };
}
