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
using Microsoft.UI.Xaml.Navigation;
using Photon.Core;
using Photon.Models;

namespace Photon.Views;

public sealed partial class AlbumContentPage : Page
{
    private AlbumRecord? _album;
    private MediaLibrary? _library;
    private ThumbnailEngine? _thumbs;
    private LibraryDatabase? _db;
    private AppSettings? _settings;
    
    private List<MediaItem> _allItems = new();
    private double _containerWidth = 900;
    private DispatcherTimer? _resizeTimer;
    private int _rebuildVersion;

    private readonly Dictionary<string, BitmapImage> _thumbCache = new();
    private readonly LinkedList<string> _thumbLru = new();
    private const int MaxCachedThumbs = 200;
    private readonly Dictionary<string, Image> _imageElements = new(StringComparer.OrdinalIgnoreCase);

    public AlbumContentPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is AlbumRecord record)
        {
            _album = record;
            AlbumTitle.Text = record.Name;
        }
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_album == null) return;

        _library  = App.GetService<MediaLibrary>();
        _thumbs   = App.GetService<ThumbnailEngine>();
        _db       = App.GetService<LibraryDatabase>();
        _settings = App.GetService<AppSettings>();

        Rebuild();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e) { }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow.GetContentFrame().CanGoBack)
            App.MainWindow.GetContentFrame().GoBack();
    }

    private void OnScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double newWidth = e.NewSize.Width - 32;
        if (Math.Abs(newWidth - _containerWidth) > 20 && newWidth > 100)
        {
            _containerWidth = newWidth;
            if (_resizeTimer == null)
            {
                _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                _resizeTimer.Tick += (s, args) => {
                    _resizeTimer.Stop();
                    _rebuildVersion++; 
                    BuildVisualTree(); 
                    _ = LoadThumbsAsync();
                };
            }
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }
    }

    private void Rebuild()
    {
        if (_library is null || _db is null || _album is null) return;

        var albumPaths = _db.GetAlbumItemPaths(_album.Id);
        _allItems = (_library.AllItems ?? Array.Empty<MediaItem>())
            .Where(i => albumPaths.Contains(i.Path))
            .OrderByDescending(i => i.DateCreated)
            .ToList();

        EmptyState.Visibility = _allItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AlbumRoot.Visibility = _allItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        _rebuildVersion++;
        BuildVisualTree();
        _ = LoadThumbsAsync();
    }

    private void BuildVisualTree()
    {
        AlbumRoot.Children.Clear();
        _imageElements.Clear();
        if (_allItems.Count == 0) return;

        int start = 0;
        const double targetHeight = 180;
        const double spacing = 4;

        while (start < _allItems.Count)
        {
            double totalAr = 0;
            int end = start;

            while (end < _allItems.Count)
            {
                double ar = GetAspectRatio(_allItems[end]);
                double testWidth = (totalAr + ar) * targetHeight + (end - start) * spacing;
                if (testWidth > _containerWidth && end > start) break;
                totalAr += ar;
                end++;
            }

            int count = end - start;
            double finalHeight;

            if (count == 1) {
                var ar = GetAspectRatio(_allItems[start]);
                double w = Math.Min(ar * targetHeight, _containerWidth);
                finalHeight = Math.Max(80, Math.Min(targetHeight * 1.8, w / ar));
            } else {
                double availWidth = _containerWidth - (count - 1) * spacing;
                finalHeight = Math.Max(targetHeight * 0.5, Math.Min(targetHeight * 1.5, availWidth / totalAr));
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, Margin = new Thickness(0, 0, 0, spacing) };

            for (int i = start; i < end; i++)
            {
                var item = _allItems[i];
                double ar = GetAspectRatio(item);
                double w = ar * finalHeight;
                row.Children.Add(CreateCard(item, w, finalHeight));
            }

            AlbumRoot.Children.Add(row);
            start = end;
        }
    }

    private Grid CreateCard(MediaItem item, double width, double height)
    {
        var card = new Grid
        {
            Width = width, Height = height, CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1), Padding = new Thickness(2), Tag = item,
        };

        var img = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        card.Children.Add(img);
        _imageElements[item.Path] = img;

        if (item.Type == MediaType.Video)
        {
            var badge = new Border {
                VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(4, 4, 0, 0), Padding = new Thickness(4, 1, 4, 1),
                CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = 0.6 }
            };
            badge.Child = new TextBlock { Text = "▶", FontSize = 10, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            card.Children.Add(badge);
        }

        // Context Menu for removing from album
        card.RightTapped += (s, e) => {
            var flyout = new MenuFlyout();
            var remove = new MenuFlyoutItem { Text = "Remove from album" };
            remove.Click += (sender, args) => {
                _db?.RemoveFromAlbum(_album!.Id, item.Path);
                Rebuild();
            };
            flyout.Items.Add(remove);
            flyout.ShowAt((FrameworkElement)s, e.GetPosition((UIElement)s));
        };

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
                    if (_imageElements.TryGetValue(capturedPath, out var el)) el.Source = bmp;
                });
            } catch { }
        }
    }

    private BitmapImage GetCachedThumb(string sourcePath, string thumbPath)
    {
        if (_thumbCache.TryGetValue(sourcePath, out var cached))
        {
            _thumbLru.Remove(sourcePath); _thumbLru.AddFirst(sourcePath);
            return cached;
        }
        var bmp = new BitmapImage(new Uri($"file:///{thumbPath}"));
        _thumbCache[sourcePath] = bmp;
        _thumbLru.AddFirst(sourcePath);
        while (_thumbCache.Count > MaxCachedThumbs)
        {
            var oldest = _thumbLru.Last!.Value;
            _thumbLru.RemoveLast(); _thumbCache.Remove(oldest);
        }
        return bmp;
    }

    private void OpenViewer(MediaItem item)
    {
        App.MainWindow.GetContentFrame().Navigate(typeof(ViewerPage),
            new ViewerNavigationPayload(item, _allItems),
            new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
    }
}