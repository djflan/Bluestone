using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>What a junction's level shows.</summary>
public enum JunctionLevelStyle
{
    /// <summary>Lit to the average (RMS) level, with the peak held as one line above it.</summary>
    RmsAndPeak,

    /// <summary>A single bar lit to the peak level (the default).</summary>
    Peak,
}

/// <summary>
/// How Bluestone draws things that are a matter of taste, not of the project. One set for the whole application. Colors are
/// not here: the meter gradients and the MIDI LED color belong to the theme (<see cref="ThemeDefinition"/>, ADR 0053).
/// </summary>
public sealed record DisplaySettings
{
    public JunctionLevelStyle LevelStyle { get; init; } = JunctionLevelStyle.Peak;

    /// <summary>Whether LEDs fade out rather than go dark at once.</summary>
    public bool LedFade { get; init; } = true;

    /// <summary>Whether LEDs warm up (ease in) rather than light at once.</summary>
    public bool LedWarmup { get; init; } = true;

    /// <summary>Whether lit LEDs throw a glow. Off skips the glow drawing altogether.</summary>
    public bool LedGlow { get; init; } = true;

    /// <summary>How strong the LED glow is, in percent of the default (<see cref="MinGlowStrength"/>–<see cref="MaxGlowStrength"/>).</summary>
    public int GlowStrength { get; init; } = DefaultGlowStrength;

    public const int MinGlowStrength = 0;
    public const int DefaultGlowStrength = 100;
    public const int MaxGlowStrength = 200;
}

/// <summary>
/// Reads and writes <see cref="DisplaySettings"/> as a small JSON file. A missing or unreadable file gives the defaults; an
/// unreadable value gives that preference's default and leaves the others as saved. Keys from older versions (the
/// gradients and MIDI color, which moved to the theme file in ADR 0053) are ignored and dropped at the next save.
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
            // old indicator positions, sizes, and LED shapes, and the colors now in the theme) are ignored.
            var defaults = new DisplaySettings();
            var levelStyle = root.TryGetProperty("levelStyle", out var style) ? style.Deserialize<JunctionLevelStyle>(Options) : defaults.LevelStyle;
            return new DisplaySettings
            {
                LevelStyle = Enum.IsDefined(levelStyle) ? levelStyle : defaults.LevelStyle,
                LedFade = Flag(root, "ledFade") ?? defaults.LedFade,
                LedWarmup = Flag(root, "ledWarmup") ?? defaults.LedWarmup,
                LedGlow = Flag(root, "ledGlow") ?? defaults.LedGlow,
                GlowStrength = root.TryGetProperty("glowStrength", out var strength) && strength.ValueKind == JsonValueKind.Number && strength.TryGetInt32(out var percent)
                    ? Math.Clamp(percent, DisplaySettings.MinGlowStrength, DisplaySettings.MaxGlowStrength)
                    : defaults.GlowStrength,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new DisplaySettings();
        }
    }

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

/// <summary>
/// Settings ▸ Display: application-wide display preferences, saved as soon as they change. The meter gradients and the MIDI
/// LED color are the active theme's (ADR 0053): editing one writes it into the theme's file.
/// </summary>
public sealed partial class DisplayViewModel : ObservableObject
{
    private DisplaySettingsStore? _store;
    private ThemeStore? _themeStore;
    private ThemeDefinition? _theme;
    private ThemeDefinition? _applied;
    private ThemeDefinition? _saved;
    private bool _loading;
    private bool _unsaved;
    private bool _saveFailing;
    private bool _themeSaveFailing;
    private bool _displayDirty;
    private bool _themeDirty;

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

    /// <summary>
    /// Raised with the reason when a change could not be saved (it still applies until Bluestone closes). Raised once until a
    /// later save succeeds.
    /// </summary>
    public event EventHandler<string>? SaveFailed;

    /// <summary>
    /// Raised with the reason when a gradient or MIDI color change could not be saved into the theme's file (it still applies
    /// until Bluestone closes). Raised once until a later theme save succeeds.
    /// </summary>
    public event EventHandler<string>? ThemeSaveFailed;

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
    public partial JunctionLevelStyle LevelStyle { get; set; } = JunctionLevelStyle.Peak;

