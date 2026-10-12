using System.Collections.ObjectModel;
using System.Globalization;
using Bluestone.Application.Editing;
using Bluestone.Audio;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>
/// The project's racks: free-standing device chains that several tracks route into, such as one shared XG
/// instrument (ADR 0022). A rack is edited in a Device View and connected in the routing inspector.
/// </summary>
public sealed partial class RacksViewModel : ObservableObject
{
    private readonly MainViewModel _owner;

    internal RacksViewModel(MainViewModel owner) => _owner = owner;

    public ObservableCollection<RackRow> Racks { get; } = [];

    public bool IsEmpty => Racks.Count == 0;

    /// <summary>The section's one-line state when closed: "2 racks", or "None".</summary>
    public string Summary => Racks.Count switch
    {
        0 => "None",
        1 => "1 rack",
        var n => $"{n} racks",
    };

    internal void Sync(Project project, DeviceChainId? editing)
    {
        var racks = project.Chains.Where(c => c.Owner.Kind == ChainOwnerKind.Rack).ToList();
        while (Racks.Count > racks.Count)
        {
            Racks.RemoveAt(Racks.Count - 1);
        }

        for (var i = 0; i < racks.Count; i++)
        {
            if (i == Racks.Count)
            {
                Racks.Add(new RackRow(this));
            }

            var sources = project.ConnectionsTo(Domain.Routing.SignalNode.Rack(racks[i].Id)).Count();
            Racks[i].Sync(racks[i], sources, racks[i].Id == editing);
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Adds an empty rack and opens it in the active Device View.</summary>
    [RelayCommand]
    private void NewRack()
    {
        var rack = DeviceChain.Create(ChainOwner.Rack, string.Create(CultureInfo.InvariantCulture, $"Rack {Racks.Count + 1}"));
        if (_owner.Execute(DeviceCommands.AddRack(rack)))
        {
            _owner.Workspace.ShowRack(rack.Id);
        }
    }

    internal void Edit(RackRow rack) => _owner.Workspace.ShowRack(rack.Id);

    internal void Remove(RackRow rack) => _owner.Execute(DeviceCommands.RemoveRack(rack.Id));

    internal void Rename(RackRow rack, string name) => _owner.Execute(DeviceCommands.RenameChain(rack.Id, name));
}

/// <summary>One rack in the list.</summary>
public sealed partial class RackRow : ObservableObject
{
    private readonly RacksViewModel _owner;
    private bool _syncing;

    internal RackRow(RacksViewModel owner) => _owner = owner;

    public DeviceChainId Id { get; private set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>"2 devices · 4 tracks in".</summary>
    [ObservableProperty]
    public partial string Detail { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEditing { get; private set; }

    internal void Sync(DeviceChain rack, int sources, bool editing)
    {
        _syncing = true;
        try
        {
            Id = rack.Id;
            Name = rack.Name;
            IsEditing = editing;
            Detail = string.Create(CultureInfo.InvariantCulture, $"{rack.Devices.Length} device{(rack.Devices.Length == 1 ? string.Empty : "s")} · {sources} input{(sources == 1 ? string.Empty : "s")}");
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnNameChanged(string value)
    {
        if (!_syncing && value.Length is > 0 and <= DeviceChain.MaxNameLength)
        {
            _owner.Rename(this, value);
        }
    }

    [RelayCommand]
    private void Edit() => _owner.Edit(this);

    [RelayCommand]
    private void Remove() => _owner.Remove(this);
}

/// <summary>Where a mixer channel sends its audio: the master, or another channel (a bus).</summary>
public sealed record MixerOutputOption(MixerChannelId? Channel, string Name)
{
    public static readonly MixerOutputOption Master = new(null, "Master");

    public override string ToString() => Name;
}

/// <summary>
/// The mixer: channels that are audio paths, independent of tracks (ADR 0023), with their sends, and the master. The
/// inspector's Mixer section and the mixer console both show this one view model, so they edit through the same
/// commands. Meters are read from the audio engine by <see cref="RefreshMeters"/> on a UI timer.
/// </summary>
public sealed partial class MixerViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _syncing;

    internal MixerViewModel(MainViewModel owner) => _owner = owner;

    public ObservableCollection<MixerChannelRow> Channels { get; } = [];

    /// <summary>The master's meter.</summary>
    public MeterLevels MasterMeter { get; } = new();

    /// <summary>
    /// Reads a meter from the running audio engine: a channel's, or the master's for null. Null when audio is off or
    /// the channel is not in the engine's graph yet. Set by the app; called on the UI thread.
    /// </summary>
    public Func<MixerChannelId?, MeterReading?>? MeterSource { get; set; }

    /// <summary>
    /// Updates every meter from <see cref="MeterSource"/>, for a UI timer (about 30 times a second). Allocates nothing;
    /// each <see cref="MeterLevels"/> raises its <see cref="MeterLevels.Updated"/> event for the view to redraw.
    /// </summary>
    /// <param name="timestamp">Now, in <see cref="System.Diagnostics.Stopwatch"/> ticks (peak hold times use it).</param>
    public void RefreshMeters(long timestamp)
    {
        var source = MeterSource;
        MasterMeter.Update(source?.Invoke(null), timestamp);
        // By index: enumerating an ObservableCollection allocates an enumerator.
        for (var i = 0; i < Channels.Count; i++)
        {
            Channels[i].Meter.Update(source?.Invoke(Channels[i].Id), timestamp);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MasterGainText), nameof(Summary))]
    public partial double MasterGain { get; set; }

    public string MasterGainText => MixerChannelRow.FormatGain(MasterGain);

    public bool IsEmpty => Channels.Count == 0;

    /// <summary>The section's one-line state when closed: "2 channels · Master 0.0 dB".</summary>
    public string Summary => Channels.Count switch
    {
        0 => $"Master {MasterGainText}",
        1 => $"1 channel · Master {MasterGainText}",
        var n => $"{n} channels · Master {MasterGainText}",
    };

    internal DeviceDefinitionLookup Definitions => _owner.Devices.Lookup;

    internal void Sync(Project project)
    {
        var mixer = project.Mixer;
        _syncing = true;
        try
        {
            MasterGain = mixer.MasterGainDecibels;
            while (Channels.Count > mixer.Channels.Length)
            {
                Channels.RemoveAt(Channels.Count - 1);
            }

            for (var i = 0; i < mixer.Channels.Length; i++)
            {
                if (i == Channels.Count)
                {
                    Channels.Add(new MixerChannelRow(this));
                }

                Channels[i].Sync(project, mixer.Channels[i]);
            }

            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(Summary));
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnMasterGainChanged(double value)
    {
        if (!_syncing)
        {
            _owner.Execute(RoutingCommands.SetMasterGain(Math.Clamp(value, MixerChannel.MinGainDecibels, MixerChannel.MaxGainDecibels)));
        }
    }

    [RelayCommand]
    private void NewChannel() =>
        _owner.Execute(RoutingCommands.AddMixerChannel(MixerChannel.Create(string.Create(CultureInfo.InvariantCulture, $"Channel {Channels.Count + 1}"))));

    internal void Update(MixerChannel channel, string? mergeKey = null) => _owner.Execute(RoutingCommands.UpdateMixerChannel(channel, Definitions, mergeKey));

    internal void Remove(MixerChannelId channel) => _owner.Execute(RoutingCommands.RemoveMixerChannel(channel));

    internal void Post(Action action) => _owner.Post(action);

    internal void AddSend(MixerChannelId channel, MixerChannelId target) =>
        _owner.Execute(RoutingCommands.AddMixerSend(channel, new MixerSend(target), Definitions));

    internal void UpdateSend(MixerChannelId channel, MixerSend send, string? mergeKey = null) =>
        _owner.Execute(RoutingCommands.UpdateMixerSend(channel, send, Definitions, mergeKey));

    internal void RemoveSend(MixerChannelId channel, MixerChannelId target) => _owner.Execute(RoutingCommands.RemoveMixerSend(channel, target));
}

/// <summary>One mixer channel, edited in place.</summary>
public sealed partial class MixerChannelRow : ObservableObject
{
    private readonly MixerViewModel _mixer;
    private MixerChannel _channel = MixerChannel.Create("-");
    private bool _syncing;

    internal MixerChannelRow(MixerViewModel mixer) => _mixer = mixer;

    public MixerChannelId Id => _channel.Id;

    public double MinimumGain { get; } = MixerChannel.MinGainDecibels;

    public double MaximumGain { get; } = MixerChannel.MaxGainDecibels;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Gain { get; set; }

    [ObservableProperty]
    public partial double Pan { get; set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial bool IsSoloed { get; set; }

    public ObservableCollection<MixerOutputOption> Outputs { get; } = [];

    [ObservableProperty]
    public partial MixerOutputOption? Output { get; set; }

    public ObservableCollection<MixerSendRow> Sends { get; } = [];

    /// <summary>Channels this one can add a send to: every other channel it does not send to yet.</summary>
    public ObservableCollection<MixerOutputOption> SendTargets { get; } = [];

    public bool CanAddSend => SendTargets.Count > 0;

    /// <summary>Choosing a target here adds a send to it (post-fader, 0 dB); the choice then clears.</summary>
    [ObservableProperty]
    public partial MixerOutputOption? SendToAdd { get; set; }

    /// <summary>What feeds this channel, in words (tracks, racks, and devices connected to it; channels routed or sending to it).</summary>
    [ObservableProperty]
    public partial string Sources { get; set; } = string.Empty;

    /// <summary>"Group", "Bus", "Return", or empty: how the channel is used, from what feeds it.</summary>
    [ObservableProperty]
    public partial string Role { get; set; } = string.Empty;

    public MeterLevels Meter { get; } = new();

    public string GainText => FormatGain(Gain);

    internal static string FormatGain(double decibels) => string.Create(CultureInfo.InvariantCulture, $"{decibels:+0.0;-0.0;0.0} dB");

    internal void Sync(Project project, MixerChannel channel)
    {
        var mixer = project.Mixer;
        _syncing = true;
        try
        {
            _channel = channel;
            Name = channel.Name;
            Gain = channel.GainDecibels;
            Pan = channel.Pan;
            IsMuted = channel.IsMuted;
            IsSoloed = channel.IsSoloed;
            var options = new List<MixerOutputOption> { MixerOutputOption.Master };
            options.AddRange(mixer.Channels.Where(c => c.Id != channel.Id).Select(c => new MixerOutputOption(c.Id, c.Name)));
            Replace(Outputs, options);
            Output = Outputs.FirstOrDefault(o => o.Channel == channel.Output) ?? MixerOutputOption.Master;
            Replace(SendTargets, [.. options.Skip(1).Where(o => channel.FindSend(o.Channel!.Value) is null)]);
            OnPropertyChanged(nameof(CanAddSend));
            while (Sends.Count > channel.Sends.Length)
            {
                Sends.RemoveAt(Sends.Count - 1);
            }

            for (var i = 0; i < channel.Sends.Length; i++)
            {
                if (i == Sends.Count)
                {
                    Sends.Add(new MixerSendRow(_mixer, this));
                }

                var send = channel.Sends[i];
                Sends[i].Sync(send, mixer.Find(send.Target)?.Name ?? "Missing channel");
            }

            (Sources, Role) = Describe(project, channel);
            OnPropertyChanged(nameof(GainText));
        }
        finally
        {
            _syncing = false;
        }
    }

    private static void Replace(ObservableCollection<MixerOutputOption> target, List<MixerOutputOption> options)
    {
        if (!target.SequenceEqual(options))
        {
            target.Clear();
            foreach (var option in options)
            {
                target.Add(option);
            }
        }
    }

    private static (string Sources, string Role) Describe(Project project, MixerChannel channel)
    {
        var node = SignalNode.Mixer(channel.Id);
        var connected = project.Connections.Where(c => c.Kind == SignalKind.Audio && c.Destination == node).Select(c => c.Source).ToList();
        var names = connected.Select(source => SignalRoutingValidator.Describe(project, source)).ToList();
        var routed = project.Mixer.Channels.Where(c => c.Output == channel.Id).Select(c => c.Name).ToList();
        var sending = project.Mixer.Channels.Where(c => c.FindSend(channel.Id) is not null).Select(c => c.Name).ToList();
        var group = connected.Any(s => s.Kind == SignalNodeKind.Track && project.Sequence.FindTrack(s.AsTrack()) is { Role: TrackRole.Group });
        List<string> parts = [.. names, .. routed.Select(n => $"{n} (output)"), .. sending.Select(n => $"{n} (send)")];
        var role = group ? "Group" : routed.Count > 0 ? "Bus" : sending.Count > 0 ? "Return" : string.Empty;
        return (parts.Count == 0 ? "Nothing feeds this channel." : $"From {string.Join(", ", parts)}", role);
    }

    partial void OnNameChanged(string value)
    {
        if (!_syncing && value.Length is > 0 and <= MixerChannel.MaxNameLength)
        {
            _mixer.Update(_channel with { Name = value });
        }
    }

    partial void OnGainChanged(double value)
    {
        OnPropertyChanged(nameof(GainText));
        if (!_syncing)
        {
            _mixer.Update(_channel with { GainDecibels = Math.Clamp(value, MinimumGain, MaximumGain) }, $"gain:{Id}");
        }
    }

    partial void OnPanChanged(double value)
    {
        if (!_syncing)
        {
            _mixer.Update(_channel with { Pan = Math.Clamp(value, -1, 1) }, $"pan:{Id}");
        }
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (!_syncing)
        {
            _mixer.Update(_channel with { IsMuted = value });
        }
    }

    partial void OnIsSoloedChanged(bool value)
    {
        if (!_syncing)
        {
            _mixer.Update(_channel with { IsSoloed = value });
        }
    }

    partial void OnOutputChanged(MixerOutputOption? value)
    {
        if (!_syncing && value is not null)
        {
            _mixer.Update(_channel with { Output = value.Channel });
        }
    }

    [RelayCommand]
    private void Remove() => _mixer.Remove(Id);

    /// <summary>Adds a post-fader send at 0 dB to <paramref name="target"/>.</summary>
    [RelayCommand]
    private void AddSend(MixerOutputOption? target)
    {
        if (target?.Channel is { } channel)
        {
            _mixer.AddSend(Id, channel);
        }
    }

    partial void OnSendToAddChanged(MixerOutputOption? value)
    {
        if (value is not null && !_syncing)
        {
            // Later: the edit replaces the list the choice was made from, which must not happen while it is choosing.
            _mixer.Post(() =>
            {
                SendToAdd = null;
                AddSend(value);
            });
        }
    }
}

/// <summary>One send of a mixer channel: its target, level, and whether it is taken before the fader.</summary>
public sealed partial class MixerSendRow : ObservableObject
{
    private readonly MixerViewModel _mixer;
    private readonly MixerChannelRow _channel;
    private MixerSend _send = new(default);
    private bool _syncing;

    internal MixerSendRow(MixerViewModel mixer, MixerChannelRow channel)
    {
        _mixer = mixer;
        _channel = channel;
    }

    public MixerChannelId Target => _send.Target;

    public double MinimumLevel { get; } = MixerChannel.MinGainDecibels;

    public double MaximumLevel { get; } = MixerChannel.MaxGainDecibels;

    [ObservableProperty]
    public partial string TargetName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LevelText))]
    public partial double Level { get; set; }

    [ObservableProperty]
    public partial bool IsPreFader { get; set; }

    public string LevelText => MixerChannelRow.FormatGain(Level);

    internal void Sync(MixerSend send, string targetName)
    {
        _syncing = true;
        try
        {
            _send = send;
            TargetName = targetName;
            Level = send.LevelDecibels;
            IsPreFader = send.PreFader;
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnLevelChanged(double value)
    {
        if (!_syncing)
        {
            _mixer.UpdateSend(_channel.Id, _send with { LevelDecibels = Math.Clamp(value, MinimumLevel, MaximumLevel) }, $"send:{_channel.Id}:{Target}");
        }
    }

    partial void OnIsPreFaderChanged(bool value)
    {
        if (!_syncing)
        {
            _mixer.UpdateSend(_channel.Id, _send with { PreFader = value });
        }
    }

    [RelayCommand]
    private void Remove() => _mixer.RemoveSend(_channel.Id, Target);
}
