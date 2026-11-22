using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>Where a Device View's signal indicators (a junction's level and note lamp) sit, top to bottom of the panels.</summary>
public enum JunctionIndicatorPosition
{
    /// <summary>Flush with the top of the device, beside its header.</summary>
    Top,

    /// <summary>In the middle of the upper half of the device's body.</summary>
    UpperHalf,

    /// <summary>In the middle of the lower half of the device's body (the default).</summary>
    LowerHalf,

    /// <summary>At the bottom of the device.</summary>
    Bottom,

    /// <summary>Centred on the device's body: Medium and Large, which are too tall for the half positions to differ from Top and Bottom.</summary>
    Middle,
}

/// <summary>What a junction's level shows.</summary>
public enum JunctionLevelStyle
{
    /// <summary>Lit to the average (RMS) level, with the peak held as one line above it (the default).</summary>
    RmsAndPeak,

    /// <summary>A single bar lit to the peak level.</summary>
    Peak,
}

/// <summary>How much a signal indicator shows: the junction's slim level, a mixer-strip level, or an expanded view's.</summary>
public enum SignalIndicatorSize
{
    /// <summary>The slim level the Device View's junctions have always drawn: 1 px segments, no scale (the default).</summary>
    Compact,

    /// <summary>2 dB segments with a 0 dB tick.</summary>
    Medium,

    /// <summary>A dB scale, a held-peak readout, and a clip light that stays on until cleared.</summary>
    Large,
}

/// <summary>The shape of a Medium or Large level's LEDs: segments, or round dots (with their own, coarser pitch).</summary>
public enum LedShape
{
    Bar,
    Round,
}

/// <summary>How Bluestone draws things that are a matter of taste, not of the project. One set for the whole application.</summary>
public sealed record DisplaySettings
{
    public JunctionIndicatorPosition IndicatorPosition { get; init; } = JunctionIndicatorPosition.LowerHalf;

    public JunctionLevelStyle LevelStyle { get; init; } = JunctionLevelStyle.RmsAndPeak;

    public SignalIndicatorSize IndicatorSize { get; init; } = SignalIndicatorSize.Compact;

    public LedShape LedShape { get; init; } = LedShape.Bar;

    /// <summary>Where Medium and Large indicators sit: Top, Middle, or Bottom. Compact uses <see cref="IndicatorPosition"/>.</summary>
    public JunctionIndicatorPosition LargeIndicatorPosition { get; init; } = JunctionIndicatorPosition.Middle;

    /// <summary>The colours signal indicators' levels run through. The clip lamp is not part of it.</summary>
    public SignalGradient SignalGradient { get; init; } = SignalGradient.Default;
}

/// <summary>Reads and writes <see cref="DisplaySettings"/> as a small JSON file. A missing or unreadable file gives the defaults.</summary>
public sealed class DisplaySettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new LenientEnum<JunctionIndicatorPosition>(), new LenientEnum<JunctionLevelStyle>(), new LenientEnum<SignalIndicatorSize>(), new LenientEnum<LedShape>() },
    };

    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public DisplaySettings Load()
    {
        try
        {
            var settings = File.Exists(Path) ? JsonSerializer.Deserialize<DisplaySettings>(File.ReadAllText(Path), Options) : null;
            if (settings is null)
            {
                return new DisplaySettings();
            }

            // Each preference on its own: one unknown value does not reset the others.
            return new DisplaySettings
            {
                IndicatorPosition = DisplayViewModel.Positions.Any(p => p.Position == settings.IndicatorPosition) ? settings.IndicatorPosition : JunctionIndicatorPosition.LowerHalf,
                LevelStyle = Enum.IsDefined(settings.LevelStyle) ? settings.LevelStyle : JunctionLevelStyle.RmsAndPeak,
                IndicatorSize = Enum.IsDefined(settings.IndicatorSize) ? settings.IndicatorSize : SignalIndicatorSize.Compact,
                LedShape = Enum.IsDefined(settings.LedShape) ? settings.LedShape : LedShape.Bar,
                LargeIndicatorPosition = DisplayViewModel.LargePositions.Any(p => p.Position == settings.LargeIndicatorPosition) ? settings.LargeIndicatorPosition : JunctionIndicatorPosition.Middle,

                // The converter reads an invalid gradient as null; an explicit null stays null too.
                SignalGradient = settings.SignalGradient ?? SignalGradient.Default,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new DisplaySettings();
        }
    }

    // Reads an enum written by name (camelCase); anything else reads as an undefined value, which Load replaces with that
    // preference's default, so one unknown value (from a newer version, or hand-edited) does not reset the others.
    private sealed class LenientEnum<T> : JsonConverter<T>
        where T : struct, Enum
    {
        private static readonly T Unknown = (T)Enum.ToObject(typeof(T), -1);

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var text = reader.GetString();
                return !string.IsNullOrEmpty(text) && char.IsLetter(text[0]) && Enum.TryParse<T>(text, ignoreCase: true, out var value) ? value : Unknown;
            }

            reader.Skip();
            return Unknown;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
    }

    /// <summary>Writes atomically (a temporary file, then a rename).</summary>
    public void Save(DisplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, Path, overwrite: true);
    }
}

