using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using Windows.Storage;
using Windows.Storage.Pickers;
using Photon.Core;
using Photon.Decode;
using Photon.Edit;
using Photon.Models;
using Microsoft.UI.Xaml.Shapes;

namespace Photon.Views;

/// <summary>
/// Editor page — dark-themed, tab-based editing with a corrected Fluent icon
/// toolbar and a directly-draggable crop overlay (see the CROP section).
/// </summary>
public sealed partial class EditPage : Page
{
    private MediaItem? _item;
    private SKBitmap? _sourceBitmap;
    private SKBitmap? _workingBitmap;
    private DecodedImage? _decoded;

    private double _cropX = 0.05, _cropY = 0.05, _cropW = 0.9, _cropH = 0.9;
    private double _rotation;
    private string _aspectLabel = "Free";
    private bool _flipH, _flipV;
    private double _rotate90; // cumulative 90° rotations

    private AdjustmentState _adjust = AdjustmentState.Neutral;
    private string? _selectedFilter;
    private CancellationTokenSource? _compressCts;

    // Drag state for the crop handles, captured at DragStarted-equivalent (first delta).
    private double _dragStartCropX, _dragStartCropY, _dragStartCropW, _dragStartCropH;
    private const double MinCropSize = 0.05;

    public ObservableCollection<FilterPreviewVm> FilterPreviewVms { get; } = new();

