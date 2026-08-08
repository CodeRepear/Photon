using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Photon.Core;
using Photon.Edit;
using Photon.Models;
using SkiaSharp;

namespace Photon.Views;

/// <summary>
/// Modal dialog for batch operations on the gallery's current selection.
/// Supports three operations: Rename (with pattern + sequence), Convert
/// (format + quality + resize + strip metadata), and Resize (by percentage
/// or max dimension). Reports progress in real time and disables the
/// primary button while work is in flight.
/// </summary>
public sealed partial class BatchOpsDialog : ContentDialog
{
    private readonly List<MediaItem> _items;
    private readonly ConversionPipeline _converter;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;

    public BatchOpsDialog(IEnumerable<MediaItem> items, ConversionPipeline converter, AppSettings settings)
    {
        this.InitializeComponent();
        _items = items.ToList();
        _converter = converter;
        _settings = settings;

        SelectionLabel.Text = $"{_items.Count} item{(_items.Count == 1 ? "" : "s")} selected";

        ResizeByPctRadio.Checked += (_, _) =>
        {
            ResizePctBox.IsEnabled = true;
            ResizePxBox.IsEnabled = false;
        };
        ResizeByPxRadio.Checked += (_, _) =>
        {
            ResizePctBox.IsEnabled = false;
            ResizePxBox.IsEnabled = true;
        };
    }

    private async void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Deferral lets us await long-running work before the dialog closes.
        var deferral = args.GetDeferral();
        args.Cancel = true; // we'll close manually when done

        _cts = new CancellationTokenSource();
        Progress.Visibility = Visibility.Visible;
        IsPrimaryButtonEnabled = false;

        try
        {
            int opIndex = OpPivot.SelectedIndex;
            switch (opIndex)
            {
                case 0: await RunRenameAsync(_cts.Token); break;
                case 1: await RunConvertAsync(_cts.Token); break;
                case 2: await RunResizeAsync(_cts.Token); break;
            }

            ProgressLabel.Text = "Done.";
            await Task.Delay(500);
            this.Hide();
        }
        catch (OperationCanceledException)
        {
            ProgressLabel.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            ProgressLabel.Text = $"Error: {ex.Message}";
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ----- operations -----

    private async Task RunRenameAsync(CancellationToken ct)
    {
        if (!int.TryParse(RenameStartBox.Text, out var start)) start = 1;
        bool keepExt = RenameKeepExtCheck.IsChecked == true;
        var pattern = RenamePatternBox.Text?.Trim();
        if (string.IsNullOrEmpty(pattern)) pattern = "Photon_{n:000}";

        int n = start;
        int done = 0;
        foreach (var item in _items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var dir = Path.GetDirectoryName(item.Path)!;
                var ext = keepExt ? Path.GetExtension(item.FileName) : "";
                var name = pattern
                    .Replace("{n}", n.ToString())
                    .Replace("{date}", item.DateCreated.ToString("yyyy-MM-dd"));
                var newPath = Path.Combine(dir, name + ext);
                if (!string.Equals(newPath, item.Path, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(item.Path, newPath);
                }
                n++;
            }
            catch (Exception ex)
            {
                ProgressLabel.Text = $"Skipped {item.FileName}: {ex.Message}";
            }
            done++;
            Progress.Value = (double)done / _items.Count * 100;
            ProgressLabel.Text = $"{done} / {_items.Count}";
            await Task.Yield();
        }
    }

    private async Task RunConvertAsync(CancellationToken ct)
    {
        var fmt = (ConvertFmtCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "JPEG";
        int qual = (int)ConvertQualSlider.Value;
        int? maxW = int.TryParse(ConvertMaxWBox.Text, out var w) ? w : null;
        int? maxH = int.TryParse(ConvertMaxHBox.Text, out var h) ? h : null;
        bool stripMeta = ConvertStripMetaCheck.IsChecked == true;

        var opts = new ConversionOptions(
            TargetFormat: fmt,
            Quality: qual,
            Lossless: false,
            MaxWidth: maxW,
            MaxHeight: maxH,
            StripMetadata: stripMeta,
            PreserveColorProfile: !stripMeta);

        // Output to a "Converted" subfolder next to the first source's parent.
        var firstDir = Path.GetDirectoryName(_items[0].Path)!;
        var outFolder = Path.Combine(firstDir, $"Converted_{fmt}");
        Directory.CreateDirectory(outFolder);

        var progress = new Progress<BatchProgress>(p =>
        {
            Progress.Value = (double)p.Completed / p.Total * 100;
            ProgressLabel.Text = $"{p.Completed} / {p.Total}  ·  {Path.GetFileName(p.CurrentFile)}";
        });

        await _converter.BatchConvertAsync(
            _items.Select(i => i.Path).ToArray(),
            outFolder,
            opts,
            progress,
            ct);
    }

    private async Task RunResizeAsync(CancellationToken ct)
    {
        bool byPct = ResizeByPctRadio.IsChecked == true;
        int pct = 100;
        int maxPx = 0;
        if (byPct)
        {
            if (!int.TryParse(ResizePctBox.Text, out pct) || pct < 1 || pct > 200) pct = 50;
        }
        else
        {
            if (!int.TryParse(ResizePxBox.Text, out maxPx) || maxPx < 1) maxPx = 1920;
        }

        int done = 0;
        foreach (var item in _items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var src = SKBitmap.Decode(item.Path);
                if (src is null) continue;

                int newW = src.Width, newH = src.Height;
                if (byPct)
                {
                    newW = (int)Math.Round(src.Width * (pct / 100.0));
                    newH = (int)Math.Round(src.Height * (pct / 100.0));
                }
                else
                {
                    double r = Math.Min(1.0, (double)maxPx / Math.Max(src.Width, src.Height));
                    newW = (int)Math.Round(src.Width * r);
                    newH = (int)Math.Round(src.Height * r);
                }

                var resized = new SKBitmap(newW, newH, src.ColorType, src.AlphaType);
                using (var canvas = new SKCanvas(resized))
                using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
                {
                    canvas.DrawBitmap(src, new SKRect(0, 0, newW, newH), paint);
                }

                var outPath = Path.Combine(
                    Path.GetDirectoryName(item.Path)!,
                    Path.GetFileNameWithoutExtension(item.FileName) + "_resized" +
                    Path.GetExtension(item.FileName));
                using (var img = SKImage.FromBitmap(resized))
                using (var data = img.Encode(SKEncodedImageFormat.Jpeg, 92))
                {
                    using var outputStream = File.Create(outPath);
                    outputStream.Write(data.ToArray());
                }
                resized.Dispose();
            }
            catch (Exception ex)
            {
                ProgressLabel.Text = $"Skipped {item.FileName}: {ex.Message}";
            }
            done++;
            Progress.Value = (double)done / _items.Count * 100;
            ProgressLabel.Text = $"{done} / {_items.Count}";
            await Task.Yield();
        }
    }
}
