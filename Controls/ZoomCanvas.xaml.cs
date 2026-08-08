using System;
using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SkiaSharp;
using SkiaSharp.Views.Windows;

namespace Photon.Controls;

/// <summary>
/// GPU-accelerated zoomable / pannable image surface. Renders the loaded
/// <see cref="Bitmap"/> via an <see cref="SKXamlCanvas"/> and supports:
/// <list type="bullet">
///   <item>Ctrl+MouseWheel to zoom toward the cursor.</item>
///   <item>Mouse drag (or touch drag) to pan.</item>
///   <item>Double-click to toggle between fit and 100%.</item>
///   <item>Keyboard arrows / +/- / F / 1 (when focused).</item>
/// </list>
/// Min zoom 10%, max zoom 3200% per <c>Idea.md</c> spec.
/// </summary>
public sealed partial class ZoomCanvas : UserControl
{
    private SKBitmap? _bitmap;
    private SKBitmap? _overlayBitmap; // optional crop overlay, drawn on top
    private float _zoom = 1f;          // 1.0 = fit-to-canvas at first load
    private float _panX = 0f, _panY = 0f;
    private bool _isFit = true;
    private bool _isDragging;
    private System.Drawing.PointF _lastPointer;
    private bool _hadFirstFit;

    public static readonly DependencyProperty BitmapProperty =
        DependencyProperty.Register(nameof(Bitmap), typeof(SKBitmap), typeof(ZoomCanvas),
            new PropertyMetadata(null, OnBitmapChanged));

    public SKBitmap? Bitmap
    {
        get => (SKBitmap?)GetValue(BitmapProperty);
        set => SetValue(BitmapProperty, value);
    }

    /// <summary>Optional overlay (e.g. crop handles) drawn above the image.</summary>
    public SKBitmap? OverlayBitmap
    {
        get => _overlayBitmap;
        set { _overlayBitmap = value; SkiaCanvas.Invalidate(); }
    }

    public float CurrentZoom => _zoom;
    public bool IsFit => _isFit;

    public event EventHandler<float>? ZoomChanged;

    public ZoomCanvas()
    {
        this.InitializeComponent();
        Loaded += (_, _) =>
        {
            // Focusable so keyboard zoom works.
            this.Focus(FocusState.Programmatic);
        };
    }

