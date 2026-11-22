using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>What a junction's level shows.</summary>
public enum JunctionLevelStyle
{
    /// <summary>Lit to the average (RMS) level, with the peak held as one line above it (the default).</summary>
    RmsAndPeak,

    /// <summary>A single bar lit to the peak level.</summary>
    Peak,
}

/// <summary>How Bluestone draws things that are a matter of taste, not of the project. One set for the whole application.</summary>
public sealed record DisplaySettings
{
    /// <summary>The colour of a lit MIDI LED by default: the accent (Palette.Accent).</summary>
    public const string DefaultMidiColour = "#3CCBF4";

    public JunctionLevelStyle LevelStyle { get; init; } = JunctionLevelStyle.RmsAndPeak;

    /// <summary>The colours the mixer's meters run through. The clip lamp is not part of it.</summary>
    public SignalGradient MixerGradient { get; init; } = SignalGradient.Default;

    /// <summary>The colours a device chain's junction meters run through. The clip lamp is not part of it.</summary>
    public SignalGradient ChainGradient { get; init; } = SignalGradient.Default;

    /// <summary>The colours the gain LEDs run through.</summary>
    public SignalGradient GainGradient { get; init; } = SignalGradient.GainDefault;

    /// <summary>The colour of a lit MIDI LED, "#RRGGBB".</summary>
    public string MidiColour { get; init; } = DefaultMidiColour;

    /// <summary>Whether LEDs fade out rather than go dark at once.</summary>
    public bool LedFade { get; init; } = true;

    /// <summary>Whether LEDs warm up (ease in) rather than light at once.</summary>
    public bool LedWarmup { get; init; } = true;
}

