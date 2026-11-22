using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>
/// Edits one <see cref="SignalGradient"/>: its stops, the selected stop's colour and position, and a drag preview. It does not
/// save; its owner (<see cref="DisplayViewModel"/>) saves when <see cref="Gradient"/> changes, and holds that save while
/// <see cref="IsPreviewing"/>.
/// </summary>
public sealed partial class GradientEditorViewModel : ObservableObject
{
    public GradientEditorViewModel(string name, SignalGradient defaultGradient, bool hasClipLamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        DefaultGradient = defaultGradient ?? throw new ArgumentNullException(nameof(defaultGradient));
        HasClipLamp = hasClipLamp;
        Gradient = defaultGradient;
    }

    /// <summary>What the musician reads for this gradient, for example "Mixer meters".</summary>
    public string Name { get; }

    /// <summary>What <see cref="ResetGradientCommand"/> puts back.</summary>
    public SignalGradient DefaultGradient { get; }

    /// <summary>Whether the indicators this gradient colours have a clip lamp above them (in its own fixed colour, not part of the gradient).</summary>
    public bool HasClipLamp { get; }

    /// <summary>Colours offered as one-click swatches: the Machined meter and signal colours. Clip red is the clip lamp's alone.</summary>
    public static IReadOnlyList<string> Swatches { get; } =
        ["#34C6F4", "#3FE0D8", "#4FB3B0", "#4CB873", "#3D5BFF", "#7C6BFF", "#B65CFF", "#E070C8", "#FF2E93", "#E0A43A", "#E3C345", "#DADCE0"];

    /// <summary>The colours the level runs through, bottom to top.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedGradientStop), nameof(SelectedGradientStopPercent), nameof(SelectedGradientStopColour), nameof(CanAddGradientStop), nameof(CanRemoveGradientStop))]
    [NotifyCanExecuteChangedFor(nameof(AddGradientStopInLargestGapCommand), nameof(RemoveSelectedGradientStopCommand))]
    public partial SignalGradient Gradient { get; set; }

    /// <summary>The stop the editor is working on (its index in <see cref="Gradient"/>'s stops).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedGradientStop), nameof(SelectedGradientStopPercent), nameof(SelectedGradientStopColour))]
    public partial int SelectedGradientStopIndex { get; set; }

    /// <summary>Whether a preview (a handle being dragged) is open: edits apply at once, but are not saved until it ends.</summary>
    [ObservableProperty]
    public partial bool IsPreviewing { get; private set; }

    /// <summary>The selected stop.</summary>
    public SignalGradientStop SelectedGradientStop => Gradient.Stops[Math.Clamp(SelectedGradientStopIndex, 0, Gradient.Stops.Length - 1)];

    /// <summary>The selected stop's position as the editor shows it, for example "40%".</summary>
    public string SelectedGradientStopPercent => FormatPercent(SelectedGradientStop.Position);

    /// <summary>
    /// The selected stop's colour as "#RRGGBB". Setting it to six hex digits (with or without the '#') recolours the stop;
    /// anything else is ignored, so a half-typed value never reaches the meters.
    /// </summary>
    public string SelectedGradientStopColour
    {
        get => SelectedGradientStop.Colour;
        set
        {
            var colour = NormalizeColour(value);
            if (colour is not null)
            {
                SetGradientStopColour(Math.Clamp(SelectedGradientStopIndex, 0, Gradient.Stops.Length - 1), colour);
            }
        }
    }

    /// <summary>Whether a stop can be removed: a gradient keeps at least <see cref="SignalGradient.MinStops"/>.</summary>
    public bool CanRemoveGradientStop => Gradient.Stops.Length > SignalGradient.MinStops;

    /// <summary>Whether a stop can be added: a gradient has at most <see cref="SignalGradient.MaxStops"/>.</summary>
    public bool CanAddGradientStop => Gradient.Stops.Length < SignalGradient.MaxStops;

