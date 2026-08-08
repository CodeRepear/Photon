using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Dispatching;
using SkiaSharp;
using Windows.Media.Core;
using Windows.Storage.Pickers;
using Windows.Storage;
using Photon.Core;
using Photon.Decode;
using Photon.Models;
using Windows.Media.Playback;
using Photon.Share;

namespace Photon.Views;

public sealed partial class ViewerPage : Page
{
    private ViewerNavigationPayload? _payload;
    private ThumbnailEngine? _thumbs;
    private MetadataReader? _metadata;
    private LibraryDatabase? _db;
    private readonly ShareProvider _share = new();
    private int _currentIndex;
    private List<MediaItem> _siblings = new();

    private List<AnimatedFrame>? _frames;
    private int _frameIndex;
    private DispatcherTimer? _frameTimer;
    private CancellationTokenSource? _loadCts;
    private SKBitmap? _currentBitmap;

    private bool _chromeVisible = true;
    private bool _exifVisible;
    private bool _infoVisible;

    // GC guard: hold a reference to the element's internal MediaPlayer.
    // The element's Source setter creates an internal player lazily;
    // we grab it after setting the source to prevent GC from collecting it.
    private MediaPlayer? _videoPlayerGuard;

    public ViewerPage()
    {
        this.InitializeComponent();
        _thumbs = App.GetService<ThumbnailEngine>();
        _metadata = App.GetService<MetadataReader>();
        _db = App.GetService<LibraryDatabase>();
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        //if (App.MainWindow is MainWindow mainWindow) mainWindow.SetWindowTitle("");
        this.PreviewKeyDown += OnPageKeyDown;
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _frameTimer?.Stop();
        _loadCts?.Cancel();
        StopVideo();
        try { _currentBitmap?.Dispose(); _currentBitmap = null; } catch { }
        if (_frames is not null)
        {
            foreach (var f in _frames) f.Bitmap.Dispose();
            _frames = null;
        }
        this.PreviewKeyDown -= OnPageKeyDown;
    }

