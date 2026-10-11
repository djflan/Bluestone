using System.Collections.ObjectModel;
using Bluestone.Application.Editing;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>Where a new connection starts: the track's output (the end of its chain) or after one of its devices.</summary>
public sealed record SourceOption(SignalNode Node, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Where a new connection can go, and which signal it carries there.</summary>
public sealed record DestinationOption(SignalNode Node, SignalKind Kind, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A connection as the inspector lists it.</summary>
public sealed record ConnectionRow(ConnectionId Id, string Text, string Detail, bool IsIncoming);

/// <summary>
/// The routing inspector for the selected track, or for the rack being edited (ADR 0023): what it sends and
/// where, what it receives, and a way to add a connection from its output or from one of its devices. It edits the project's one
/// connection list, the same list the device strip's indicators show, and refuses connections that would
/// not work or would feed back.
/// </summary>
public sealed partial class ConnectionsViewModel : ObservableObject
{
    private readonly MainViewModel _owner;

    internal ConnectionsViewModel(MainViewModel owner) => _owner = owner;

    public ObservableCollection<ConnectionRow> Outgoing { get; } = [];

    public ObservableCollection<ConnectionRow> Incoming { get; } = [];

    public ObservableCollection<SourceOption> Sources { get; } = [];

    public ObservableCollection<DestinationOption> Destinations { get; } = [];

    public IReadOnlyList<ChannelOption> ChannelChoices { get; } = ChannelOption.All;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial SourceOption? Source { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial DestinationOption? Destination { get; set; }

    /// <summary>The channel the new connection forces, or the first choice to keep each event's channel.</summary>
    [ObservableProperty]
    public partial ChannelOption? Channel { get; set; }

    [ObservableProperty]
    public partial bool HasTrack { get; private set; }

    /// <summary>Raised when a device's routing indicator asks to show this inspector.</summary>
    public event EventHandler? ShowRequested;

    internal void Sync(Project project, TrackId? track, DeviceChainId? rack = null)
    {
        Outgoing.Clear();
        Incoming.Clear();
        SignalNode node;
        DeviceChain? chain;
        string name;
        if (rack is { } rackId && project.FindChain(rackId) is { Owner.Kind: ChainOwnerKind.Rack } rackChain)
        {
            node = SignalNode.Rack(rackId);
            chain = rackChain;
            name = DeviceStripViewModel.RackName(rackChain);
        }
        else if (track is { } id && project.Sequence.FindTrack(id) is { } found)
        {
            node = SignalNode.Track(id);
            chain = project.ChainOf(id);
            name = found.Name;
        }
        else
        {
            HasTrack = false;
            Sources.Clear();
            Destinations.Clear();
            return;
        }

        HasTrack = true;
        foreach (var connection in project.Connections)
        {
            var fromHere = connection.Source == node || (connection.Source.Kind == SignalNodeKind.Device && chain?.Find(connection.Source.AsDevice()) is not null);
            if (fromHere)
            {
                var from = connection.Source == node ? string.Empty : $"After {SignalRoutingValidator.Describe(project, connection.Source)}: ";
                Outgoing.Add(new ConnectionRow(connection.Id, $"{from}{Kind(connection.Kind)} → {SignalRoutingValidator.Describe(project, connection.Destination)}", Mapping(connection), false));
            }
            else if (connection.Destination == node)
            {
                Incoming.Add(new ConnectionRow(connection.Id, $"{Kind(connection.Kind)} ← {SignalRoutingValidator.Describe(project, connection.Source)}", Mapping(connection), true));
            }
        }

        var source = Source?.Node;
        Sources.Clear();
        Sources.Add(new SourceOption(node, $"{name} output"));
        foreach (var device in chain?.Devices ?? [])
        {
            Sources.Add(new SourceOption(SignalNode.Device(device.Id), $"After {device.DisplayName}"));
        }

        Source = Sources.FirstOrDefault(s => s.Node == source) ?? Sources[0];

        var destination = Destination;
        Destinations.Clear();
        foreach (var other in project.Sequence.Tracks.Where(t => SignalNode.Track(t.Id) != node))
        {
            Destinations.Add(new DestinationOption(SignalNode.Track(other.Id), SignalKind.Events, $"Track: {other.Name}"));
        }

        foreach (var other in project.Chains.Where(c => c.Owner.Kind == ChainOwnerKind.Rack && SignalNode.Rack(c.Id) != node))
        {
            Destinations.Add(new DestinationOption(SignalNode.Rack(other.Id), SignalKind.Events, $"Rack: {DeviceStripViewModel.RackName(other)}"));
        }

        foreach (var instrument in project.Instruments)
        {
            foreach (var port in instrument.Ports)
            {
                Destinations.Add(new DestinationOption(SignalNode.ExternalPart(instrument.Id, port.Id), SignalKind.Events, instrument.Ports.Length == 1 ? $"Instrument: {instrument.Name}" : $"Instrument: {instrument.Name} · {port.Name}"));
            }
        }

        foreach (var channel in project.Mixer.Channels)
        {
            Destinations.Add(new DestinationOption(SignalNode.Mixer(channel.Id), SignalKind.Audio, $"Mixer: {channel.Name}"));
        }

        Destinations.Add(new DestinationOption(SignalNode.Master, SignalKind.Audio, "Master"));
        Destination = Destinations.FirstOrDefault(d => d == destination);
        Channel ??= ChannelOption.All[0];
    }

    /// <summary>Shows the inspector with <paramref name="device"/>'s output as the source of the next connection.</summary>
    internal void RouteFrom(DeviceId device)
    {
        Source = Sources.FirstOrDefault(s => s.Node == SignalNode.Device(device)) ?? Source;
        ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void Connect()
    {
        if (Source is not { } source || Destination is not { } destination)
        {
            return;
        }

        var mapping = destination.Kind == SignalKind.Events && Channel?.Channel is { } forced ? ChannelMapping.ForceTo(forced) : ChannelMapping.Preserve;
        var connection = SignalConnection.Create(destination.Kind, source.Node, destination.Node) with { Mapping = mapping };
        if (_owner.Execute(RoutingCommands.Connect(connection, _owner.Devices.Lookup)))
        {
            Destination = null;
        }
    }

    private bool CanConnect() => Source is not null && Destination is not null;

    [RelayCommand]
    private void Disconnect(ConnectionRow? row)
    {
        if (row is not null)
        {
            _owner.Execute(RoutingCommands.Disconnect(row.Id));
        }
    }

    private static string Kind(SignalKind kind) => kind == SignalKind.Events ? "Events" : "Audio";

    private static string Mapping(SignalConnection connection)
    {
        if (connection.Kind == SignalKind.Audio)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (!connection.Mapping.Only.IsEmpty)
        {
            parts.Add("only ch " + string.Join(", ", connection.Mapping.Only));
        }

        if (connection.Mapping.Force is { } force)
        {
            parts.Add($"ch {force}");
        }

        parts.AddRange(connection.Mapping.Remap.Select(r => $"ch {r.From} → {r.To}"));
        if (connection.Voice is { } voice)
        {
            parts.Add($"voice {voice.BankId} {voice.Program.Number}");
        }

        return parts.Count == 0 ? "each event's own channel" : string.Join(" · ", parts);
    }
}
