using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using Photon.AI;

namespace Photon.Controls;

/// <summary>
/// Translucent overlay drawn above the viewer's image to highlight detected
/// subjects. Each subject is rendered as a colored bounding box with a label
/// pill at the top-left. Tapping a box raises <see cref="SubjectTapped"/>.
///
/// The overlay is sized to match the underlying image and positioned by the
/// viewer's layout; we don't try to sync pan/zoom — the viewer hides the
/// overlay while zoomed in (kept simple for Phase 4).
/// </summary>
public sealed partial class SubjectOverlay : UserControl
{
    private IReadOnlyList<DetectedSubject> _subjects = Array.Empty<DetectedSubject>();
    private int _imageWidth, _imageHeight;

    public static readonly DependencyProperty SubjectsProperty =
        DependencyProperty.Register(nameof(Subjects), typeof(IReadOnlyList<DetectedSubject>),
            typeof(SubjectOverlay), new PropertyMetadata(null, OnSubjectsChanged));

    public IReadOnlyList<DetectedSubject> Subjects
    {
        get => (IReadOnlyList<DetectedSubject>)GetValue(SubjectsProperty);
        set => SetValue(SubjectsProperty, value);
    }

    public int ImageWidth
    {
        get => _imageWidth;
        set { _imageWidth = value; Canvas.Invalidate(); }
    }

    public int ImageHeight
    {
        get => _imageHeight;
        set { _imageHeight = value; Canvas.Invalidate(); }
    }

    public event EventHandler<DetectedSubject>? SubjectTapped;

    // Color palette cycling through high-contrast hues for class differentiation.
    private static readonly SKColor[] Palette =
    {
        new(0xFF, 0x4F, 0x4F),  // red
        new(0x4F, 0xFF, 0x4F),  // green
        new(0x4F, 0x4F, 0xFF),  // blue
        new(0xFF, 0xFF, 0x4F),  // yellow
        new(0xFF, 0x4F, 0xFF),  // magenta
        new(0x4F, 0xFF, 0xFF),  // cyan
        new(0xFF, 0xA5, 0x00),  // orange
    };

    public SubjectOverlay()
    {
        this.InitializeComponent();
    }

    private static void OnSubjectsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SubjectOverlay o)
        {
            o._subjects = (IReadOnlyList<DetectedSubject>)(e.NewValue ?? Array.Empty<DetectedSubject>());
            o.Canvas.Invalidate();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Canvas.Invalidate();

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        if (_subjects.Count == 0 || _imageWidth == 0 || _imageHeight == 0) return;

        // Compute the scale that fits the image into the canvas (Uniform mode).
        float sx = (float)Canvas.ActualWidth  / _imageWidth;
        float sy = (float)Canvas.ActualHeight / _imageHeight;
        float s = Math.Min(sx, sy);
        // And the offset to center it.
        float offX = ((float)Canvas.ActualWidth  - _imageWidth  * s) / 2f;
        float offY = ((float)Canvas.ActualHeight - _imageHeight * s) / 2f;

        canvas.Save();
        canvas.Translate(offX, offY);
        canvas.Scale(s);

        for (int i = 0; i < _subjects.Count; i++)
        {
            var subj = _subjects[i];
            var color = Palette[i % Palette.Length];

            using var boxPaint = new SKPaint
            {
                Color = color,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(2, _imageWidth / 300f),
                IsAntialias = true,
            };
            canvas.DrawRect(subj.BoundingBox, boxPaint);

            // Label pill.
            float fontSize = Math.Max(12, _imageWidth / 60f);
            using var font = new SKFont(SKTypeface.Default, fontSize);
            using var textPaint = new SKPaint
            {
                Color = SKColors.White,
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
            };
            var label = $"{subj.Label}  {(int)(subj.Confidence * 100)}%";
            var textWidth = textPaint.MeasureText(label);
            float pillH = fontSize * 1.4f;
            var pillRect = new SKRect(
                subj.BoundingBox.Left,
                subj.BoundingBox.Top - pillH,
                subj.BoundingBox.Left + textWidth + 8,
                subj.BoundingBox.Top);

            using var pillPaint = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRect(pillRect, pillPaint);

            canvas.DrawText(label, pillRect.Left + 4, pillRect.Bottom - fontSize * 0.25f, font, textPaint);
        }

        canvas.Restore();
    }

    protected override void OnTapped(Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        base.OnTapped(e);
        if (_subjects.Count == 0) return;

        // Hit-test against each box (in canvas-pixel space).
        var pos = e.GetPosition(Canvas);
        float px = (float)pos.X;
        float py = (float)pos.Y;

        // Reverse the canvas transform.
        float sx = (float)Canvas.ActualWidth  / _imageWidth;
        float sy = (float)Canvas.ActualHeight / _imageHeight;
        float s = Math.Min(sx, sy);
        float offX = ((float)Canvas.ActualWidth  - _imageWidth  * s) / 2f;
        float offY = ((float)Canvas.ActualHeight - _imageHeight * s) / 2f;
        float imgX = (px - offX) / s;
        float imgY = (py - offY) / s;

        foreach (var subj in _subjects)
        {
            if (subj.BoundingBox.Contains(imgX, imgY))
            {
                SubjectTapped?.Invoke(this, subj);
                return;
            }
        }
    }
}
