using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The picker's content (see ColourPicker.axaml). It edits one colour from <see cref="Load"/>: every change raises
/// <see cref="ColourChanged"/> so the caller can show it live; Apply (or Enter) raises <see cref="Applied"/>, and Cancel
/// (or Esc) raises <see cref="Cancelled"/>. A half-typed hex never leaves the picker: the field shows it as invalid and
/// Apply waits for six digits.
/// </summary>
public partial class ColourPicker : UserControl
{
    private ColourHsv _hsv = new(0, 0, 1);
    private Color _original = Colors.White;
    private bool _syncing;
    private bool _hexValid = true;

    public ColourPicker()
    {
        InitializeComponent();
        Wheel.PropertyChanged += (_, e) =>
        {
            if (e.Property == ColourWheel.HueProperty || e.Property == ColourWheel.SaturationProperty)
            {
                FromControls();
            }
        };
        Fader.PropertyChanged += (_, e) =>
        {
            if (e.Property == MixerValueControl.ValueProperty)
            {
                FromControls();
            }
        };
        HexBox.TextChanged += (_, _) => FromHex();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    /// <summary>The colour as it stands (live), a hex the field could not read leaves the last good one.</summary>
    public Color Colour => _hsv.ToColor();

    /// <summary>The colour being edited, as hue, saturation, and brightness.</summary>
    public ColourHsv Hsv => _hsv;

    /// <summary>The colour before this edit.</summary>
    public Color Original => _original;

    /// <summary>Whether the hex field holds a colour (Apply waits until it does).</summary>
    public bool IsHexValid => _hexValid;

    /// <summary>The colour changed (by the wheel, fader, hex, or a swatch).</summary>
    public event EventHandler<Color>? ColourChanged;

    /// <summary>Apply or Enter: keep the colour.</summary>
    public event EventHandler<Color>? Applied;

    /// <summary>Cancel or Esc: put the original back.</summary>
    public event EventHandler? Cancelled;

    /// <summary>"#RRGGBB" as a brush; anything else draws nothing.</summary>
    public static IValueConverter HexToBrush { get; } = new FuncValueConverter<string?, IBrush?>(hex =>
        ColourHsv.ParseHex(hex) is { } colour ? new ImmutableSolidColorBrush(colour) : null);

    /// <summary>Starts editing <paramref name="original"/>, titled <paramref name="context"/> ("DEVICE CHAIN · MIDI LIGHT").</summary>
    public void Load(Color original, string context, IReadOnlyList<string> swatches)
    {
        _original = original;
        ContextText.Text = context;
        SwatchList.ItemsSource = swatches;
        WasSwatch.Background = new ImmutableSolidColorBrush(original);
        Set(ColourHsv.From(original), raise: false);
    }

    /// <summary>Sets the colour being edited, as a swatch or the hex field does.</summary>
    public void SetColour(Color colour) => Set(ColourHsv.From(colour, _hsv.Hue, _hsv.Saturation), raise: true);

    /// <summary>Applies, as the Apply button does; does nothing while the hex is invalid.</summary>
    public void Apply()
    {
        if (_hexValid)
        {
            Applied?.Invoke(this, Colour);
        }
    }

    /// <summary>Cancels, as the Cancel button and Esc do.</summary>
    public void Cancel() => Cancelled?.Invoke(this, EventArgs.Empty);

    /// <summary>Focuses the wheel, where the keyboard edits the hue and saturation.</summary>
    public void FocusWheel(NavigationMethod method = NavigationMethod.Tab) => Wheel.Focus(method);

    private void Set(ColourHsv hsv, bool raise)
    {
        _hsv = hsv;
        _syncing = true;
        try
        {
            Wheel.SetCurrentValue(ColourWheel.HueProperty, hsv.Hue);
            Wheel.SetCurrentValue(ColourWheel.SaturationProperty, hsv.Saturation);
            Wheel.SetCurrentValue(ColourWheel.BrightnessProperty, hsv.Brightness);
            Fader.SetCurrentValue(BrightnessFader.HueProperty, hsv.Hue);
            Fader.SetCurrentValue(BrightnessFader.SaturationProperty, hsv.Saturation);
            Fader.SetCurrentValue(MixerValueControl.ValueProperty, hsv.Brightness * 100);
            var hex = ColourHsv.Hex(hsv.ToColor());
            if (!string.Equals(ColourHsv.ParseHex(HexBox.Text) is { } typed ? ColourHsv.Hex(typed) : null, hex, StringComparison.Ordinal))
            {
                HexBox.Text = hex;
            }
        }
        finally
        {
            _syncing = false;
        }

        SetHexValid(true);
        Show();
        if (raise)
        {
            ColourChanged?.Invoke(this, Colour);
        }
    }

    private void FromControls()
    {
        if (_syncing)
        {
            return;
        }

        Set(new ColourHsv(Wheel.Hue, Wheel.Saturation, Math.Clamp(Fader.Value / 100, 0, 1)), raise: true);
    }

    private void FromHex()
    {
        if (_syncing)
        {
            return;
        }

        if (ColourHsv.ParseHex(HexBox.Text) is { } colour)
        {
            if (colour != Colour)
            {
                SetColour(colour);
            }

            SetHexValid(true);
            Show();
        }
        else
        {
            SetHexValid(false);
            Show();
        }
    }

    private void SetHexValid(bool valid)
    {
        _hexValid = valid;
        HexBox.Classes.Set("invalid", !valid);
        ApplyButton.IsEnabled = valid;
    }

    private void Show()
    {
        NewSwatch.Background = new ImmutableSolidColorBrush(Colour);
        ReadoutText.Text = _hexValid ? _hsv.Readout : "Enter 6 hex digits";
        ReadoutText.Classes.Set("invalid", !_hexValid);
        AutomationProperties.SetHelpText(NewSwatch, ColourHsv.Hex(Colour));
    }

    private void OnSwatch(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string hex } && ColourHsv.ParseHex(hex) is { } colour)
        {
            SetColour(colour);
        }
    }

    private void OnApply(object? sender, RoutedEventArgs e) => Apply();

    private void OnCancel(object? sender, RoutedEventArgs e) => Cancel();

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            // Handled here, so Esc closes only the picker, never Settings under it.
            e.Handled = true;
            Cancel();
        }
        else if (e.Key == Key.Enter && e.Source is not Button)
        {
            e.Handled = true;
            Apply();
        }
    }
}