/// <summary>A choice of indicator position, with what the musician reads.</summary>
public sealed record IndicatorPositionOption(JunctionIndicatorPosition Position, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A choice of level style, with what the musician reads.</summary>
public sealed record LevelStyleOption(JunctionLevelStyle Style, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A choice of indicator size, with what the musician reads.</summary>
public sealed record IndicatorSizeOption(SignalIndicatorSize Size, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A choice of LED shape, with what the musician reads.</summary>
public sealed record LedShapeOption(LedShape Shape, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Settings ▸ Display: application-wide display preferences, saved as soon as they change.</summary>
public sealed partial class DisplayViewModel : ObservableObject
{
    private DisplaySettingsStore? _store;
    private bool _loading;

    public static IReadOnlyList<IndicatorPositionOption> Positions { get; } =
    [
        new(JunctionIndicatorPosition.Top, "Top of the device"),
        new(JunctionIndicatorPosition.UpperHalf, "Upper half"),
        new(JunctionIndicatorPosition.LowerHalf, "Lower half"),
        new(JunctionIndicatorPosition.Bottom, "Bottom of the device"),
    ];

    /// <summary>The positions Medium and Large can take: they fill too much of the device for a half to differ from its end.</summary>
    public static IReadOnlyList<IndicatorPositionOption> LargePositions { get; } =
    [
        new(JunctionIndicatorPosition.Top, "Top of the device"),
        new(JunctionIndicatorPosition.Middle, "Middle of the device"),
        new(JunctionIndicatorPosition.Bottom, "Bottom of the device"),
    ];

    public static IReadOnlyList<LevelStyleOption> LevelStyles { get; } =
    [
        new(JunctionLevelStyle.RmsAndPeak, "Average level, with the peak held"),
        new(JunctionLevelStyle.Peak, "Peak level"),
    ];

    public static IReadOnlyList<IndicatorSizeOption> Sizes { get; } =
    [
        new(SignalIndicatorSize.Compact, "Compact"),
        new(SignalIndicatorSize.Medium, "Medium, with a 0 dB mark"),
        new(SignalIndicatorSize.Large, "Large, with a dB scale, peak readout, and clip light"),
    ];

    public static IReadOnlyList<LedShapeOption> LedShapes { get; } =
    [
        new(LedShape.Bar, "Bars"),
        new(LedShape.Round, "Round"),
    ];

    /// <summary>Legacy (one meter design now; removed once the Device View no longer reads it). Where every Device View draws its Compact junctions' levels and note lamps.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeamIndicatorPosition))]
    public partial JunctionIndicatorPosition IndicatorPosition { get; set; } = JunctionIndicatorPosition.LowerHalf;

    /// <summary>Legacy (one meter design now; removed once the Device View no longer reads it). Where every Device View draws its Medium and Large indicators: Top, Middle, or Bottom.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeamIndicatorPosition))]
    public partial JunctionIndicatorPosition LargeIndicatorPosition { get; set; } = JunctionIndicatorPosition.Middle;

    /// <summary>Legacy (one meter design now; removed once the Device View no longer reads it). The position the seams use: Compact's own, or Medium and Large's.</summary>
    public JunctionIndicatorPosition SeamIndicatorPosition => IndicatorSize == SignalIndicatorSize.Compact ? IndicatorPosition : LargeIndicatorPosition;

    /// <summary>What every Device View's junction levels show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LevelStyleChoice))]
    public partial JunctionLevelStyle LevelStyle { get; set; } = JunctionLevelStyle.RmsAndPeak;

    /// <summary><see cref="LevelStyle"/> as a list choice, for the Settings page.</summary>
    public LevelStyleOption LevelStyleChoice
    {
        get => LevelStyles.First(s => s.Style == LevelStyle);
        set => LevelStyle = value?.Style ?? JunctionLevelStyle.RmsAndPeak;
    }

    /// <summary>Legacy (one meter design now; removed once the Device View no longer reads it). How much every Device View's junction indicators show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeamIndicatorPosition))]
    public partial SignalIndicatorSize IndicatorSize { get; set; } = SignalIndicatorSize.Compact;

    /// <summary>Legacy (one meter design now; removed once the Device View no longer reads it). The LEDs of Medium and Large junction indicators (Compact always draws its slim segments).</summary>
    [ObservableProperty]
    public partial LedShape LedShape { get; set; } = LedShape.Bar;

    /// <summary>The colours every signal indicator's level runs through, bottom to top. The clip lamp keeps its own colour.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedGradientStop), nameof(SelectedGradientStopPercent), nameof(SelectedGradientStopColour), nameof(CanAddGradientStop), nameof(CanRemoveGradientStop))]
    [NotifyCanExecuteChangedFor(nameof(AddGradientStopInLargestGapCommand), nameof(RemoveSelectedGradientStopCommand))]
    public partial SignalGradient SignalGradient { get; set; } = SignalGradient.Default;

    /// <summary>Colours offered as one-click swatches for a gradient stop: the Machined meter and signal colours. Clip red is the clip lamp's alone.</summary>
    public static IReadOnlyList<string> GradientSwatches { get; } =
        ["#34C6F4", "#3FE0D8", "#4FB3B0", "#4CB873", "#3D5BFF", "#7C6BFF", "#B65CFF", "#E070C8", "#FF2E93", "#E0A43A", "#E3C345", "#DADCE0"];

    /// <summary>The stop the editor is working on (its index in <see cref="SignalGradient"/>'s stops).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedGradientStop), nameof(SelectedGradientStopPercent), nameof(SelectedGradientStopColour))]
    public partial int SelectedGradientStopIndex { get; set; }

    /// <summary>The selected stop.</summary>
    public SignalGradientStop SelectedGradientStop => SignalGradient.Stops[Math.Clamp(SelectedGradientStopIndex, 0, SignalGradient.Stops.Length - 1)];

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
            var colour = value is { Length: 6 } ? "#" + value : value?.Trim();
            if (SignalGradient.IsColour(colour))
            {
                SetGradientStopColour(Math.Clamp(SelectedGradientStopIndex, 0, SignalGradient.Stops.Length - 1), colour!);
            }
        }
    }

    /// <summary>Whether a stop can be removed: a gradient keeps at least <see cref="SignalGradient.MinStops"/>.</summary>
    public bool CanRemoveGradientStop => SignalGradient.Stops.Length > SignalGradient.MinStops;

    /// <summary>Whether a stop can be added: a gradient has at most <see cref="SignalGradient.MaxStops"/>.</summary>
    public bool CanAddGradientStop => SignalGradient.Stops.Length < SignalGradient.MaxStops;

    /// <summary>"40%": a position in [0, 1] as a whole percentage.</summary>
    public static string FormatPercent(double position) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Math.Round(position * 100):0}%");

    /// <summary>Adds a stop at the middle of the widest gap between stops (the colour already there) and selects it.</summary>
    [RelayCommand(CanExecute = nameof(CanAddGradientStop))]
    private void AddGradientStopInLargestGap()
    {
        var stops = SignalGradient.Stops;
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
        var index = Math.Clamp(SelectedGradientStopIndex, 0, SignalGradient.Stops.Length - 1);
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

    /// <summary>Nudges the stop at <paramref name="index"/> by <paramref name="delta"/> (0.01 is 1%) and selects it wherever it lands.</summary>
    public void NudgeGradientStop(int index, double delta)
    {
        if (index >= 0 && index < SignalGradient.Stops.Length)
        {
            MoveAndSelectGradientStop(index, Math.Round((SignalGradient.Stops[index].Position + delta) * 1000) / 1000);
        }
    }

    /// <summary>
    /// Adds a stop at <paramref name="position"/> (clamped to [0, 1]) in the colour the gradient already has there, so
    /// the gradient looks the same until the stop is changed. Returns the new stop's index, or -1 at <see cref="SignalGradient.MaxStops"/>.
    /// </summary>
    public int AddGradientStop(double position)
    {
        if (double.IsNaN(position) || SignalGradient.Stops.Length >= SignalGradient.MaxStops)
        {
            return -1;
        }

        position = Math.Clamp(position, 0, 1);
        var (r, g, b) = SignalGradient.Evaluate(position);
        var stop = new SignalGradientStop($"#{r:X2}{g:X2}{b:X2}", position);

        // After any stops already at this position, so an existing hard edge keeps its order.
        var index = 0;
        while (index < SignalGradient.Stops.Length && SignalGradient.Stops[index].Position <= position)
        {
            index++;
        }

        SignalGradient = SignalGradient.Create(SignalGradient.Stops.Insert(index, stop));
        return index;
    }

    /// <summary>Removes the stop at <paramref name="index"/>. Returns false for an index out of range, or at <see cref="SignalGradient.MinStops"/>.</summary>
    public bool RemoveGradientStop(int index)
    {
        if (index < 0 || index >= SignalGradient.Stops.Length || SignalGradient.Stops.Length <= SignalGradient.MinStops)
        {
            return false;
        }

        SignalGradient = SignalGradient.Create(SignalGradient.Stops.RemoveAt(index));
        return true;
    }

    /// <summary>
    /// Moves the stop at <paramref name="index"/> to <paramref name="position"/> (clamped to [0, 1]); moving it past a
    /// neighbour reorders the stops. Returns the stop's new index, or -1 for an index out of range or a position that is not a number.
    /// </summary>
    public int MoveGradientStop(int index, double position)
    {
        if (index < 0 || index >= SignalGradient.Stops.Length || double.IsNaN(position))
        {
            return -1;
        }

        position = Math.Clamp(position, 0, 1);
        var stops = SignalGradient.Stops;
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

        SignalGradient = SignalGradient.Create(stops.SetItem(index, moved));
        return newIndex;
    }

    /// <summary>Sets the colour ("#RRGGBB") of the stop at <paramref name="index"/>. Returns false for an index out of range or a colour that is not "#RRGGBB".</summary>
    public bool SetGradientStopColour(int index, string colour)
    {
        if (index < 0 || index >= SignalGradient.Stops.Length || !SignalGradient.IsColour(colour))
        {
            return false;
        }

        SignalGradient = SignalGradient.Create(SignalGradient.Stops.SetItem(index, SignalGradient.Stops[index] with { Colour = colour }));
        return true;
    }

    /// <summary>Puts the gradient back to <see cref="SignalGradient.Default"/>.</summary>
    [RelayCommand]
    private void ResetGradient()
    {
        SignalGradient = SignalGradient.Default;
        SelectedGradientStopIndex = 0;
    }

    /// <summary>Reads the saved settings from <paramref name="store"/> and saves every later change there.</summary>
    public void Use(DisplaySettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _loading = true;
        try
        {
            var settings = store.Load();
            IndicatorPosition = settings.IndicatorPosition;
            LevelStyle = settings.LevelStyle;
            IndicatorSize = settings.IndicatorSize;
            LedShape = settings.LedShape;
            LargeIndicatorPosition = settings.LargeIndicatorPosition;
            SignalGradient = settings.SignalGradient;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnIndicatorPositionChanged(JunctionIndicatorPosition value) => Save();

    partial void OnLevelStyleChanged(JunctionLevelStyle value) => Save();

    partial void OnIndicatorSizeChanged(SignalIndicatorSize value) => Save();

    partial void OnLedShapeChanged(LedShape value) => Save();

    partial void OnLargeIndicatorPositionChanged(JunctionIndicatorPosition value) => Save();

    partial void OnSignalGradientChanged(SignalGradient value)
    {
        if (SelectedGradientStopIndex >= value.Stops.Length)
        {
            SelectedGradientStopIndex = value.Stops.Length - 1;
        }

        Save();
    }

    private void Save()
    {
        if (!_loading && _store is { } store)
        {
            try
            {
                store.Save(new DisplaySettings { IndicatorPosition = IndicatorPosition, LevelStyle = LevelStyle, IndicatorSize = IndicatorSize, LedShape = LedShape, LargeIndicatorPosition = LargeIndicatorPosition, SignalGradient = SignalGradient });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A preference that could not be saved still applies for this session.
            }
        }
    }
}