    /// <summary><see cref="LevelStyle"/> as a list choice, for the Settings page.</summary>
    public LevelStyleOption LevelStyleChoice
    {
        get => LevelStyles.First(s => s.Style == LevelStyle);
        set => LevelStyle = value?.Style ?? JunctionLevelStyle.Peak;
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
                SaveTheme();
            }
        }
    } = ThemeDefinition.DefaultMidiColor;

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

    /// <summary>Whether lit LEDs throw a glow. Off skips the glow drawing altogether.</summary>
    [ObservableProperty]
    public partial bool LedGlow { get; set; } = true;

    /// <summary>How strong the LED glow is, in percent of the default; clamped to <see cref="DisplaySettings.MinGlowStrength"/>–<see cref="DisplaySettings.MaxGlowStrength"/>.</summary>
    [ObservableProperty]
    public partial int GlowStrength { get; set; } = DisplaySettings.DefaultGlowStrength;

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

    /// <summary>Puts the MIDI LED's color back to the theme's shipped one.</summary>
    [RelayCommand]
    private void ResetMidiColour() => MidiColour = _themeStore?.Defaults.MidiColor ?? ThemeDefinition.DefaultMidiColor;

    /// <summary>The active theme's name, for example "Dark Sodalite".</summary>
    [ObservableProperty]
    public partial string ThemeName { get; private set; } = ThemeDefinition.DarkSodaliteName;

    /// <summary>Whether any member of the theme (a color, a gradient, or the MIDI color) differs from what it shipped with.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetThemeCommand))]
    public partial bool IsThemeChanged { get; private set; }

    /// <summary>
    /// Whether the theme's saved colors differ from the ones in use: colors apply when Bluestone starts, so after
    /// <see cref="ResetThemeCommand"/> puts back colors that were changed, they take effect at the next start.
    /// </summary>
    [ObservableProperty]
    public partial bool IsRestartNeeded { get; private set; }

    /// <summary>
    /// Puts every member of the theme back to what it shipped with: its colors, the gradients, and the MIDI color. The view
    /// asks the musician to confirm first. Any open preview ends first (keeping what it shows), so the reset is the last word.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsThemeChanged))]
    private void ResetTheme()
    {
        if (_themeStore is not { } store)
        {
            return;
        }

        foreach (var editor in Editors.Where(e => e.IsPreviewing))
        {
            editor.EndGradientPreview();
        }

        EndMidiColourPreview();
        _loading = true;
        try
        {
            _theme = store.Defaults;
            Mixer.Gradient = store.Defaults.MixerGradient;
            Chain.Gradient = store.Defaults.ChainGradient;
            Gain.Gradient = store.Defaults.GainGradient;
            MidiColour = store.Defaults.MidiColor;
        }
        finally
        {
            _loading = false;
        }

        SaveTheme();
    }

    /// <summary>
    /// Takes the gradients and the MIDI color from <paramref name="theme"/> (as loaded and applied when Bluestone started) and
    /// saves every later change to them, and <see cref="ResetThemeCommand"/>, into <paramref name="store"/>'s file. Each
    /// gradient's reset puts back <paramref name="store"/>'s shipped gradient.
    /// </summary>
    public void UseTheme(ThemeStore store, ThemeDefinition theme)
    {
        _themeStore = store ?? throw new ArgumentNullException(nameof(store));
        _theme = _applied = _saved = theme ?? throw new ArgumentNullException(nameof(theme));
        ThemeName = theme.Name;
        Mixer.UseDefault(store.Defaults.MixerGradient);
        Chain.UseDefault(store.Defaults.ChainGradient);
        Gain.UseDefault(store.Defaults.GainGradient);
        _loading = true;
        try
        {
            Mixer.Gradient = theme.MixerGradient;
            Chain.Gradient = theme.ChainGradient;
            Gain.Gradient = theme.GainGradient;
            MidiColour = theme.MidiColor;
        }
        finally
        {
            _loading = false;
        }

        RefreshThemeState();
    }

    /// <summary>The theme as it is now: its colors as saved, with the gradients and the MIDI color as edited.</summary>
    public ThemeDefinition? Theme => _theme is { } theme
        ? theme with { MixerGradient = Mixer.Gradient, ChainGradient = Chain.Gradient, GainGradient = Gain.Gradient, MidiColor = MidiColour }
        : null;

    /// <summary>Reads the saved settings from <paramref name="store"/> and saves every later change there.</summary>
    public void Use(DisplaySettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _loading = true;
        try
        {
            var settings = store.Load();
            LevelStyle = settings.LevelStyle;
            LedFade = settings.LedFade;
            LedWarmup = settings.LedWarmup;
            LedGlow = settings.LedGlow;
            GlowStrength = settings.GlowStrength;
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
        LedFade = LedFade,
        LedWarmup = LedWarmup,
        LedGlow = LedGlow,
        GlowStrength = GlowStrength,
    };

    partial void OnLevelStyleChanged(JunctionLevelStyle value) => SaveDisplay();

    partial void OnLedFadeChanged(bool value) => SaveDisplay();

    partial void OnLedWarmupChanged(bool value) => SaveDisplay();

    partial void OnLedGlowChanged(bool value) => SaveDisplay();

    partial void OnGlowStrengthChanged(int value)
    {
        var clamped = Math.Clamp(value, DisplaySettings.MinGlowStrength, DisplaySettings.MaxGlowStrength);
        if (clamped != value)
        {
            // Setting it again saves (and notifies) the clamped value.
            GlowStrength = clamped;
            return;
        }

        SaveDisplay();
    }

    partial void OnIsMidiColourPreviewingChanged(bool value)
    {
        if (!value && _unsaved && !Previewing)
        {
            Flush();
        }
    }

    // Whether any preview, of a gradient or of the MIDI colour, holds saves.
    private bool Previewing => IsMidiColourPreviewing || Editors.Any(e => e.IsPreviewing);

    // An edit in any gradient saves; one made during a preview is saved once, when the last preview ends.
    private void OnEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GradientEditorViewModel.Gradient))
        {
            SaveTheme();
        }
        else if (e.PropertyName == nameof(GradientEditorViewModel.IsPreviewing) && _unsaved && !Previewing)
        {
            Flush();
        }
    }

    // A display preference changed: it is written to the display settings file only.
    private void SaveDisplay()
    {
        if (!_loading)
        {
            _displayDirty = true;
            Flush(includeTheme: false);
        }
    }

    // A theme member changed: it is written to the theme's file only, so changing a display preference never rewrites the
    // theme.
    private void SaveTheme()
    {
        if (!_loading)
        {
            _themeDirty = true;
            Flush();
        }
    }

    // Writes whatever changed, unless a preview holds saves until it ends. Each file reports its own failure once per run of
    // failures, so dragging a control on a read-only disk does not flood the messages; the change still applies for this
    // session, and stays pending so the next save of that kind tries again. A display preference never retries the theme,
    // so a display change never writes the theme file.
    private void Flush(bool includeTheme = true)
    {
        if (Previewing)
        {
            _unsaved = true;
            return;
        }

        _unsaved = false;
        RefreshThemeState();
        if (_displayDirty && _store is { } store)
        {
            try
            {
                store.Save(Current);
                _displayDirty = false;
                _saveFailing = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!_saveFailing)
                {
                    _saveFailing = true;
                    SaveFailed?.Invoke(this, ex.Message);
                }
            }
        }

        if (includeTheme && _themeDirty && _themeStore is { } themeStore && Theme is { } theme)
        {
            try
            {
                themeStore.Save(theme);
                _saved = theme;
                _themeDirty = false;
                _themeSaveFailing = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!_themeSaveFailing)
                {
                    _themeSaveFailing = true;
                    ThemeSaveFailed?.Invoke(this, ex.Message);
                }
            }

            RefreshThemeState();
        }
    }

    private void RefreshThemeState()
    {
        if (_themeStore is not { } store || Theme is not { } theme)
        {
            return;
        }

        // A change that couldn't be saved keeps Reset theme available, so it can be tried again; the restart note speaks
        // only of what the file holds, since that is what the next start applies.
        IsThemeChanged = theme != store.Defaults || _themeDirty;
        IsRestartNeeded = _applied is { } applied && _saved is { } saved && (applied.Variant != saved.Variant || !applied.SameColorsAs(saved));
    }
}
