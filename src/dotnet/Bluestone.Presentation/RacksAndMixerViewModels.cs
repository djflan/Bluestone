using System.Collections.ObjectModel;
using System.Globalization;
using Bluestone.Application.Editing;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Projects;
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

            var sources = project.ConnectionsTo(Bluestone.Domain.Routing.SignalNode.Rack(racks[i].Id)).Count();
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