    private void StopVideo()
    {
        if (_videoPlayerGuard is not null)
        {
            _videoPlayerGuard.MediaOpened -= OnMediaOpened;
            _videoPlayerGuard.MediaFailed -= OnMediaFailed;
        }
        try { VideoPlayer.MediaPlayer?.Pause(); } catch { }
        try { VideoPlayer.Source = null; } catch { }
        VideoPlayer.AreTransportControlsEnabled = false;
        _videoPlayerGuard = null;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is ViewerNavigationPayload p)
        {
            _payload = p;
            _siblings = p.Siblings.ToList();
            _currentIndex = _siblings.FindIndex(i =>
                string.Equals(i.Path, p.Current.Path, StringComparison.OrdinalIgnoreCase));
            if (_currentIndex < 0) _currentIndex = 0;
            Strip.SetItems(_siblings, _thumbs, thumbSize: 72);
            Strip.SelectedItemChanged += OnFilmstripSelection;
            _ = LoadCurrentAsync();
        }
    }

    // ----- Chrome -----

    private void OnCanvasTapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe &&
            (IsChildOf(fe, ActionToolbar) ||
             IsChildOf(fe, TopLeftBar) ||
             IsChildOf(fe, PrevButton) || IsChildOf(fe, NextButton) ||
             IsChildOf(fe, FilmstripBorder) ||
             IsChildOf(fe, ExifPanel) || IsChildOf(fe, InfoStrip)))
            return;
        ToggleChrome();
    }

    private void ToggleChrome()
    {
        _chromeVisible = !_chromeVisible;
        ApplyChromeVisibility();
    }

    private static bool IsChildOf(FrameworkElement child, DependencyObject parent)
    {
        var current = child.Parent;
        while (current is not null)
        {
            if (current == parent) return true;
            current = (current as FrameworkElement)?.Parent;
        }
        return false;
    }

    private void ApplyChromeVisibility()
    {
        PrevButton.Opacity = _chromeVisible ? 0.6 : 0.0;
        PrevButton.IsHitTestVisible = _chromeVisible;

        NextButton.Opacity = _chromeVisible ? 0.6 : 0.0;
        NextButton.IsHitTestVisible = _chromeVisible;

        FilmstripRow.Height = new GridLength(_chromeVisible ? 68 : 0);
        FilmstripBorder.IsHitTestVisible = _chromeVisible;

        ActionToolbar.Opacity = _chromeVisible ? 1.0 : 0.0;
        ActionToolbar.IsHitTestVisible = _chromeVisible;

        TopLeftActions.Opacity = _chromeVisible ? 1.0 : 0.0;
        TopLeftActions.IsHitTestVisible = _chromeVisible;
    }

    private void OnNavPointerEnter(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b)
            b.Background = new SolidColorBrush(Microsoft.UI.Colors.White) { Opacity = 0.2 };
    }
    private void OnNavPointerExit(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b)
            b.Background = new SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = 0.5 };
    }
    private void OnChromePointerEnter(object sender, PointerRoutedEventArgs e) { }
    private void OnChromePointerExit(object sender, PointerRoutedEventArgs e) { }
    private void OnBtnEnter(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b)
            b.Background = new SolidColorBrush(Microsoft.UI.Colors.White) { Opacity = 0.12 };
    }
    private void OnBtnExit(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b)
            b.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    // ----- Keyboard -----

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Left: GoPrev(); e.Handled = true; break;
            case Windows.System.VirtualKey.Right: GoNext(); e.Handled = true; break;
            case Windows.System.VirtualKey.Space: ToggleChrome(); e.Handled = true; break;
            case Windows.System.VirtualKey.Escape: Frame.GoBack(); e.Handled = true; break;
            case Windows.System.VirtualKey.I:
                OnInfoToggleTap(null, null); e.Handled = true; break;
        }
    }

    // ----- Navigation -----

    private void OnPrevTap(object sender, TappedRoutedEventArgs e) { e.Handled = true; GoPrev(); }
    private void OnNextTap(object sender, TappedRoutedEventArgs e) { e.Handled = true; GoNext(); }

    private void OnFilmstripSelection(object? sender, MediaItem? item)
    {
        if (item is null) return;
        var idx = _siblings.FindIndex(i =>
            string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx != _currentIndex)
        {
            _currentIndex = idx;
            _ = LoadCurrentAsync();
        }
    }

    private void GoPrev()
    {
        if (_siblings.Count == 0) return;
        _currentIndex = (_currentIndex - 1 + _siblings.Count) % _siblings.Count;
        _ = LoadCurrentAsync();
    }
    private void GoNext()
    {
        if (_siblings.Count == 0) return;
        _currentIndex = (_currentIndex + 1) % _siblings.Count;
        _ = LoadCurrentAsync();
    }

    // ----- Load current item -----

    private async Task LoadCurrentAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        _frameTimer?.Stop(); _frameTimer = null; _frames = null;

        StopVideo();
        AnimatedSurface.Visibility = Visibility.Collapsed;
        Zoom.Visibility = Visibility.Collapsed;

        var current = _currentIndex < _siblings.Count ? _siblings[_currentIndex] : null;
        if (current is null) return;

        if (App.MainWindow is MainWindow mw)
        {
            var titleBlock = new TextBlock 
            { 
                Text = current.FileName, 
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13
            };
            mw.ConfigureTitleBar(false, titleBlock);
        }
        CounterLabel.Text = $"{_currentIndex + 1} / {_siblings.Count}";
        Strip.SelectByPath(current.Path);
        FavIcon.Glyph = (_db is not null && _db.IsFavorite(current.Path)) ? "\xE735" : "\xE734";

        // Populate info strip
        PopulateInfoStrip(current);

        try
        {
            if (current.Type == MediaType.Video)
            {
                AnimatedSurface.Visibility = Visibility.Collapsed;
                Zoom.Visibility = Visibility.Collapsed;

                var file = await StorageFile.GetFileFromPathAsync(current.Path);
                // Simple approach: set Source directly and grab the internal player as GC guard.
                // The MediaPlayerElement creates its own MediaPlayer on first Source set.
                VideoPlayer.Source = MediaSource.CreateFromStorageFile(file);

                // Grab the element's internal player to prevent GC.
                // The element's player is created lazily when Source is first set.
                _videoPlayerGuard = VideoPlayer.MediaPlayer;
                if (_videoPlayerGuard is not null)
                {
                    _videoPlayerGuard.MediaOpened += OnMediaOpened;
                    _videoPlayerGuard.MediaFailed += OnMediaFailed;
                }

                VideoPlayer.AreTransportControlsEnabled = true;
            }
            else if (current.Type == MediaType.Animation)
            {
                var dec = DecoderFactory.CreateAnimated(current.Path);
                if (dec is not null)
                {
                    _frames = (await dec.DecodeAllFramesAsync(ct)).ToList();
                    if (_frames.Count > 0)
                    {
                        ShowFrame(0);
                        AnimatedSurface.Visibility = Visibility.Visible;
                        _frameTimer = new DispatcherTimer();
                        _frameTimer.Tick += OnFrameTick;
                        _frameTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _frames[0].DelayMs));
                        _frameTimer.Start();
                    }
                }
            }
            else
            {
                var dec = DecoderFactory.Create(current.Path);
                var decoded = await dec.DecodeAsync(0, ct);
                try { _currentBitmap?.Dispose(); } catch { }
                _currentBitmap = decoded.SkiaBitmap;
                decoded.SharpImage.Dispose();
                Zoom.Visibility = Visibility.Visible;
                Zoom.Bitmap = _currentBitmap;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            //TitleLabel.Text = $"Error: {ex.Message}";
        }
    }

    // ----- Info strip -----

    private void PopulateInfoStrip(MediaItem item)
    {
        InfoDimensions.Text = $"{item.Width:N0} × {item.Height:N0}";
        InfoSize.Text = $"{item.FileSize / 1024.0:F1} KB";
        if (item.FileSize > 1024 * 1024)
            InfoSize.Text = $"{item.FileSize / (1024.0 * 1024):F1} MB";
        InfoFormat.Text = item.Format.ToUpperInvariant();
        InfoDate.Text = item.DateModified.ToString("yyyy-MM-dd HH:mm");
        InfoFileName.Text = item.FileName;
        InfoPath.Text = item.Path;

        // EXIF panel
        if (_exifVisible && _metadata is not null)
            PopulateExifPanel(item);
    }

    private void OnInfoToggleTap(object? sender, TappedRoutedEventArgs? e)
    {
        if (e is not null) e.Handled = true;
        _exifVisible = !_exifVisible;
        ExifPanel.Visibility = _exifVisible ? Visibility.Visible : Visibility.Collapsed;

        if (_exifVisible && _siblings.Count > 0 && _currentIndex < _siblings.Count)
        {
            var current = _siblings[_currentIndex];
            PopulateExifPanel(current);
        }
    }

    private void PopulateExifPanel(MediaItem item)
    {
        ExifContent.Children.Clear();
        ExifContent.Children.Add(new TextBlock
        {
            Text = "EXIF DATA",
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 100, 181, 246)),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        // Camera fields from MediaItem (already populated by MediaLibrary)
        if (item.CameraMake is not null || item.CameraModel is not null)
        {
            AddExifRow("Camera", $"{item.CameraMake} {item.CameraModel}".Trim());
        }
        if (item.LensModel is not null)
            AddExifRow("Lens", item.LensModel);
        if (item.FocalLength is not null)
            AddExifRow("Focal Length", $"{item.FocalLength:F0} mm");
        if (item.ApertureFNumber is not null)
            AddExifRow("Aperture", $"f/{item.ApertureFNumber:F1}");
        if (item.IsoSpeed is not null)
            AddExifRow("ISO", item.IsoSpeed.ToString());
        if (item.ExposureTime is not null)
            AddExifRow("Exposure", item.ExposureTime);

        if (item.Location is not null)
        {
            AddExifRow("GPS", $"{item.Location.Latitude:F4}, {item.Location.Longitude:F4}");
            // Try reverse geocode hint (just show coordinates — full geocoding needs an API)
        }

        // Read more from MetadataReader if available
        if (_metadata is not null)
        {
            try
            {
                var rows = _metadata.ReadFlat(item.Path);
                var interesting = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Date/Time Original", "Date/Time Digitized", "Flash",
                    "White Balance", "Metering Mode", "Exposure Program",
                    "Color Space", "Scene Capture Type", "Digital Zoom Ratio",
                };
                foreach (var row in rows)
                {
                    if (interesting.Contains(row.Tag))
                        AddExifRow(row.Tag, row.Value);
                }
            }
            catch { }
        }

        // Only the "EXIF DATA" header exists — nothing was actually found for this file.
        // Show an explicit empty state instead of leaving the panel looking broken.
        if (ExifContent.Children.Count == 1)
        {
            ExifContent.Children.Add(new TextBlock
            {
                Text = "No EXIF metadata found for this file.",
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 136, 136, 136)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
    }

    private void AddExifRow(string label, string value)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 2, 0, 2),
        };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 136, 136, 136)),
            Width = 110,
        });
        panel.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 11,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 204, 204, 204)),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 160,
        });
        ExifContent.Children.Add(panel);
    }

    // ----- Animation -----

    private void ShowFrame(int index)
    {
        if (_frames is null || _frames.Count == 0) return;
        _frameIndex = (index + _frames.Count) % _frames.Count;
        var frame = _frames[_frameIndex];
        var tempPath = System.IO.Path.Combine(AppPaths.ThumbCacheDir, $"anim_{_frameIndex}_{_frames.Count}.png");
        using (var skImage = SKImage.FromBitmap(frame.Bitmap))
        using (var skPng = skImage.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
        {
            using var outputStream = System.IO.File.Create(tempPath);
            outputStream.Write(skPng.ToArray());
        }
        AnimatedSurface.Source = new BitmapImage(new Uri($"file:///{tempPath}"));
    }

    private void OnFrameTick(object? sender, object e)
    {
        if (_frames is null || _frameTimer is null) return;
        ShowFrame(_frameIndex + 1);
        _frameTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _frames[_frameIndex].DelayMs));
    }

    // ----- Video events -----

    private void OnMediaOpened(MediaPlayer sender, object args) { }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        string reason = args.Error.ToString();
        string hresult = args.ExtendedErrorCode != null ? $"0x{args.ExtendedErrorCode.HResult:X8}" : string.Empty;
        string message = $"Could not play this video.\n\nReason: {reason}";
        if (!string.IsNullOrEmpty(hresult))
            message += $"\n\nError code: {hresult}";
        message += "\n\nTip: For MKV/WebM, install HEVC Video Extensions or VP9 codecs from the Microsoft Store.";
        try
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, async () =>
            {
                try
                {
                    await new ContentDialog
                    { XamlRoot = this.XamlRoot, Title = "Video Playback Failed", Content = message, CloseButtonText = "OK" }.ShowAsync();
                }
                catch { }
            });
        }
        catch { }
    }

    // ----- Toolbar actions -----

    private void OnBackTap(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        Frame.GoBack();
        if (App.MainWindow is MainWindow mw)mw.ConfigureTitleBar(false, null);
    }

    private void OnEditTap(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_siblings.Count > 0 && _currentIndex >= 0)
            Frame.Navigate(typeof(EditPage), _siblings[_currentIndex]);
    }

    private void OnFavoriteTap(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_siblings.Count == 0 || _db is null) return;
        var item = _siblings[_currentIndex];
        bool wasFav = _db.IsFavorite(item.Path);
        _db.SetFavorite(item.Path, !wasFav);
        FavIcon.Glyph = !wasFav ? "\xE735" : "\xE734";
    }

    private async void OnSaveAsTap(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_siblings.Count == 0) return;
        var item = _siblings[_currentIndex];
        var picker = new FileSavePicker();
        picker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(item.FileName);
        picker.FileTypeChoices.Add("JPEG", new[] { ".jpg" });
        picker.FileTypeChoices.Add("PNG", new[] { ".png" });
        picker.FileTypeChoices.Add("WebP", new[] { ".webp" });
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            var source = await StorageFile.GetFileFromPathAsync(item.Path);
            await source.CopyAndReplaceAsync(file);
        }
        catch (Exception ex)
        {
            await new ContentDialog
            { XamlRoot = this.XamlRoot, Title = "Save failed", Content = ex.Message, CloseButtonText = "OK" }.ShowAsync();
        }
    }

    private void OnShareTap(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_siblings.Count == 0) return;
        try { _share.ShareFiles("Photon photo", new[] { _siblings[_currentIndex].Path }); } catch { }
    }

    private void OnSlideshowTap(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_siblings.Count == 0) return;
        Frame.Navigate(typeof(SlideshowPage),
            new ViewerNavigationPayload(_siblings[_currentIndex], _siblings));
    }

    private void OnMoreTap(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Border b)
        {
            var flyout = new MenuFlyout();
            var copyImg = new MenuFlyoutItem { Text = "Copy image" }; copyImg.Click += OnCopyImage;
            var copyPath = new MenuFlyoutItem { Text = "Copy path" }; copyPath.Click += OnCopyPath;
            var props = new MenuFlyoutItem { Text = "Properties" }; props.Click += OnProperties;
            flyout.Items.Add(copyImg);
            flyout.Items.Add(copyPath);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(props);
            flyout.ShowAt(b);
        }
    }

    private async void OnCopyImage(object sender, RoutedEventArgs e)
    {
        if (_siblings.Count == 0) return;
        try { await _share.CopyImageToClipboardAsync(_siblings[_currentIndex].Path); }
        catch (Exception ex)
        {
            await new ContentDialog
            { XamlRoot = this.XamlRoot, Title = "Copy failed", Content = ex.Message, CloseButtonText = "OK" }.ShowAsync();
        }
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (_siblings.Count == 0) return;
        _share.CopyPathToClipboard(_siblings[_currentIndex].Path);
    }

    private async void OnProperties(object sender, RoutedEventArgs e)
    {
        if (_siblings.Count == 0 || _metadata is null) return;
        var item = _siblings[_currentIndex];
        var rows = _metadata.ReadFlat(item.Path);
        var list = new ListView
        {
            ItemsSource = rows.Select(r => $"{r.Directory}  ·  {r.Tag}  =  {r.Value}").ToList()
        };
        await new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = $"Properties — {item.FileName}",
            Content = new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 480,
                Padding = new Thickness(12),
            },
            CloseButtonText = "Close",
        }.ShowAsync();
    }
}