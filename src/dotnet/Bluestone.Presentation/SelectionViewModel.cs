using System.Collections.ObjectModel;
using System.Globalization;
using Bluestone.Application.Routing;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bluestone.Presentation;

/// <summary>
/// The inspector for the selected tracks. Values shared by every selected track are shown as-is;
/// values that differ show "Mixed". Changing a value applies it to every selected track as one
/// undoable step.
/// </summary>
public sealed partial class SelectionViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private IReadOnlyList<TrackViewModel> _tracks = [];
    private DeviceProfile? _sharedProfile;
    private bool _syncing;

    internal SelectionViewModel(MainViewModel owner) => _owner = owner;

    public IReadOnlyList<TrackViewModel> Tracks => _tracks;

    public int Count => _tracks.Count;

    public bool HasSelection => _tracks.Count > 0;

    public bool IsSingle => _tracks.Count == 1;

    public bool IsMultiple => _tracks.Count > 1;

    /// <summary>The track name, or "3 tracks".</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; private set; } = string.Empty;

    public ObservableCollection<OutputOption> OutputChoices { get; } = [];

    public ObservableCollection<ProfileOption> ProfileChoices { get; } = [];

    public ObservableCollection<ChannelOption> ChannelChoices { get; } = [];

    public ObservableCollection<BankOption> Banks { get; } = [];

    public ObservableCollection<ProgramOption> Programs { get; } = [];

    [ObservableProperty]
    public partial OutputOption? Output { get; set; }

    [ObservableProperty]
    public partial ProfileOption? Profile { get; set; }

    [ObservableProperty]
    public partial ChannelOption? Channel { get; set; }

    /// <summary>Semitones; empty when the selected tracks differ.</summary>
    [ObservableProperty]
    public partial decimal? Transpose { get; set; }

    [ObservableProperty]
    public partial BankOption? VoiceBank { get; set; }

    [ObservableProperty]
    public partial ProgramOption? VoiceProgram { get; set; }

    /// <summary>Voices can be chosen only when every selected track uses the same installed profile.</summary>
    [ObservableProperty]
    public partial bool CanEditVoice { get; private set; }

    [ObservableProperty]
    public partial string VoiceHint { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> ProfileNotices { get; private set; } = [];

    [ObservableProperty]
    public partial bool CanInitialize { get; private set; }

    public IReadOnlyList<TrackRole> RoleChoices { get; } = Enum.GetValues<TrackRole>();

    /// <summary>The selected tracks' role, or null when they differ.</summary>
    [ObservableProperty]
    public partial TrackRole? Role { get; set; }

    internal void Sync(IReadOnlyList<TrackViewModel> tracks, IReadOnlyList<OutputOption> outputs, ProfileCatalog catalog)
    {
        _syncing = true;
        try
        {
            _tracks = [.. tracks];
            Title = tracks.Count switch
            {
                0 => string.Empty,
                1 => tracks[0].Name,
                _ => string.Create(CultureInfo.InvariantCulture, $"{tracks.Count} tracks"),
            };
            Subtitle = tracks.Count == 1 ? tracks[0].RouteText : "Changes apply to every selected track.";

            SyncOutputs(outputs);
            SyncProfiles(catalog);
            SyncChannels();
            var transposes = tracks.Select(t => t.Route.Transpose).Distinct().ToList();
            Transpose = transposes.Count == 1 ? transposes[0] : null;
            SyncVoice();
            var roles = tracks.Select(t => t.Role).Distinct().ToList();
            Role = roles.Count == 1 ? roles[0] : null;

            Problems = tracks.Count == 1
                ? tracks[0].Problems
                : [.. tracks.SelectMany(t => t.Problems.Select(p => $"{t.Name}: {p}"))];
            ProfileNotices = _sharedProfile?.Notices ?? [];
            CanInitialize = tracks.Count == 1 && tracks[0].CanInitialize;

            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(IsSingle));
            OnPropertyChanged(nameof(IsMultiple));
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnRoleChanged(TrackRole? value)
    {
        if (!_syncing && value is { } role)
        {
            _ = _owner.ChangeRoleAsync(role);
        }
    }

    partial void OnOutputChanged(OutputOption? value)
    {
        if (_syncing || value is null || value.IsMixed || (value.Id is not null && !value.IsAvailable))
        {
            return;
        }

        var endpoint = value.Id is { } id ? _owner.FindEndpoint(id) : null;
        var reference = endpoint is null ? null : RouteResolver.ReferenceTo(endpoint);
        _owner.ApplyToSelection("Change Output", r => r with { Endpoint = reference });
    }

    partial void OnProfileChanged(ProfileOption? value)
    {
        if (_syncing || value is null || value.IsMixed || !value.IsInstalled)
        {
            return;
        }

        var profile = value.Id is { } id ? _owner.FindProfile(id) : null;
        _owner.ApplyToSelection("Change Profile", r => r with
        {
            Profile = profile is null ? null : RouteResolver.ReferenceTo(profile),
            Voice = profile?.FindBank(r.Voice?.BankId ?? string.Empty) is null ? null : r.Voice,
        });
    }

    partial void OnChannelChanged(ChannelOption? value)
    {
        if (!_syncing && value is { IsMixed: false })
        {
            _owner.ApplyToSelection("Change Channel", r => r with { Channel = value.Channel });
        }
    }

    partial void OnTransposeChanged(decimal? value)
    {
        if (!_syncing && value is { } semitones)
        {
            var clamped = (int)Math.Clamp(semitones, -BuiltInDevices.MaxTranspose, BuiltInDevices.MaxTranspose);
            _owner.ApplyToSelection("Transpose", r => r with { Transpose = clamped });
        }
    }

    partial void OnVoiceBankChanged(BankOption? value)
    {
        if (_syncing || value is null || value.IsMixed || !CanEditVoice)
        {
            return;
        }

        var program = VoiceProgram is { IsMixed: false } p ? p.Program : new ProgramNumber(0);
        _owner.ApplyToSelection("Change Voice", r => r with { Voice = value.Id is { } bank ? new VoiceAssignment(bank, program) : null });
    }

    partial void OnVoiceProgramChanged(ProgramOption? value)
    {
        if (!_syncing && value is { IsMixed: false } && VoiceBank is { IsMixed: false, Id: { } bank } && CanEditVoice)
        {
            _owner.ApplyToSelection("Change Voice", r => r with { Voice = new VoiceAssignment(bank, value.Program) });
        }
    }

    private void SyncOutputs(IReadOnlyList<OutputOption> outputs)
    {
        OutputChoices.Clear();
        OutputChoices.Add(OutputOption.None);
        foreach (var output in outputs)
        {
            OutputChoices.Add(output);
        }

        var distinct = _tracks.Select(t => t.Output).Distinct().ToList();
        if (distinct.Count == 1)
        {
            var current = distinct[0];
            if (!OutputChoices.Contains(current))
            {
                OutputChoices.Add(current);
            }

            Output = current;
        }
        else if (distinct.Count > 1)
        {
            var mixed = OutputOption.Mixed(distinct.Count);
            OutputChoices.Insert(0, mixed);
            Output = mixed;
        }
        else
        {
            Output = null;
        }
    }

    private void SyncProfiles(ProfileCatalog catalog)
    {
        ProfileChoices.Clear();
        ProfileChoices.Add(ProfileOption.None);
        foreach (var profile in catalog.Profiles)
        {
            ProfileChoices.Add(new ProfileOption(profile.Id, profile.Name, true));
        }

        var distinct = _tracks.Select(t => t.Profile).Distinct().ToList();
        _sharedProfile = distinct.Count == 1 ? _tracks[0].ResolvedProfile : null;
        if (distinct.Count == 1)
        {
            if (!ProfileChoices.Contains(distinct[0]))
            {
                ProfileChoices.Add(distinct[0]);
            }

            Profile = distinct[0];
        }
        else if (distinct.Count > 1)
        {
            ProfileChoices.Insert(0, ProfileOption.Mixed);
            Profile = ProfileOption.Mixed;
        }
        else
        {
            Profile = null;
        }
    }

    private void SyncChannels()
    {
        ChannelChoices.Clear();
        var distinct = _tracks.Select(t => t.Route.Channel).Distinct().ToList();
        if (distinct.Count > 1)
        {
            ChannelChoices.Add(ChannelOption.Mixed);
        }

        foreach (var option in ChannelOption.All)
        {
            ChannelChoices.Add(option);
        }

        Channel = distinct.Count switch
        {
            0 => null,
            1 => ChannelChoices.First(c => !c.IsMixed && c.Channel == distinct[0]),
            _ => ChannelOption.Mixed,
        };
    }

    private void SyncVoice()
    {
        Banks.Clear();
        Programs.Clear();
        CanEditVoice = _sharedProfile is not null;
        VoiceHint = CanEditVoice
            ? "Selected at the start of playback, before the track's own program changes."
            : _tracks.Count > 1 ? "Choose the same installed profile for every selected track to pick a voice." : "Choose an instrument profile to pick a voice.";
        if (_sharedProfile is not { } profile)
        {
            VoiceBank = null;
            VoiceProgram = null;
            return;
        }

        var banks = _tracks.Select(t => t.Route.Voice?.BankId).Distinct().ToList();
        if (banks.Count > 1)
        {
            Banks.Add(BankOption.Mixed);
        }

        Banks.Add(BankOption.None);
        foreach (var bank in profile.Banks)
        {
            Banks.Add(new BankOption(bank.Id, bank.Name));
        }

        VoiceBank = banks.Count > 1 ? BankOption.Mixed : Banks.FirstOrDefault(b => !b.IsMixed && b.Id == banks[0]) ?? BankOption.None;

        if (VoiceBank is not { IsMixed: false, Id: { } bankId } || profile.FindBank(bankId) is not { } selectedBank)
        {
            VoiceProgram = null;
            return;
        }

        var programs = _tracks.Select(t => t.Route.Voice?.Program).Distinct().ToList();
        if (programs.Count > 1)
        {
            Programs.Add(ProgramOption.Mixed);
        }

        var named = selectedBank.Programs.ToDictionary(p => p.Program);
        for (var value = 0; value < 128; value++)
        {
            var program = new ProgramNumber(value);
            Programs.Add(new ProgramOption(program, named.TryGetValue(program, out var p)
                ? string.Create(CultureInfo.InvariantCulture, $"{program.Number} · {p.Name}")
                : string.Create(CultureInfo.InvariantCulture, $"Program {program.Number}")));
        }

        VoiceProgram = programs.Count > 1
            ? ProgramOption.Mixed
            : programs[0] is { } single ? Programs.First(p => !p.IsMixed && p.Program == single) : null;
    }
}
