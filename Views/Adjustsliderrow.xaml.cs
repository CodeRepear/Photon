using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Photon.Views;

/// <summary>
/// One adjustment row: label, slider, and a live numeric readout (e.g. "+0.35").
/// The old UI had no numeric feedback at all, which made it hard to tell how
/// far you'd pushed a slider — especially once <see cref="Value"/> steps are
/// as fine as 0.01. Double-clicking the slider resets just that one control
/// back to neutral (0), which is faster than "Reset adjustments" when you
/// only want to undo one slider.
/// </summary>
public sealed partial class AdjustSliderRow : UserControl
{
    public event EventHandler<double>? SliderChanged;

    public AdjustSliderRow()
    {
        this.InitializeComponent();
        Loaded += (_, __) => { LabelText.Text = Label; ValueSlider.Minimum = Minimum; ValueSlider.Maximum = Maximum; UpdateReadout(); };
    }

    public string Label { get; set; } = "";
    public double Minimum { get; set; } = -1;
    public double Maximum { get; set; } = 1;

    public double Value
    {
        get => ValueSlider?.Value ?? 0;
        set { if (ValueSlider is not null) ValueSlider.Value = value; }
    }

    private void OnSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateReadout();
        SliderChanged?.Invoke(this, e.NewValue);
    }

    private void OnSliderDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Value = 0;

    private void UpdateReadout()
    {
        if (ValueText is null || ValueSlider is null) return;
        var v = ValueSlider.Value;
        ValueText.Text = v == 0 ? "0" : (v > 0 ? $"+{v:F2}" : $"{v:F2}");
    }
}