    /// <summary>"40%": a position in [0, 1] as a whole percentage.</summary>
    public static string FormatPercent(double position) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Math.Round(position * 100):0}%");

    /// <summary>"#RRGGBB" (upper case) from six hex digits with or without the '#'; null for anything else.</summary>
    internal static string? NormalizeColour(string? value)
    {
        var colour = value?.Trim();
        colour = colour is { Length: 6 } ? "#" + colour : colour;
        return SignalGradient.IsColour(colour) ? colour!.ToUpperInvariant() : null;
    }

    public override string ToString() => Name;

    /// <summary>Adds a stop at the middle of the widest gap between stops (the colour already there) and selects it.</summary>
    [RelayCommand(CanExecute = nameof(CanAddGradientStop))]
    private void AddGradientStopInLargestGap()
    {
        var stops = Gradient.Stops;
        var at = 0;
        for (var i = 1; i < stops.Length - 1; i++)
        {
            if (stops[i + 1].Position - stops[i].Position > stops[at + 1].Position - stops[at].Position)
            {
                at = i;
            }
        }

        var index = AddGradientStop((stops[at].Position + stops[at + 1].Position) / 2);
        if (index >= 0)
        {
            SelectedGradientStopIndex = index;
        }
    }

    /// <summary>Removes the selected stop and selects its neighbour below (or the first).</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveGradientStop))]
    private void RemoveSelectedGradientStop()
    {
        var index = Math.Clamp(SelectedGradientStopIndex, 0, Gradient.Stops.Length - 1);
        if (RemoveGradientStop(index))
        {
            SelectedGradientStopIndex = Math.Max(0, index - 1);
        }
    }

    /// <summary>Recolours the selected stop with a swatch colour ("#RRGGBB").</summary>
    [RelayCommand]
    private void ChooseGradientStopColour(string? colour) => SelectedGradientStopColour = colour ?? string.Empty;

    /// <summary>Moves the stop at <paramref name="index"/> to <paramref name="position"/> and selects it wherever it lands.</summary>
    public void MoveAndSelectGradientStop(int index, double position)
    {
        var landed = MoveGradientStop(index, position);
        if (landed >= 0)
        {
            SelectedGradientStopIndex = landed;
        }
    }

    /// <summary>
    /// Starts a preview (a handle being dragged): edits still apply and notify at once, so meters follow, but nothing is
    /// written until <see cref="EndGradientPreview"/>. Starting one while one is open does nothing.
    /// </summary>
    public void BeginGradientPreview() => IsPreviewing = true;

    /// <summary>Ends a preview; the owner saves once if anything changed during it. Ending one that is not open does nothing.</summary>
    public void EndGradientPreview() => IsPreviewing = false;

    /// <summary>Nudges the stop at <paramref name="index"/> by <paramref name="delta"/> (0.01 is 1%) and selects it wherever it lands.</summary>
    public void NudgeGradientStop(int index, double delta)
    {
        if (index >= 0 && index < Gradient.Stops.Length)
        {
            MoveAndSelectGradientStop(index, Math.Round((Gradient.Stops[index].Position + delta) * 1000) / 1000);
        }
    }

    /// <summary>
    /// Adds a stop at <paramref name="position"/> (clamped to [0, 1]) in the colour the gradient already has there, so
    /// the gradient looks the same until the stop is changed. Returns the new stop's index, or -1 at <see cref="SignalGradient.MaxStops"/>.
    /// </summary>
    public int AddGradientStop(double position)
    {
        if (double.IsNaN(position) || Gradient.Stops.Length >= SignalGradient.MaxStops)
        {
            return -1;
        }

        position = Math.Clamp(position, 0, 1);
        var (r, g, b) = Gradient.Evaluate(position);
        var stop = new SignalGradientStop($"#{r:X2}{g:X2}{b:X2}", position);

        // After any stops already at this position, so an existing hard edge keeps its order.
        var index = 0;
        while (index < Gradient.Stops.Length && Gradient.Stops[index].Position <= position)
        {
            index++;
        }

        Gradient = SignalGradient.Create(Gradient.Stops.Insert(index, stop));
        return index;
    }

    /// <summary>Removes the stop at <paramref name="index"/>. Returns false for an index out of range, or at <see cref="SignalGradient.MinStops"/>.</summary>
    public bool RemoveGradientStop(int index)
    {
        if (index < 0 || index >= Gradient.Stops.Length || Gradient.Stops.Length <= SignalGradient.MinStops)
        {
            return false;
        }

        Gradient = SignalGradient.Create(Gradient.Stops.RemoveAt(index));
        return true;
    }

    /// <summary>
    /// Moves the stop at <paramref name="index"/> to <paramref name="position"/> (clamped to [0, 1]); moving it past a
    /// neighbour reorders the stops. Returns the stop's new index, or -1 for an index out of range or a position that is not a number.
    /// </summary>
    public int MoveGradientStop(int index, double position)
    {
        if (index < 0 || index >= Gradient.Stops.Length || double.IsNaN(position))
        {
            return -1;
        }

        position = Math.Clamp(position, 0, 1);
        var stops = Gradient.Stops;
        var moved = stops[index] with { Position = position };

        // Where the moved stop lands among the others: after those below or at its position, as Create's stable sort places it.
        var newIndex = 0;
        for (var i = 0; i < stops.Length; i++)
        {
            if (i != index && (stops[i].Position < position || (stops[i].Position == position && i < index)))
            {
                newIndex++;
            }
        }

        Gradient = SignalGradient.Create(stops.SetItem(index, moved));
        return newIndex;
    }

    /// <summary>Sets the colour ("#RRGGBB") of the stop at <paramref name="index"/>. Returns false for an index out of range or a colour that is not "#RRGGBB".</summary>
    public bool SetGradientStopColour(int index, string colour)
    {
        if (index < 0 || index >= Gradient.Stops.Length || !SignalGradient.IsColour(colour))
        {
            return false;
        }

        Gradient = SignalGradient.Create(Gradient.Stops.SetItem(index, Gradient.Stops[index] with { Colour = colour }));
        return true;
    }

    /// <summary>Puts the gradient back to <see cref="DefaultGradient"/>.</summary>
    [RelayCommand]
    private void ResetGradient()
    {
        Gradient = DefaultGradient;
        SelectedGradientStopIndex = 0;
    }

    partial void OnGradientChanged(SignalGradient value)
    {
        if (SelectedGradientStopIndex >= value.Stops.Length)
        {
            SelectedGradientStopIndex = value.Stops.Length - 1;
        }
    }
}
