using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Bluestone.Desktop.Controls;
using Bluestone.Desktop.Theme;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Views;

/// <summary>One indicator family on the Display page's stage: its tile, its heading, and the gradient it edits.</summary>
public sealed record IndicatorStageItem(MeterGradientTarget Family, GradientEditorViewModel Editor, string Title, string Description)
{
    /// <summary>The tile's spoken name: list items without one are read by their text.</summary>
    public override string ToString() => Title;
}

/// <summary>
/// The Display settings page. Its settings and their saving are in <see cref="DisplayViewModel"/>; the page keeps only
/// which indicator the stage has chosen (the one editor edits that family) and runs the color picker's edits through the
/// view model's preview seams, so a picker's live color is saved once, on Apply, and Cancel leaves nothing behind.
/// </summary>
public partial class DisplaySettingsPage : UserControl
{
    private DisplayViewModel? _display;
    private bool _syncing;

    public DisplaySettingsPage()
    {
        InitializeComponent();
        Stage.SelectionChanged += (_, _) => Select(Stage.SelectedItem as IndicatorStageItem);
        Stage.AddHandler(KeyDownEvent, OnStageKey, RoutingStrategies.Tunnel);
        MixerTarget.IsCheckedChanged += (_, _) => FromTargets(MixerTarget, 0);
        ChainTarget.IsCheckedChanged += (_, _) => FromTargets(ChainTarget, 1);
        GainTarget.IsCheckedChanged += (_, _) => FromTargets(GainTarget, 2);
        StopColourBox.TextChanged += (_, _) => MarkHex(StopColourBox);
        MidiColourBox.TextChanged += (_, _) => MarkHex(MidiColourBox);
    }

    /// <summary>"#RRGGBB" as a brush; anything else draws nothing.</summary>
    public static IValueConverter HexToBrush { get; } = new FuncValueConverter<string?, IBrush?>(hex =>
        hex is not null && Color.TryParse(hex, out var colour) ? new ImmutableSolidColorBrush(colour) : null);

    // The tiles sit in a row: Left and Right (and Home and End) choose between them, and focus follows the choice.
    private void OnStageKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || Stage.ItemCount == 0)
        {
            return;
        }

        var index = e.Key switch
        {
            Key.Left or Key.Up => Stage.SelectedIndex - 1,
            Key.Right or Key.Down => Stage.SelectedIndex + 1,
            Key.Home => 0,
            Key.End => Stage.ItemCount - 1,
            _ => -2,
        };
        if (index == -2)
        {
            return;
        }

        e.Handled = true;
        Stage.SelectedIndex = Math.Clamp(index, 0, Stage.ItemCount - 1);
        Stage.ContainerFromIndex(Stage.SelectedIndex)?.Focus(NavigationMethod.Directional);
    }

    /// <summary>The indicator the stage has chosen (its gradient is the one being edited).</summary>
    public IndicatorStageItem? Selected => Stage.SelectedItem as IndicatorStageItem;

    /// <summary>The color picker popup the chips open.</summary>
    public ColourPickerPopup ColourPicker => Picker;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ReferenceEquals(DataContext, _display))
        {
            return;
        }

        Picker.Dismiss();
        _display = DataContext as DisplayViewModel;
        if (_display is null)
        {
            Stage.ItemsSource = null;
            return;
        }

        IndicatorStageItem[] items =
        [
            new(MeterGradientTarget.Mixer, _display.Mixer, "Mixer meters", "Every mixer strip's level meter. The clip lamp stays red."),
            new(MeterGradientTarget.Chain, _display.Chain, "Device chain", "Meters between devices and each chain's Out. The clip lamp stays red."),
            new(MeterGradientTarget.Gain, _display.Gain, "Gain LEDs", "The LED scale beside each fader, from its lowest LED to its highest."),
        ];
        Stage.ItemsSource = items;
        Stage.SelectedIndex = 0;
        Select(items[0]);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Leaving the page (or Settings) cancels an open picker, so no preview outlives it.
        Picker.Dismiss();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && !IsVisible)
        {
            Picker.Dismiss();
        }
    }

    private void Select(IndicatorStageItem? item)
    {
        if (item is null)
        {
            return;
        }

        Picker.Dismiss();
        EditorPanel.DataContext = item.Editor;
        EditorTitle.Text = item.Title;
        EditorDescription.Text = item.Description;
        MidiPanel.IsVisible = item.Family == MeterGradientTarget.Chain;
        _syncing = true;
        try
        {
            MixerTarget.IsChecked = item.Family == MeterGradientTarget.Mixer;
            ChainTarget.IsChecked = item.Family == MeterGradientTarget.Chain;
            GainTarget.IsChecked = item.Family == MeterGradientTarget.Gain;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void FromTargets(RadioButton target, int index)
    {
        if (!_syncing && target.IsChecked == true && Stage.SelectedIndex != index)
        {
            Stage.SelectedIndex = index;
        }
    }

    private static void MarkHex(TextBox box) => box.Classes.Set("invalid", ColourHsv.ParseHex(box.Text) is null);

    private void OnStopChip(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } item || sender is not Control chip)
        {
            return;
        }

        // A picker still open closes first (its edit cancelled and its preview ended), so this edit's original is the
        // settled gradient, and that edit's ending cannot end this one's preview.
        Picker.Dismiss();
        var editor = item.Editor;
        var gradient = editor.Gradient;
        var index = editor.SelectedGradientStopIndex;
        if (index < 0 || index >= gradient.Stops.Length || ColourHsv.ParseHex(gradient.Stops[index].Colour) is not { } original)
        {
            return;
        }

        var context = $"{item.Title.ToUpperInvariant()}  ·  STOP {index + 1} OF {gradient.Stops.Length}  ·  {editor.SelectedGradientStopPercent}";
        editor.BeginGradientPreview();
        Picker.Show(chip, new ColourEdit(
            original,
            context,
            DisplayViewModel.GradientSwatches,
            Preview: colour => editor.SetGradientStopColour(index, ColourHsv.Hex(colour)),
            Apply: colour =>
            {
                editor.SetGradientStopColour(index, ColourHsv.Hex(colour));
                editor.EndGradientPreview();
            },
            Cancel: () =>
            {
                editor.Gradient = gradient;
                editor.EndGradientPreview();
            }));
    }

    private void OnMidiChip(object? sender, RoutedEventArgs e)
    {
        // As for a stop: any open edit is settled before this one reads its original or begins its preview.
        Picker.Dismiss();
        if (_display is not { } display || sender is not Control chip || ColourHsv.ParseHex(display.MidiColour) is not { } original)
        {
            return;
        }

        var was = display.MidiColour;
        display.BeginMidiColourPreview();
        Picker.Show(chip, new ColourEdit(
            original,
            "DEVICE CHAIN  ·  MIDI LIGHT",
            DisplayViewModel.GradientSwatches,
            Preview: colour => display.MidiColour = ColourHsv.Hex(colour),
            Apply: colour =>
            {
                display.MidiColour = ColourHsv.Hex(colour);
                display.EndMidiColourPreview();
            },
            Cancel: () =>
            {
                display.MidiColour = was;
                display.EndMidiColourPreview();
            }));
    }
}