/// <summary>
/// Reads and writes <see cref="DisplaySettings"/> as a small JSON file. A missing or unreadable file gives the defaults; an
/// unreadable value gives that preference's default and leaves the others as saved. A file from before the gradients were
/// split has one "signalGradient", which loads into both the mixer and chain gradients; it is no longer written (a file
/// saved now carries "mixerGradient" and "chainGradient" instead, and an older build reading it shows its default gradient).
/// </summary>
public sealed class DisplaySettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new LenientEnum<JunctionLevelStyle>() },
    };

    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public DisplaySettings Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return new DisplaySettings();
            }

            using var document = JsonDocument.Parse(File.ReadAllText(Path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new DisplaySettings();
            }

            // Each preference on its own: one unknown value does not reset the others. Keys this version no longer has (the
            // old indicator positions, sizes, and LED shapes) are ignored.
            var defaults = new DisplaySettings();
            var legacy = Gradient(root, "signalGradient");
            var levelStyle = root.TryGetProperty("levelStyle", out var style) ? style.Deserialize<JunctionLevelStyle>(Options) : defaults.LevelStyle;
            return new DisplaySettings
            {
                LevelStyle = Enum.IsDefined(levelStyle) ? levelStyle : defaults.LevelStyle,
                MixerGradient = Gradient(root, "mixerGradient") ?? legacy ?? defaults.MixerGradient,
                ChainGradient = Gradient(root, "chainGradient") ?? legacy ?? defaults.ChainGradient,
                GainGradient = Gradient(root, "gainGradient") ?? defaults.GainGradient,
                MidiColour = root.TryGetProperty("midiColour", out var midi) && midi.ValueKind == JsonValueKind.String && SignalGradient.IsColour(midi.GetString())
                    ? midi.GetString()!.ToUpperInvariant()
                    : defaults.MidiColour,
                LedFade = Flag(root, "ledFade") ?? defaults.LedFade,
                LedWarmup = Flag(root, "ledWarmup") ?? defaults.LedWarmup,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new DisplaySettings();
        }
    }

    // The converter reads an invalid gradient as null; an explicit null stays null too.
    private static SignalGradient? Gradient(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.Deserialize<SignalGradient>(Options) : null;

    private static bool? Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

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

/// <summary>A choice of level style, with what the musician reads.</summary>
public sealed record LevelStyleOption(JunctionLevelStyle Style, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Settings ▸ Display: application-wide display preferences, saved as soon as they change.</summary>
public sealed partial class DisplayViewModel : ObservableObject
{
    private DisplaySettingsStore? _store;
    private bool _loading;
    private bool _unsaved;

    public DisplayViewModel()
    {
        Mixer = new GradientEditorViewModel("Mixer meters", SignalGradient.Default, hasClipLamp: true);
        Chain = new GradientEditorViewModel("Chain meters", SignalGradient.Default, hasClipLamp: true);
        Gain = new GradientEditorViewModel("Gain LEDs", SignalGradient.GainDefault, hasClipLamp: false);
        Editors = [Mixer, Chain, Gain];
        foreach (var editor in Editors)
        {
            editor.PropertyChanged += OnEditorChanged;
        }
    }

    public static IReadOnlyList<LevelStyleOption> LevelStyles { get; } =
    [
        new(JunctionLevelStyle.RmsAndPeak, "Average level, with the peak held"),
        new(JunctionLevelStyle.Peak, "Peak level"),
    ];

    /// <summary>Colours offered as one-click swatches, for a gradient stop or the MIDI LED.</summary>
    public static IReadOnlyList<string> GradientSwatches => GradientEditorViewModel.Swatches;

    /// <summary>The mixer meters' gradient.</summary>
    public GradientEditorViewModel Mixer { get; }

    /// <summary>The device chain's junction meters' gradient.</summary>
    public GradientEditorViewModel Chain { get; }

    /// <summary>The gain LEDs' gradient.</summary>
    public GradientEditorViewModel Gain { get; }

    /// <summary>Every gradient, in the order a selector lists them.</summary>
    public IReadOnlyList<GradientEditorViewModel> Editors { get; }

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

    /// <summary>
    /// The colour of a lit MIDI LED, "#RRGGBB". Setting it to six hex digits (with or without the '#') changes it; anything
    /// else is ignored, so a half-typed value never reaches the LEDs.
    /// </summary>
    public string MidiColour
    {
        get;
        set
        {
            var colour = GradientEditorViewModel.NormalizeColour(value);
            if (colour is not null && SetProperty(ref field, colour))
            {
                Save();
            }
        }
    } = DisplaySettings.DefaultMidiColour;

    /// <summary>
    /// Whether a MIDI colour preview (a picker open on it) is open: <see cref="MidiColour"/> changes apply at once, but are
    /// not saved until it ends. As a gradient's preview (<see cref="GradientEditorViewModel.IsPreviewing"/>), it also holds
    /// any other save until every preview has ended.
    /// </summary>
    [ObservableProperty]
    public partial bool IsMidiColourPreviewing { get; private set; }

    /// <summary>Whether LEDs fade out rather than go dark at once.</summary>
    [ObservableProperty]
    public partial bool LedFade { get; set; } = true;

    /// <summary>Whether LEDs warm up (ease in) rather than light at once.</summary>
    [ObservableProperty]
    public partial bool LedWarmup { get; set; } = true;

    /// <summary>
    /// Opens a MIDI colour preview: from now <see cref="MidiColour"/> changes apply at once but are not written until
    /// <see cref="EndMidiColourPreview"/>. To cancel, set <see cref="MidiColour"/> back to the value it had before, then end
    /// the preview. Starting one while one is open does nothing.
    /// </summary>
    public void BeginMidiColourPreview() => IsMidiColourPreviewing = true;

    /// <summary>Ends a MIDI colour preview, saving once if anything changed during any preview. Ending none does nothing.</summary>
    public void EndMidiColourPreview() => IsMidiColourPreviewing = false;

    /// <summary>Sets the MIDI LED's colour from a swatch ("#RRGGBB").</summary>
    [RelayCommand]
    private void ChooseMidiColour(string? colour) => MidiColour = colour ?? string.Empty;

    /// <summary>Reads the saved settings from <paramref name="store"/> and saves every later change there.</summary>
    public void Use(DisplaySettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _loading = true;
        try
        {
            var settings = store.Load();
            LevelStyle = settings.LevelStyle;
            Mixer.Gradient = settings.MixerGradient;
            Chain.Gradient = settings.ChainGradient;
            Gain.Gradient = settings.GainGradient;
            MidiColour = settings.MidiColour;
            LedFade = settings.LedFade;
            LedWarmup = settings.LedWarmup;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The settings as they are now.</summary>
    public DisplaySettings Current => new()
    {
        LevelStyle = LevelStyle,
        MixerGradient = Mixer.Gradient,
        ChainGradient = Chain.Gradient,
        GainGradient = Gain.Gradient,
        MidiColour = MidiColour,
        LedFade = LedFade,
        LedWarmup = LedWarmup,
    };

    partial void OnLevelStyleChanged(JunctionLevelStyle value) => Save();

    partial void OnLedFadeChanged(bool value) => Save();

    partial void OnLedWarmupChanged(bool value) => Save();

    partial void OnIsMidiColourPreviewingChanged(bool value)
    {
        if (!value && _unsaved && !Previewing)
        {
            Save();
        }
    }

    // Whether any preview, of a gradient or of the MIDI colour, holds saves.
    private bool Previewing => IsMidiColourPreviewing || Editors.Any(e => e.IsPreviewing);

    // An edit in any gradient saves; one made during a preview is saved once, when the last preview ends.
    private void OnEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GradientEditorViewModel.Gradient))
        {
            Save();
        }
        else if (e.PropertyName == nameof(GradientEditorViewModel.IsPreviewing) && _unsaved && !Previewing)
        {
            Save();
        }
    }

    private void Save()
    {
        if (_loading)
        {
            return;
        }

        if (Previewing)
        {
            _unsaved = true;
            return;
        }

        _unsaved = false;
        if (_store is { } store)
        {
            try
            {
                store.Save(Current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A preference that could not be saved still applies for this session.
            }
        }
    }
}
