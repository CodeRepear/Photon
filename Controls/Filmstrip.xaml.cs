using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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

namespace Photon.Controls;

/// <summary>
/// Lightweight horizontal filmstrip for the viewer.
/// Uses a ScrollViewer+StackPanel instead of ListView for smooth scrolling.
/// </summary>
public sealed partial class Filmstrip : UserControl
{
    private readonly List<ThumbSlot> _slots = new();
    private int _selectedIndex = -1;
    private MediaItem?[] _items = Array.Empty<MediaItem>();
    private ThumbnailEngine? _thumbs;
    private int _thumbSize;
    private static readonly SolidColorBrush SelectedBorder = new(Microsoft.UI.Colors.White);
    private static readonly SolidColorBrush NormalBorder = new(Microsoft.UI.ColorHelper.FromArgb(40, 255, 255, 255));
    private static readonly Thickness SelectedThickness = new(2);
    private static readonly Thickness NormalThickness = new(1);

    public event EventHandler<MediaItem?>? SelectedItemChanged;

    public Filmstrip()
    {
        this.InitializeComponent();
    }

    public void SetItems(IEnumerable<MediaItem> items, ThumbnailEngine thumbs, int thumbSize = 72)
    {
        Row.Children.Clear();
        _slots.Clear();
        _selectedIndex = -1;

        _items = items.ToArray();
        _thumbs = thumbs;
        _thumbSize = thumbSize;

        foreach (var item in _items)
        {
            var slot = new ThumbSlot(item);
            var border = CreateThumbBorder(slot, out var img);
            slot.Border = border;
            slot.Image = img;
            Row.Children.Add(border);
            _slots.Add(slot);

            // Load thumbnail asynchronously
            _ = LoadThumbAsync(slot, item);
        }
    }

    private Border CreateThumbBorder(ThumbSlot slot, out Image img)
    {
        var border = new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(1),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(25, 255, 255, 255)),
            BorderBrush = NormalBorder,
            BorderThickness = NormalThickness,
            Tag = slot,
        };

        img = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        border.Child = img;

        border.Tapped += OnThumbTapped;
        border.PointerEntered += OnThumbPointerEntered;
        border.PointerExited += OnThumbPointerExited;

        return border;
    }

    private void OnThumbTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Border b && b.Tag is ThumbSlot slot)
        {
            int idx = _slots.IndexOf(slot);
            if (idx >= 0)
            {
                SelectIndex(idx);
                SelectedItemChanged?.Invoke(this, slot.Source);
            }
        }
    }

    private void OnThumbPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b && _slots.IndexOf((ThumbSlot)b.Tag) != _selectedIndex)
        {
            b.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(40, 255, 255, 255));
        }
    }

    private void OnThumbPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b && _slots.IndexOf((ThumbSlot)b.Tag) != _selectedIndex)
        {
            b.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(25, 255, 255, 255));
        }
    }

    public void SelectByPath(string? path)
    {
        if (path is null) return;
        for (int i = 0; i < _slots.Count; i++)
        {
            if (string.Equals(_slots[i].Source.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                SelectIndex(i);
                return;
            }
        }
    }

    private void SelectIndex(int idx)
    {
        // Deselect previous
        if (_selectedIndex >= 0 && _selectedIndex < _slots.Count)
        {
            var prev = _slots[_selectedIndex].Border;
            if (prev is not null)
            {
                prev.BorderBrush = NormalBorder;
                prev.BorderThickness = NormalThickness;
                prev.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(25, 255, 255, 255));
            }
        }

        _selectedIndex = idx;

        // Highlight new
        if (_selectedIndex >= 0 && _selectedIndex < _slots.Count)
        {
            var curr = _slots[_selectedIndex].Border;
            if (curr is not null)
            {
                curr.BorderBrush = SelectedBorder;
                curr.BorderThickness = SelectedThickness;
                curr.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(50, 255, 255, 255));

                // Smooth scroll into view
                var offset = Scroller.HorizontalOffset;
                var viewport = Scroller.ViewportWidth;
                var itemX = _selectedIndex * 58.0; // 52 width + 6 spacing

                if (itemX < offset + 10)
                    Scroller.ChangeView(itemX - 10, null, null);
                else if (itemX + 52 > offset + viewport - 10)
                    Scroller.ChangeView(itemX + 52 - viewport + 10, null, null);
            }
        }
    }

    private async Task LoadThumbAsync(ThumbSlot slot, MediaItem item)
    {
        if (_thumbs is null) return;
        try
        {
            var path = await _thumbs.GetOrCreateAsync(item.Path, _thumbSize, CancellationToken.None);
            if (path is null) return;

            var capturedSlot = slot;
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var uri = new Uri("file:///" + path.Replace('\\', '/'));
                    var bmp = new BitmapImage(uri);
                    bmp.DecodePixelWidth = _thumbSize;
                    if (capturedSlot.Image is not null)
                        capturedSlot.Image.Source = bmp;
                }
                catch { }
            });
        }
        catch { }
    }

    private class ThumbSlot
    {
        public MediaItem Source { get; }
        public Border? Border { get; set; }
        public Image? Image { get; set; }
        public ThumbSlot(MediaItem source) => Source = source;
    }
}
