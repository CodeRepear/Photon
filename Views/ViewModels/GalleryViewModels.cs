using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media.Imaging;
using Photon.Models;

namespace Photon.Views.ViewModels;

/// <summary>One group in the gallery (e.g. "March 2024").</summary>
public sealed class GalleryGroupVm
{
    public string Label { get; }
    public string CountLabel => $"{Items.Count} item{(Items.Count == 1 ? "" : "s")}";
    public IReadOnlyList<GalleryItemVm> Items { get; }

    public GalleryGroupVm(string label, IReadOnlyList<GalleryItemVm> items)
    {
        Label = label;
        Items = items;
    }
}

/// <summary>Single thumbnail card view-model.</summary>
public sealed class GalleryItemVm : INotifyPropertyChanged
{
    public MediaItem Source { get; }
    public bool ShowVideoBadge => Source.Type == MediaType.Video;

    private BitmapImage? _thumb;
    public BitmapImage? Thumb
    {
        get => _thumb;
        set { _thumb = value; OnPropertyChanged(); }
    }

    public GalleryItemVm(MediaItem source) => Source = source;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
