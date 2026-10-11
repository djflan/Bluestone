using System.Globalization;
using Bluestone.Midi.Endpoints;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>
/// Plays notes from the computer keyboard, like Bitwig and Reason. The view maps physical keys to
/// semitone offsets from the current octave's C and calls <see cref="Press"/> and <see cref="Release"/>;
/// this class sends the notes through <see cref="ComputerKeyboardProvider"/>, so they are recorded,
/// echoed, and monitored like any other input.
/// </summary>
public sealed partial class ComputerKeyboardViewModel : ObservableObject
{
    public const int MinOctave = -2;
    public const int MaxOctave = 8;
    public const int DefaultOctave = 3;
    public const int DefaultVelocity = 100;
    public const int VelocityStep = 10;

    private static readonly string[] NoteNames = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];

    private readonly ComputerKeyboardProvider _provider;

    /// <summary>The note and channel each held key started, so a key released after an octave or track change stops the right note.</summary>
    private readonly Dictionary<int, (byte Note, byte Channel)> _held = [];

    public ComputerKeyboardViewModel(ComputerKeyboardProvider provider) =>
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    /// <summary>Returns the channel index (0-15) to play on, normally the record target track's. Null plays on channel 1.</summary>
    public Func<int>? ChannelSource { get; set; }

    /// <summary>The channel index (0-15) notes are sent on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChannelText))]
    public partial int Channel { get; private set; }

    public string ChannelText => (Channel + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Re-reads <see cref="ChannelSource"/>, e.g. when the selected or armed track changes.</summary>
    public void RefreshChannel() => Channel = Math.Clamp(ChannelSource?.Invoke() ?? 0, 0, 15);

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    /// <summary>The octave of the A key, where C3 is middle C (note 60).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OctaveText))]
    public partial int Octave { get; set; } = DefaultOctave;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VelocityText), nameof(VelocityFraction))]
    public partial int Velocity { get; set; } = DefaultVelocity;

    /// <summary>The name of the note on the A key, e.g. "C3".</summary>
    public string OctaveText => string.Create(CultureInfo.InvariantCulture, $"C{Octave}");

    public string VelocityText => Velocity.ToString(CultureInfo.InvariantCulture);

    /// <summary>Velocity as 0-1, for the meter.</summary>
    public double VelocityFraction => Velocity / 127.0;

    /// <summary>True while at least one key is sounding.</summary>
    public bool IsSounding => _held.Count > 0;

    /// <summary>The name of the most recently pressed note, or empty.</summary>
    [ObservableProperty]
    public partial string LastNoteText { get; set; } = string.Empty;

    /// <summary>Starts the note <paramref name="semitone"/> above the current octave's C. Repeats of a held key are ignored.</summary>
    public void Press(int semitone)
    {
        if (!IsEnabled || _held.ContainsKey(semitone))
        {
            return;
        }

        var note = ((Octave + 2) * 12) + semitone;
        if (note is < 0 or > 127)
        {
            return;
        }

        RefreshChannel();
        var status = (byte)Channel;
        _held[semitone] = ((byte)note, status);
        _provider.Send([(byte)(0x90 | status), (byte)note, (byte)Velocity]);
        LastNoteText = NoteName(note);
        OnPropertyChanged(nameof(IsSounding));
    }

    public void Release(int semitone)
    {
        if (!_held.Remove(semitone, out var held))
        {
            return;
        }

        _provider.Send([(byte)(0x80 | held.Channel), held.Note, 0]);
        OnPropertyChanged(nameof(IsSounding));
    }

    /// <summary>Stops every held note, e.g. when the window loses focus and key-ups would be lost.</summary>
    public void ReleaseAll()
    {
        if (_held.Count == 0)
        {
            return;
        }

        foreach (var held in _held.Values)
        {
            _provider.Send([(byte)(0x80 | held.Channel), held.Note, 0]);
        }

        _held.Clear();
        OnPropertyChanged(nameof(IsSounding));
    }

    public static string NoteName(int note) =>
        string.Create(CultureInfo.InvariantCulture, $"{NoteNames[note % 12]}{(note / 12) - 2}");

    [RelayCommand]
    private void Toggle() => IsEnabled = !IsEnabled;

    [RelayCommand]
    private void OctaveDown() => Octave = Math.Max(MinOctave, Octave - 1);

    [RelayCommand]
    private void OctaveUp() => Octave = Math.Min(MaxOctave, Octave + 1);

    [RelayCommand]
    private void VelocityDown() => Velocity = Math.Max(1, Velocity - VelocityStep);

    [RelayCommand]
    private void VelocityUp() => Velocity = Math.Min(127, Velocity + VelocityStep);

    partial void OnIsEnabledChanged(bool value)
    {
        if (!value)
        {
            ReleaseAll();
            LastNoteText = string.Empty;
        }
    }

    partial void OnVelocityChanged(int value)
    {
        if (value is < 1 or > 127)
        {
            Velocity = Math.Clamp(value, 1, 127);
        }
    }

    partial void OnOctaveChanged(int value)
    {
        if (value is < MinOctave or > MaxOctave)
        {
            Octave = Math.Clamp(value, MinOctave, MaxOctave);
        }
    }
}