    public EditPage()
    {
        this.InitializeComponent();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_item is not null)
        {
            TitleLabel.Text = $"Edit — {_item.FileName}";
            await LoadImageAsync();
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MediaItem mi) _item = mi;
    }

    // ---- Tab switching ----

    private void SelectTab(string tab)
    {
        SetTabBackground(TabCrop, false); SetTabBackground(TabAdjust, false);
        SetTabBackground(TabFilters, false); SetTabBackground(TabConvert, false);

        PanelCrop.Visibility = Visibility.Collapsed;
        PanelAdjust.Visibility = Visibility.Collapsed;
        PanelFilters.Visibility = Visibility.Collapsed;
        PanelConvert.Visibility = Visibility.Collapsed;

        var active = tab switch
        {
            "crop" => TabCrop, "adjust" => TabAdjust, "filters" => TabFilters, "convert" => TabConvert,
            _ => TabCrop,
        };
        SetTabBackground(active, true);

        var panel = tab switch
        {
            "crop" => PanelCrop, "adjust" => PanelAdjust, "filters" => PanelFilters, "convert" => PanelConvert,
            _ => PanelCrop,
        };
        panel.Visibility = Visibility.Visible;

        // The crop stage (fit-to-view, draggable) only makes sense while the
        // Crop tab is open; every other tab uses the normal pan/zoom canvas.
        bool cropActive = tab == "crop";
        CropStage.Visibility = cropActive ? Visibility.Visible : Visibility.Collapsed;
        Zoom.Visibility = cropActive ? Visibility.Collapsed : Visibility.Visible;
        if (cropActive) { CropCanvas.Invalidate(); UpdateCropHandlesUI(); }
    }

    private static void SetTabBackground(Border b, bool active) =>
        b.Background = new SolidColorBrush(active
            ? Microsoft.UI.ColorHelper.FromArgb(255, 45, 45, 45)
            : Microsoft.UI.Colors.Transparent);

    private void OnTabCropTap(object sender, TappedRoutedEventArgs e) => SelectTab("crop");
    private void OnTabAdjustTap(object sender, TappedRoutedEventArgs e) => SelectTab("adjust");
    private void OnTabFiltersTap(object sender, TappedRoutedEventArgs e) => SelectTab("filters");
    private void OnTabConvertTap(object sender, TappedRoutedEventArgs e) => SelectTab("convert");

    private void OnTabEnter(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b)
            b.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 50, 50, 50));
    }
    private void OnTabExit(object sender, PointerRoutedEventArgs e) { }

    private void OnToolBtnEnter(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b)
            b.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(38, 255, 255, 255));
    }
    private void OnToolBtnExit(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    // ---- Image loading ----

    private async Task LoadImageAsync()
    {
        if (_item is null) return;
        try
        {
            var dec = DecoderFactory.Create(_item.Path);
            _decoded = await dec.DecodeAsync(0);
            _sourceBitmap = _decoded.SkiaBitmap;
            _decoded.SharpImage.Dispose();

            _workingBitmap = CopyBitmap(_sourceBitmap);
            Zoom.Bitmap = _workingBitmap;

            CropXSlider.Value = _cropX; CropYSlider.Value = _cropY;
            CropWSlider.Value = _cropW; CropHSlider.Value = _cropH;
            RotationSlider.Value = _rotation;

            UpdateOverlay(); UpdateOutputPreview();
            CropCanvas.Invalidate();
            UpdateCropHandlesUI();
            await BuildFilterPreviewsAsync();
        }
        catch (Exception ex)
        {
            await new ContentDialog
            { XamlRoot = this.XamlRoot, Title = "Failed to load image", Content = ex.Message, CloseButtonText = "OK" }.ShowAsync();
            Frame.GoBack();
        }
    }

    // ==================================================================
    // CROP — interactive overlay
    //
    // The crop stage fits the whole source image into CropStage using the
    // same math as Stretch="Uniform" (see ImageRectInCanvas). Every crop
    // handle's screen position is derived from that rect + the normalized
    // (_cropX/_cropY/_cropW/_cropH) values, and every drag delta is
    // converted back from screen pixels into normalized image space by
    // dividing by the fitted image's rendered width/height. That's the
    // only piece of math this needs — it stays correct across window
    // resizes because OnCropStageSizeChanged recomputes it.
    // ==================================================================

    private void OnCropCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Black);
        if (_sourceBitmap is null) return;

        var (ox, oy, rw, rh) = ImageRectInCanvas(e.Info.Width, e.Info.Height);
        var dest = new SKRect((float)ox, (float)oy, (float)(ox + rw), (float)(oy + rh));
        using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
        canvas.DrawBitmap(_sourceBitmap, dest, paint);
    }

    private void OnCropStageSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCropHandlesUI();

    /// <summary>Where the full source image lands inside the crop stage, in stage pixels (Uniform fit).</summary>
    private (double OffsetX, double OffsetY, double RenderW, double RenderH) ImageRectInCanvas(double stageW, double stageH)
    {
        if (_sourceBitmap is null || stageW <= 0 || stageH <= 0) return (0, 0, stageW, stageH);
        double scale = Math.Min(stageW / _sourceBitmap.Width, stageH / _sourceBitmap.Height);
        double rw = _sourceBitmap.Width * scale;
        double rh = _sourceBitmap.Height * scale;
        return ((stageW - rw) / 2.0, (stageH - rh) / 2.0, rw, rh);
    }

    private void UpdateCropHandlesUI()
    {
        if (_sourceBitmap is null || CropStage.ActualWidth <= 0) return;
        var (ox, oy, rw, rh) = ImageRectInCanvas(CropStage.ActualWidth, CropStage.ActualHeight);

        double sx = ox + _cropX * rw, sy = oy + _cropY * rh;
        double sw = _cropW * rw, sh = _cropH * rh;

        Canvas.SetLeft(CropBorder, sx); Canvas.SetTop(CropBorder, sy);
        CropBorder.Width = sw; CropBorder.Height = sh;

        Canvas.SetLeft(MoveThumb, sx); Canvas.SetTop(MoveThumb, sy);
        MoveThumb.Width = sw; MoveThumb.Height = sh;

        PlaceHandle(HandleTL, sx, sy);
        PlaceHandle(HandleTR, sx + sw, sy);
        PlaceHandle(HandleBL, sx, sy + sh);
        PlaceHandle(HandleBR, sx + sw, sy + sh);
        PlaceHandle(HandleT, sx + sw / 2, sy);
        PlaceHandle(HandleB, sx + sw / 2, sy + sh);
        PlaceHandle(HandleL, sx, sy + sh / 2);
        PlaceHandle(HandleR, sx + sw, sy + sh / 2);

        // Dim outside the crop rect.
        Canvas.SetLeft(DimTop, 0); Canvas.SetTop(DimTop, 0);
        DimTop.Width = CropStage.ActualWidth; DimTop.Height = Math.Max(0, sy);

        Canvas.SetLeft(DimBottom, 0); Canvas.SetTop(DimBottom, sy + sh);
        DimBottom.Width = CropStage.ActualWidth; DimBottom.Height = Math.Max(0, CropStage.ActualHeight - (sy + sh));

        Canvas.SetLeft(DimLeft, 0); Canvas.SetTop(DimLeft, sy);
        DimLeft.Width = Math.Max(0, sx); DimLeft.Height = sh;

        Canvas.SetLeft(DimRight, sx + sw); Canvas.SetTop(DimRight, sy);
        DimRight.Width = Math.Max(0, CropStage.ActualWidth - (sx + sw)); DimRight.Height = sh;

        // Rule-of-thirds guide lines inside the crop rect.
        PlaceVLine(RuleThirdV1, sx + sw / 3, sy, sh);
        PlaceVLine(RuleThirdV2, sx + sw * 2 / 3, sy, sh);
        PlaceHLine(RuleThirdH1, sx, sy + sh / 3, sw);
        PlaceHLine(RuleThirdH2, sx, sy + sh * 2 / 3, sw);
    }

    private static void PlaceHandle(Thumb h, double centerX, double centerY)
    {
        Canvas.SetLeft(h, centerX - h.Width / 2);
        Canvas.SetTop(h, centerY - h.Height / 2);
    }
    private static void PlaceVLine(Rectangle r, double x, double y, double h)
    { Canvas.SetLeft(r, x); Canvas.SetTop(r, y); r.Width = 1; r.Height = h; }
    private static void PlaceHLine(Rectangle r, double x, double y, double w)
    { Canvas.SetLeft(r, x); Canvas.SetTop(r, y); r.Width = w; r.Height = 1; }

    private void OnMoveThumbDrag(object sender, DragDeltaEventArgs e)
    {
        if (_sourceBitmap is null) return;
        var (_, _, rw, rh) = ImageRectInCanvas(CropStage.ActualWidth, CropStage.ActualHeight);
        if (rw <= 0 || rh <= 0) return;

        _cropX = Math.Clamp(_cropX + e.HorizontalChange / rw, 0, 1 - _cropW);
        _cropY = Math.Clamp(_cropY + e.VerticalChange / rh, 0, 1 - _cropH);
        CommitCropChange();
    }

    private void OnHandleDrag(object sender, DragDeltaEventArgs e)
    {
        if (_sourceBitmap is null || sender is not FrameworkElement fe) return;
        var (_, _, rw, rh) = ImageRectInCanvas(CropStage.ActualWidth, CropStage.ActualHeight);
        if (rw <= 0 || rh <= 0) return;

        double dx = e.HorizontalChange / rw, dy = e.VerticalChange / rh;
        double x = _cropX, y = _cropY, w = _cropW, h = _cropH;

        switch (fe.Tag as string)
        {
            case "TL": x += dx; y += dy; w -= dx; h -= dy; break;
            case "TR": y += dy; w += dx; h -= dy; break;
            case "BL": x += dx; w -= dx; h += dy; break;
            case "BR": w += dx; h += dy; break;
            case "T":  y += dy; h -= dy; break;
            case "B":  h += dy; break;
            case "L":  x += dx; w -= dx; break;
            case "R":  w += dx; break;
        }

        // Clamp: never shrink below MinCropSize, never push past the image edge.
        if (w < MinCropSize) { if (fe.Tag as string is "TL" or "BL" or "L") x -= (MinCropSize - w); w = MinCropSize; }
        if (h < MinCropSize) { if (fe.Tag as string is "TL" or "TR" or "T") y -= (MinCropSize - h); h = MinCropSize; }
        x = Math.Clamp(x, 0, 1 - w);
        y = Math.Clamp(y, 0, 1 - h);
        w = Math.Clamp(w, MinCropSize, 1 - x);
        h = Math.Clamp(h, MinCropSize, 1 - y);

        _cropX = x; _cropY = y; _cropW = w; _cropH = h;

        if (_aspectLabel != "Free")
        {
            var (cx, cy, cw, ch) = CropTool.ConstrainToAspect(_cropX, _cropY, _cropW, _cropH, _aspectLabel,
                _sourceBitmap.Width, _sourceBitmap.Height);
            _cropX = cx; _cropY = cy; _cropW = cw; _cropH = ch;
        }
        CommitCropChange();
    }

    /// <summary>Push a crop-state change out to the sliders, overlay, handles, and status text.</summary>
    private void CommitCropChange()
    {
        CropXSlider.Value = _cropX; CropYSlider.Value = _cropY;
        CropWSlider.Value = _cropW; CropHSlider.Value = _cropH;
        UpdateCropHandlesUI();
        UpdateOverlay();
        UpdateOutputPreview();
    }

    private void OnAspectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AspectCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _aspectLabel = tag;
            if (_sourceBitmap is not null)
            {
                var (x, y, w, h) = CropTool.ConstrainToAspect(
                    _cropX, _cropY, _cropW, _cropH, _aspectLabel,
                    _sourceBitmap.Width, _sourceBitmap.Height);
                _cropX = x; _cropY = y; _cropW = w; _cropH = h;
            }
            CommitCropChange();
        }
    }

    private void OnRotationChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (RotationSlider is null || RotationLabel is null) return;
        _rotation = RotationSlider.Value;
        RotationLabel.Text = $"{_rotation:F1}\u00B0";
        UpdateOutputPreview();
    }

    private void OnCropSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (CropXSlider is null || CropYSlider is null || CropWSlider is null || CropHSlider is null) return;
        _cropX = CropXSlider.Value; _cropY = CropYSlider.Value;
        _cropW = CropWSlider.Value; _cropH = CropHSlider.Value;

        if (_aspectLabel != "Free" && _sourceBitmap is not null)
        {
            var (x, y, w, h) = CropTool.ConstrainToAspect(
                _cropX, _cropY, _cropW, _cropH, _aspectLabel,
                _sourceBitmap.Width, _sourceBitmap.Height);
            _cropX = x; _cropY = y; _cropW = w; _cropH = h;
        }
        UpdateCropHandlesUI(); UpdateOverlay(); UpdateOutputPreview();
    }

    private void OnResetCrop(object sender, RoutedEventArgs e)
    {
        _cropX = 0.05; _cropY = 0.05; _cropW = 0.9; _cropH = 0.9;
        CropXSlider.Value = _cropX; CropYSlider.Value = _cropY;
        CropWSlider.Value = _cropW; CropHSlider.Value = _cropH;
        _rotation = 0; RotationSlider.Value = 0;
        _flipH = false; _flipV = false; _rotate90 = 0;
        UpdateCropHandlesUI(); UpdateOverlay(); UpdateOutputPreview(); RepaintWorking();
    }

    // ---- Rotate/Flip 90° ----

    private void OnRotateLeftTap(object sender, TappedRoutedEventArgs e)
    { _rotate90 = (_rotate90 - 90) % 360; RepaintWorking(); UpdateOutputPreview(); }

    private void OnRotateRightTap(object sender, TappedRoutedEventArgs e)
    { _rotate90 = (_rotate90 + 90) % 360; RepaintWorking(); UpdateOutputPreview(); }

    private void OnFlipHorizontalTap(object sender, TappedRoutedEventArgs e)
    { _flipH = !_flipH; RepaintWorking(); UpdateOutputPreview(); }

    private void OnFlipVerticalTap(object sender, TappedRoutedEventArgs e)
    { _flipV = !_flipV; RepaintWorking(); UpdateOutputPreview(); }

    // ---- Reset ----

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in AllAdjustRows()) row.Value = 0;
        _selectedFilter = null;
        if (FilterGrid.SelectedItem is not null) FilterGrid.SelectedItem = null;
        _flipH = false; _flipV = false; _rotate90 = 0;
        OnResetCrop(sender, e);
        RepaintWorking();
    }

    private void OnResetAdjust(object sender, RoutedEventArgs e)
    {
        foreach (var row in AllAdjustRows()) row.Value = 0;
        RepaintWorking();
    }

    private IEnumerable<AdjustSliderRow> AllAdjustRows() => new[]
    {
        RowExposure, RowBrightness, RowContrast, RowHighlights, RowShadows,
        RowSaturation, RowVibrance, RowWarmth, RowTint,
        RowSharpness, RowClarity, RowVignette, RowGrain,
    };

    // ---- Adjust ----

    private void OnAdjustSliderChanged(object sender, double value)
    {
        if (RowExposure is null || RowBrightness is null) return; // controls not yet loaded

        _adjust = new AdjustmentState(
            Brightness: RowBrightness.Value,
            Contrast:   RowContrast.Value,
            Saturation: RowSaturation.Value,
            Vibrance:   RowVibrance.Value,
            Highlights: RowHighlights.Value,
            Shadows:    RowShadows.Value,
            Warmth:     RowWarmth.Value,
            Tint:       RowTint.Value,
            Sharpness:  RowSharpness.Value,
            Exposure:   RowExposure.Value,
            Clarity:    RowClarity.Value,
            Vignette:   RowVignette.Value,
            Grain:      RowGrain.Value);
        RepaintWorking(); UpdateOutputPreview();
    }

    // ---- Filters ----

    private async Task BuildFilterPreviewsAsync()
    {
        if (_sourceBitmap is null) return;
        FilterPreviewVms.Clear();
        using var thumbSrc = MakeThumb(_sourceBitmap, 72);
        foreach (var preset in FilterPipeline.Presets)
        {
            try
            {
                var preview = FilterPipeline.Apply(thumbSrc, preset);
                var path = System.IO.Path.Combine(AppPaths.ThumbCacheDir, $"filter_{preset.Name}_{Guid.NewGuid():N}.png");
                using (var img = SKImage.FromBitmap(preview))
                using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
                using (var file = File.Create(path))
                    file.Write(data.ToArray());
                preview.Dispose();
                FilterPreviewVms.Add(new FilterPreviewVm(preset.Name, new BitmapImage(new Uri($"file:///{path}"))));
            }
            catch { }
        }
    }

    private void OnFilterSelected(object sender, SelectionChangedEventArgs e)
    {
        _selectedFilter = (FilterGrid.SelectedItem is FilterPreviewVm vm) ? vm.Name : null;
        RepaintWorking(); UpdateOutputPreview();
    }

    // ---- Convert ----

    private async void OnCompressSave(object sender, RoutedEventArgs e)
    {
        if (_sourceBitmap is null || _item is null) return;
        _compressCts?.Cancel(); _compressCts = new CancellationTokenSource();
        try
        {
            var fmt = (ConvertFormatCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "JPEG";
            var picker = new FileSavePicker();
            // FIXED: Explicitly use System.IO.Path
            picker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_item.FileName) + "_compressed";
            picker.FileTypeChoices.Add(fmt, new[] { GetExtensionFor(fmt) });
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            long? maxBytes = long.TryParse(CompressTargetKBBox.Text, out var kb) ? kb * 1024 : null;

            using var finalBitmap = BuildFinalBitmap();
            // FIXED: Explicitly use System.IO.Path
            var tempPath = System.IO.Path.Combine(AppPaths.ThumbCacheDir, $"compress_{Guid.NewGuid():N}.tmp");
            await using (var fs = File.Create(tempPath))
            {
                using var img = SKImage.FromBitmap(finalBitmap);
                using var data = maxBytes is null
                    ? img.Encode(ParseSkFormat(fmt), (int)ConvertQualitySlider.Value)
                    : EncodeToTargetSize(img, fmt, maxBytes.Value, (int)ConvertQualitySlider.Value);
                data.SaveTo(fs);
            }
            File.Move(tempPath, file.Path, overwrite: true);
            var info = new FileInfo(file.Path);
            await new ContentDialog
            { XamlRoot = this.XamlRoot, Title = "Saved",
              Content = $"Saved to {file.Path}\n\nSize: {info.Length / 1024.0:F1} KB",
              CloseButtonText = "OK" }.ShowAsync();
        }
        catch (Exception ex)
        {
            await new ContentDialog
            { XamlRoot = this.XamlRoot, Title = "Failed", Content = ex.Message, CloseButtonText = "OK" }.ShowAsync();
        }
    }

    private async void OnSaveAsClick(object sender, RoutedEventArgs e)
    {
        if (_sourceBitmap is null || _item is null) return;
        var picker = new FileSavePicker();
        // FIXED: Explicitly use System.IO.Path
        picker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_item.FileName) + "_edited";
        picker.FileTypeChoices.Add("PNG", new[] { ".png" });
        picker.FileTypeChoices.Add("JPEG", new[] { ".jpg" });
        picker.FileTypeChoices.Add("WebP", new[] { ".webp" });
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            using var finalBitmap = BuildFinalBitmap();
            using var skImage = SKImage.FromBitmap(finalBitmap);
            using var encoded = file.FileType.ToLowerInvariant() switch
            {
                ".jpg"  => skImage.Encode(SKEncodedImageFormat.Jpeg, 92),
                ".webp" => skImage.Encode(SKEncodedImageFormat.Webp, 90),
                _       => skImage.Encode(SKEncodedImageFormat.Png, 100),
            };
            await using var outputStream = File.Create(file.Path);
            outputStream.Write(encoded.ToArray());
            await new ContentDialog
            { XamlRoot = this.XamlRoot, Title = "Saved", Content = $"Saved to {file.Path}", CloseButtonText = "OK" }.ShowAsync();
        }
        catch (Exception ex)
        {
            await new ContentDialog
            { XamlRoot = this.XamlRoot, Title = "Save failed", Content = ex.Message, CloseButtonText = "OK" }.ShowAsync();
        }
    }

    // ---- Repaint / build ----

    private void RepaintWorking()
    {
        if (_sourceBitmap is null) return;
        var adjustForPaint = _adjust;
        if (_selectedFilter is not null)
        {
            var preset = FilterPipeline.ByName(_selectedFilter);
            adjustForPaint = FilterPipeline.Combine(adjustForPaint, preset.Adjust);
        }

        try { _workingBitmap?.Dispose(); } catch { }
        _workingBitmap = new SKBitmap(_sourceBitmap.Width, _sourceBitmap.Height, _sourceBitmap.ColorType, _sourceBitmap.AlphaType);
        using var canvas = new SKCanvas(_workingBitmap);
        canvas.Clear();

        if (adjustForPaint.IsNeutral)
            canvas.DrawBitmap(_sourceBitmap, 0, 0);
        else
        {
            using var paint = AdjustmentEngine.BuildPaint(_sourceBitmap, adjustForPaint);
            canvas.DrawBitmap(_sourceBitmap, 0, 0, paint);
        }

        if (_selectedFilter is not null)
        {
            var preset = FilterPipeline.ByName(_selectedFilter);
            if (preset.ExtraColorMatrix is not null)
            {
                var matrix = preset.ExtraColorMatrix();
                if (matrix is not null)
                {
                    using var cf = SKColorFilter.CreateColorMatrix(matrix);
                    using var p2 = new SKPaint { ColorFilter = cf };
                    using var tmp = CopyBitmap(_workingBitmap);
                    canvas.Clear();
                    canvas.DrawBitmap(tmp, 0, 0, p2);
                }
            }
        }

        if (adjustForPaint.Vignette > 0.01)
            AdjustmentEngine.ApplyVignette(canvas, _sourceBitmap.Width, _sourceBitmap.Height, adjustForPaint.Vignette);
        if (adjustForPaint.Grain > 0.01)
            AdjustmentEngine.ApplyGrain(canvas, _sourceBitmap.Width, _sourceBitmap.Height, adjustForPaint.Grain);

        Zoom.Bitmap = _workingBitmap;
        UpdateOverlay();
    }

    private SKBitmap BuildFinalBitmap()
    {
        if (_sourceBitmap is null) throw new InvalidOperationException("No source bitmap");
        SKBitmap cropped;
        if (_cropW < 1 || _cropH < 1 || Math.Abs(_rotation) > 0.01)
            cropped = CropTool.Apply(_sourceBitmap, _cropX, _cropY, _cropW, _cropH, _rotation);
        else
            cropped = CopyBitmap(_sourceBitmap);

        if (_flipH || _flipV || _rotate90 != 0)
            cropped = ApplyTransforms(cropped);

        SKBitmap result;
        var adjustForExport = _adjust;
        if (_selectedFilter is not null)
        {
            var preset = FilterPipeline.ByName(_selectedFilter);
            adjustForExport = FilterPipeline.Combine(adjustForExport, preset.Adjust);
            result = FilterPipeline.Apply(cropped, preset, _adjust);
        }
        else if (!adjustForExport.IsNeutral)
            result = AdjustmentEngine.Apply(cropped, adjustForExport);
        else
            result = CopyBitmap(cropped);
        cropped.Dispose();
        return result;
    }

    private SKBitmap ApplyTransforms(SKBitmap src)
    {
        SKBitmap result = src;

        if (_flipH)
        {
            var flipped = new SKBitmap(src.Width, src.Height, src.ColorType, src.AlphaType);
            using var c = new SKCanvas(flipped);
            c.Scale(-1, 1, src.Width / 2f, 0);
            c.DrawBitmap(result, 0, 0);
            if (result != src) result.Dispose();
            result = flipped;
        }
        if (_flipV)
        {
            var flipped = new SKBitmap(result.Width, result.Height, result.ColorType, result.AlphaType);
            using var c = new SKCanvas(flipped);
            c.Scale(1, -1, 0, result.Height / 2f);
            c.DrawBitmap(result, 0, 0);
            if (result != src) result.Dispose();
            result = flipped;
        }

        int rotations = ((int)_rotate90 / 90 + 4) % 4;
        for (int i = 0; i < rotations; i++)
        {
            int newW = result.Height, newH = result.Width;
            var rotated = new SKBitmap(newW, newH, result.ColorType, result.AlphaType);
            using var c = new SKCanvas(rotated);
            c.Translate(newW, 0);
            c.RotateDegrees(90);
            c.DrawBitmap(result, 0, 0);
            if (result != src) result.Dispose();
            result = rotated;
        }

        if (result == src) return CopyBitmap(src);
        return result;
    }

    private void UpdateOverlay()
    {
        if (_sourceBitmap is null) return;
        Zoom.OverlayBitmap = CropTool.BuildOverlay(
            _sourceBitmap.Width, _sourceBitmap.Height, _cropX, _cropY, _cropW, _cropH);
    }

    private void UpdateOutputPreview()
    {
        if (_sourceBitmap is null || _item is null) return;
        int outW = (int)(_sourceBitmap.Width * _cropW);
        int outH = (int)(_sourceBitmap.Height * _cropH);
        OutputPreviewLabel.Text =
            $"Source: {_sourceBitmap.Width} \u00D7 {_sourceBitmap.Height} ({_item.Format})\n" +
            $"Crop:   {outW} \u00D7 {outH}\n" +
            $"Rotation: {_rotation:F1}\u00B0" +
            (_rotate90 != 0 ? $" + {_rotate90:F0}\u00B0" : "") +
            (_flipH ? " [Flipped H]" : "") + (_flipV ? " [Flipped V]" : "") +
            $"\nFilter:  {_selectedFilter ?? "None"}";
    }

    // ---- Helpers ----

    private static SKBitmap CopyBitmap(SKBitmap src)
    {
        var copy = new SKBitmap(src.Width, src.Height, src.ColorType, src.AlphaType);
        using var c = new SKCanvas(copy); c.DrawBitmap(src, 0, 0); return copy;
    }

    private static SKBitmap MakeThumb(SKBitmap src, int maxDim)
    {
        double r = Math.Min(1.0, (double)maxDim / Math.Max(src.Width, src.Height));
        int w = (int)Math.Round(src.Width * r), h = (int)Math.Round(src.Height * r);
        var thumb = new SKBitmap(w, h, src.ColorType, src.AlphaType);
        using var c = new SKCanvas(thumb); c.Clear();
        using var p = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
        c.DrawBitmap(src, new SKRect(0, 0, w, h), p); return thumb;
    }

    private static SKEncodedImageFormat ParseSkFormat(string fmt) => fmt.ToUpperInvariant() switch
    { "JPEG" or "JPG" => SKEncodedImageFormat.Jpeg, "PNG" => SKEncodedImageFormat.Png,
      "WEBP" => SKEncodedImageFormat.Webp, "BMP" => SKEncodedImageFormat.Bmp, _ => SKEncodedImageFormat.Jpeg };

    private static string GetExtensionFor(string fmt) => fmt.ToUpperInvariant() switch
    { "JPEG" or "JPG" => ".jpg", "PNG" => ".png", "WEBP" => ".webp", "BMP" => ".bmp", _ => ".jpg" };

    private static SKData EncodeToTargetSize(SKImage img, string fmt, long maxBytes, int startQuality)
    {
        var skfmt = ParseSkFormat(fmt);
        using var first = img.Encode(skfmt, startQuality);
        if (first.Size <= maxBytes) return SKData.CreateCopy(first.ToArray());
        int lo = 30, hi = startQuality; SKData best = SKData.CreateCopy(first.ToArray());
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            using var attempt = img.Encode(skfmt, mid);
            if (attempt.Size <= maxBytes) { best?.Dispose(); best = SKData.CreateCopy(attempt.ToArray()); lo = mid + 1; }
            else hi = mid - 1;
        }
        return best;
    }

    // ---- Lifecycle ----

    private void OnBackClick(object sender, TappedRoutedEventArgs e) => Frame.GoBack();

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e); _compressCts?.Cancel();
        try { _sourceBitmap?.Dispose(); } catch { }
        try { _workingBitmap?.Dispose(); } catch { }
        _sourceBitmap = null; _workingBitmap = null; _decoded = null;
    }
}

public sealed class FilterPreviewVm : INotifyPropertyChanged
{
    public string Name { get; }
    public BitmapImage Preview { get; }
    public FilterPreviewVm(string name, BitmapImage preview) { Name = name; Preview = preview; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}