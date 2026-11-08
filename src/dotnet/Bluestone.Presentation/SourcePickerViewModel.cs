using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Bluestone.Application.Editing;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>
/// One row of a <see cref="SourcePickerViewModel"/>: a track or rack heading, or one of its points (its input, the point
/// after each of its devices, its output) that a device input can take signal from.
/// </summary>
public sealed record SourceRow(string Name, string Owner, int AccentIndex, SignalNode? Node = null, DeviceRole? Role = null)
{
    /// <summary>A track or rack heading: not chosen itself.</summary>
    public bool IsHeading => Node is null;

    /// <summary>The point is after a device: the row shows the device's role colour.</summary>
    public bool HasRole => Role is not null;

    public override string ToString() => Node is null ? Owner : $"{Owner} · {Name}";
}

/// <summary>
/// Chooses where a device input (a receiver's source, a plugin's sidechain) takes its signal from: every track and rack,
/// and in each its input, the point after each device, and its output, in signal order. Only points that can be
/// connected are offered: the right kind of signal is there, and the connection would not feed back. Typing filters by
/// track, rack, or device name.
/// </summary>
public sealed partial class SourcePickerViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<SourceRow>> _build;
    private readonly Action<SignalNode> _choose;
    private readonly Action? _clear;
    private IReadOnlyList<SourceRow> _all = [];

    internal SourcePickerViewModel(Func<IReadOnlyList<SourceRow>> build, Action<SignalNode> choose, Action? clear = null, string clearText = "Disconnect")
    {
        _build = build;
        _choose = choose;
        _clear = clear;
        ClearText = clearText;
    }

    /// <summary>The input has a source that the picker's footer can remove.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearCommand))]
    public partial bool CanClear { get; internal set; }

    /// <summary>The footer's words: "Remove sidechain", or "Disconnect".</summary>
    public string ClearText { get; }

    /// <summary>What the musician typed: rows whose track, rack, or point contains it, with their headings.</summary>
    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    public ObservableCollection<SourceRow> Rows { get; } = [];

    /// <summary>Nothing matches: the picker says so instead of showing an empty list.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>Raised after a source is chosen, so the popup closes.</summary>
    public event EventHandler? Chosen;

    /// <summary>Builds the list from the project as it is now, with no filter. Called each time the picker opens.</summary>
    public void Open()
    {
        _all = _build();
        Query = string.Empty;
        Filter();
    }

    partial void OnQueryChanged(string value) => Filter();

    [RelayCommand]
    private void Choose(SourceRow? row)
    {
        if (row?.Node is { } node)
        {
            _choose(node);
            Chosen?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear()
    {
        _clear?.Invoke();
        Chosen?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Chooses the first point listed (Enter in the search box).</summary>
    [RelayCommand]
    private void ChooseFirst() => Choose(Rows.FirstOrDefault(r => !r.IsHeading));

    private void Filter()
    {
        var query = Query.Trim();
        Rows.Clear();
        SourceRow? heading = null;
        var headingShown = false;
        foreach (var row in _all)
        {
            if (row.IsHeading)
            {
                heading = row;
                headingShown = false;
                continue;
            }

            if (query.Length > 0 && !row.Owner.Contains(query, StringComparison.OrdinalIgnoreCase) && !row.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (heading is not null && !headingShown)
            {
                Rows.Add(heading);
                headingShown = true;
            }

            Rows.Add(row);
        }

        IsEmpty = Rows.Count == 0;
    }
}

/// <summary>
/// One input of a device panel (ADR 0037): a Note or Audio Receiver's source, or an effect plugin's sidechain. Shows where
/// its signal comes from, and changes it through a <see cref="SourcePickerViewModel"/> as one undoable step.
/// </summary>
public sealed partial class DeviceInputViewModel : ObservableObject
{
    private readonly DeviceViewViewModel _view;
    private DeviceId _device;

    internal DeviceInputViewModel(DeviceViewViewModel view, DeviceInputPort port)
    {
        _view = view;
        Port = port;
        Picker = new SourcePickerViewModel(BuildChoices, SetSource, () => Clear(), port.Id == DeviceInputPort.Sidechain ? "Remove sidechain" : "Disconnect");
    }

    public DeviceInputPort Port { get; }

    /// <summary>"Source", or "Sidechain".</summary>
    public string Name => Port.Name;

    public bool IsSidechain => Port.Id == DeviceInputPort.Sidechain;

    public SourcePickerViewModel Picker { get; }

    /// <summary>Where the signal comes from: "Pad · After Transpose", or "Choose a source".</summary>
    [ObservableProperty]
    public partial string SourceText { get; private set; } = "Choose a source";

    /// <summary>The source's track or rack, for the chip's tooltip and screen readers.</summary>
    [ObservableProperty]
    public partial string SourceDetail { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearCommand))]
    public partial bool HasSource { get; private set; }

    /// <summary>The source track's colour index, or -1 for a rack or no source.</summary>
    [ObservableProperty]
    public partial int SourceAccent { get; private set; } = -1;

    internal SignalNode Destination => SignalNode.DeviceInput(_device, Port.Id);

    /// <summary>The device this input belongs to (set at the first sync).</summary>
    internal DeviceId? Device { get; private set; }

    internal void Sync(Project project, DeviceId device)
    {
        _device = device;
        Device = device;
        var source = project.ConnectionsTo(Destination).Select(c => c.Source).FirstOrDefault();
        HasSource = source != default;
        Picker.CanClear = HasSource;
        if (!HasSource)
        {
            SourceText = IsSidechain ? "No key" : "Choose a source";
            SourceDetail = IsSidechain ? "Nothing feeds this plugin's sidechain. Choose where its key signal comes from." : "Nothing feeds this receiver yet. Choose where its signal comes from.";
            SourceAccent = -1;
            return;
        }

        var (owner, point, accent) = Describe(project, source);
        SourceText = $"{owner} · {point}";
        SourceDetail = IsSidechain ? $"Keyed by {owner}, {point.ToLowerInvariant()}" : $"Receives from {owner}, {point.ToLowerInvariant()}";
        SourceAccent = accent;
    }

    [RelayCommand(CanExecute = nameof(HasSource))]
    private void Clear()
    {
        var existing = _view.Project.ConnectionsTo(Destination).Select(c => RoutingCommands.Disconnect(c.Id)).ToList();
        if (existing.Count > 0)
        {
            _view.Execute(ProjectCommands.Batch(IsSidechain ? "Remove Sidechain" : "Remove Source", existing));
        }
    }

    // Replaces whatever feeds this input with source, as one undo step. A refused connection leaves it as it was.
    private void SetSource(SignalNode source)
    {
        var project = _view.Project;
        var commands = project.ConnectionsTo(Destination).Select(c => RoutingCommands.Disconnect(c.Id)).ToList();
        commands.Add(RoutingCommands.Connect(SignalConnection.Create(Port.Kind, source, Destination), _view.Definitions));
        _view.Execute(ProjectCommands.Batch(IsSidechain ? "Set Sidechain" : "Set Source", commands));
    }

    private List<SourceRow> BuildChoices()
    {
        var project = _view.Project;
        var definitions = _view.Definitions;
        var rows = new List<SourceRow>();
        var others = project.Connections.Where(c => c.Destination != Destination).ToImmutableArray();
        var baseline = project with { Connections = others };
        foreach (var track in project.Sequence.Tracks)
        {
            Add(project.ChainOf(track.Id), track.Name, _view.AccentOf(track.Id), SignalNode.Track(track.Id));
        }

        foreach (var rack in project.Chains.Where(c => c.Owner.Kind == ChainOwnerKind.Rack))
        {
            Add(rack, $"Rack: {DeviceViewViewModel.RackName(rack)}", -1, SignalNode.Rack(rack.Id));
        }

        return rows;

        void Add(DeviceChain? chain, string owner, int accent, SignalNode output)
        {
            var points = new List<SourceRow>();
            if (chain is { Devices.Length: > 0 })
            {
                Offer(SignalNode.ChainInput(chain.Id), "Input", null);
            }

            foreach (var device in chain?.Devices ?? [])
            {
                Offer(SignalNode.Device(device.Id), $"After {device.DisplayName}", DevicePanelViewModel.RoleOf(definitions(device.Definition.Id)));
            }

            Offer(output, "Output", null);
            if (points.Count > 0)
            {
                rows.Add(new SourceRow(owner, owner, accent));
                rows.AddRange(points);
            }

            void Offer(SignalNode node, string name, DeviceRole? role)
            {
                if (CanConnect(baseline, node, definitions))
                {
                    points.Add(new SourceRow(name, owner, accent, node, role));
                }
            }
        }
    }

    // Whether a connection from source would be accepted: none of the errors the validator finds involve it.
    private bool CanConnect(Project baseline, SignalNode source, DeviceDefinitionLookup definitions)
    {
        var candidate = SignalConnection.Create(Port.Kind, source, Destination);
        var issues = SignalRoutingValidator.Validate(baseline with { Connections = baseline.Connections.Add(candidate) }, definitions);
        return !SignalRoutingValidator.Errors(issues).Any(i => i.Connections.Contains(candidate.Id));
    }

    private (string Owner, string Point, int Accent) Describe(Project project, SignalNode node)
    {
        string OwnerOf(DeviceChain chain) => chain.Owner.Kind == ChainOwnerKind.Track && project.Sequence.FindTrack(chain.Owner.Track) is { } track
            ? track.Name
            : $"Rack: {DeviceViewViewModel.RackName(chain)}";

        int AccentOf(DeviceChain chain) => chain.Owner.Kind == ChainOwnerKind.Track ? _view.AccentOf(chain.Owner.Track) : -1;

        switch (node.Kind)
        {
            case SignalNodeKind.ChainInput when project.FindChain(new DeviceChainId(node.Id)) is { } chain:
                return (OwnerOf(chain), "Input", AccentOf(chain));
            case SignalNodeKind.Device when project.FindDevice(new DeviceId(node.Id)) is var (chain, device):
                return (OwnerOf(chain), $"After {device.DisplayName}", AccentOf(chain));
            case SignalNodeKind.Track when project.Sequence.FindTrack(new TrackId(node.Id)) is { } track:
                return (track.Name, "Output", _view.AccentOf(track.Id));
            case SignalNodeKind.Rack when project.FindChain(new DeviceChainId(node.Id)) is { } rack:
                return ($"Rack: {DeviceViewViewModel.RackName(rack)}", "Output", -1);
            default:
                return (SignalRoutingValidator.Describe(project, node), "Output", -1);
        }
    }
}