    private static void OnBitmapChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ZoomCanvas z)
        {
            z._hadFirstFit = false;
            z.ZoomToFit();
            // If the canvas hasn't been measured yet (width < 1),
            // schedule a deferred fit after layout pass completes.
            if (z.SkiaCanvas.ActualWidth < 1)
            {
                z.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!z._hadFirstFit) z.ZoomToFit();
                });
            }
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ZoomToFitIfNeeded();

    public void ZoomToFit()
    {
        if (Bitmap is null || SkiaCanvas.ActualWidth < 1) return;
        float sx = (float)SkiaCanvas.ActualWidth  / Bitmap.Width;
        float sy = (float)SkiaCanvas.ActualHeight / Bitmap.Height;
        _zoom = Math.Min(sx, sy);
        _panX = 0;
        _panY = 0;
        _isFit = true;
        _hadFirstFit = true;
        SkiaCanvas.Invalidate();
        ZoomChanged?.Invoke(this, _zoom);
        UpdateIndicator();
    }

    private void ZoomToFitIfNeeded()
    {
        if (_isFit) ZoomToFit();
        else SkiaCanvas.Invalidate();
    }

    public void SetZoom(float newZoom, float? centerX = null, float? centerY = null)
    {
        newZoom = Math.Clamp(newZoom, 0.1f, 32f);
        float cx = centerX ?? (float)SkiaCanvas.ActualWidth  / 2f;
        float cy = centerY ?? (float)SkiaCanvas.ActualHeight / 2f;

        // Keep the point under the cursor stable while zooming.
        float imgX = (cx - (float)SkiaCanvas.ActualWidth  / 2f - _panX) / _zoom;
        float imgY = (cy - (float)SkiaCanvas.ActualHeight / 2f - _panY) / _zoom;

        _zoom = newZoom;
        _panX = cx - (float)SkiaCanvas.ActualWidth  / 2f - imgX * _zoom;
        _panY = cy - (float)SkiaCanvas.ActualHeight / 2f - imgY * _zoom;
        _isFit = false;

        SkiaCanvas.Invalidate();
        ZoomChanged?.Invoke(this, _zoom);
        UpdateIndicator();
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // Prevent rendering if layout hasn't run yet
        if (Bitmap is null || SkiaCanvas.ActualWidth < 1) return;

        // 1. Calculate the physical DPI scaling factor Windows is applying
        float dpiScale = e.Info.Width / (float)SkiaCanvas.ActualWidth;

        var halfW = (float)SkiaCanvas.ActualWidth  / 2f;
        var halfH = (float)SkiaCanvas.ActualHeight / 2f;

        canvas.Save();
        
        // 2. Scale the entire Skia surface so your logical math matches physical pixels
        canvas.Scale(dpiScale);
        // 3. Perform the exact centering math you already had
        canvas.Translate(halfW + _panX, halfH + _panY);
        canvas.Scale(_zoom);
        canvas.Translate(-Bitmap.Width / 2f, -Bitmap.Height / 2f);

        using var paint = new SKPaint { IsAntialias = true };
        using var options = new SKPaint 
        { 
            FilterQuality = _zoom > 2f ? SKFilterQuality.None : SKFilterQuality.High
        };
        canvas.DrawBitmap(Bitmap, 0, 0, options);

        if (_overlayBitmap is not null)
        {
            canvas.DrawBitmap(_overlayBitmap, 0, 0, paint);
        }

        canvas.Restore();
    }

    // --- input ---

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        CapturePointer(e.Pointer);
        _isDragging = true;
        var p = e.GetCurrentPoint(this);
        _lastPointer = new System.Drawing.PointF((float)p.Position.X, (float)p.Position.Y);
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isDragging) return;
        var p = e.GetCurrentPoint(this);
        float dx = (float)p.Position.X - _lastPointer.X;
        float dy = (float)p.Position.Y - _lastPointer.Y;
        _lastPointer = new System.Drawing.PointF((float)p.Position.X, (float)p.Position.Y);

        _panX += dx;
        _panY += dy;
        _isFit = false;
        SkiaCanvas.Invalidate();
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
        _isDragging = false;
        ReleasePointerCaptures();
    }

    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var mods = e.KeyModifiers;
        if ((mods & Windows.System.VirtualKeyModifiers.Control) == 0) return;

        var p = e.GetCurrentPoint(this);
        float factor = p.Properties.MouseWheelDelta > 0 ? 1.15f : 1f / 1.15f;
        SetZoom(_zoom * factor, (float)p.Position.X, (float)p.Position.Y);
    }

    protected override void OnDoubleTapped(DoubleTappedRoutedEventArgs e)
    {
        base.OnDoubleTapped(e);
        if (_isFit) SetZoom(1f);
        else ZoomToFit();
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);
        var step = 24f;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Up:    _panY += step; break;
            case Windows.System.VirtualKey.Down:  _panY -= step; break;
            case Windows.System.VirtualKey.Left:  _panX += step; break;
            case Windows.System.VirtualKey.Right: _panX -= step; break;
            case Windows.System.VirtualKey.Add:   SetZoom(_zoom * 1.2f); break;
            case Windows.System.VirtualKey.Subtract: SetZoom(_zoom / 1.2f); break;
            case Windows.System.VirtualKey.F:     ZoomToFit(); return;
            case Windows.System.VirtualKey.Number1: SetZoom(1f); return;
            default: return;
        }
        _isFit = false;
        SkiaCanvas.Invalidate();
    }

    private void UpdateIndicator()
    {
        if (ZoomIndicator is null) return;
        ZoomLabel.Text = $"{_zoom * 100:F0}%";
        ZoomIndicator.Visibility = _isFit ? Visibility.Collapsed : Visibility.Visible;
    }
}
