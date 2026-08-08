using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage;
using Photon.Core;
using Photon.Models;

namespace Photon.Views;

/// <summary>
/// Full-screen slideshow page. Cycles through the supplied list of media
/// items at the interval set in <see cref="AppSettings.SlideshowInterval"/>.
/// Fades between images using XAML storyboard transitions. Auto-hides the
/// control bar after 3 seconds of mouse inactivity; press any key to bring
/// it back or escape to exit.
/// </summary>
public sealed partial class SlideshowPage : Page
{
    private List<MediaItem> _items = new();
    private int _index;
    private DispatcherTimer? _slideTimer;
    private DispatcherTimer? _hideControlsTimer;
    private bool _isPlaying = true;

    public SlideshowPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is ViewerNavigationPayload payload)
        {
            _items = payload.Siblings.ToList();
            _index = Math.Max(0, _items.FindIndex(i =>
                string.Equals(i.Path, payload.Current.Path, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        var settings = App.GetService<AppSettings>();
        _slideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(2, settings.SlideshowInterval)),
        };
        _slideTimer.Tick += (_, _) => GoNext();
        _slideTimer.Start();

        _hideControlsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _hideControlsTimer.Tick += (_, _) => { ControlBar.Opacity = 0; };
        _hideControlsTimer.Start();

        // Auto-hide cursor too (Windows doesn't have a managed API for this;
        // the control-bar fade is the visible cue).
        this.PointerMoved += OnPointerMoved;
        this.PreviewKeyDown += OnKeyDown;

        ShowCurrent();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _slideTimer?.Stop();
        _hideControlsTimer?.Stop();
        this.PointerMoved -= OnPointerMoved;
        this.PreviewKeyDown -= OnKeyDown;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ControlBar.Opacity = 1;
        _hideControlsTimer?.Stop();
        _hideControlsTimer?.Start();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape: OnExitClick(this, e); e.Handled = true; break;
            case Windows.System.VirtualKey.Left:   GoPrev(); e.Handled = true; break;
            case Windows.System.VirtualKey.Right:
            case Windows.System.VirtualKey.Space:  GoNext(); e.Handled = true; break;
            case Windows.System.VirtualKey.P:      OnPlayPauseClick(this, e); e.Handled = true; break;
        }
    }

    private async void ShowCurrent()
    {
        if (_items.Count == 0) return;
        var item = _items[_index];
        CounterLabel.Text = $"{_index + 1} / {_items.Count}";

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(await file.OpenReadAsync());
            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(400),
            };
            Storyboard.SetTarget(fade, CurrentImage);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(fade);

            CurrentImage.Source = bmp;
            sb.Begin();
        }
        catch { /* skip unreadable */ }
    }

    private void GoPrev()
    {
        if (_items.Count == 0) return;
        _index = (_index - 1 + _items.Count) % _items.Count;
        ShowCurrent();
    }

    private void GoNext()
    {
        if (_items.Count == 0) return;
        _index = (_index + 1) % _items.Count;
        ShowCurrent();
    }

    private void OnPrevClick(object sender, RoutedEventArgs e) { GoPrev(); }
    private void OnNextClick(object sender, RoutedEventArgs e) { GoNext(); }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        _isPlaying = !_isPlaying;
        if (_isPlaying)
        {
            _slideTimer?.Start();
            PlayPauseIcon.Text = "❚❚";
        }
        else
        {
            _slideTimer?.Stop();
            PlayPauseIcon.Text = "▶";
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        _slideTimer?.Stop();
        Frame.GoBack();
    }
}
