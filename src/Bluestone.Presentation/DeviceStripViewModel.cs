using System.Collections.ObjectModel;
using System.Globalization;
using Bluestone.Application.Editing;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Infrastructure.Projects;
using Bluestone.Signal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>A device that can be added to a chain.</summary>
public sealed record DeviceChoice(DeviceDefinition Definition)
{
    public string Name => Definition.Name;

    public string Detail => Definition.Origin == DeviceOrigin.Plugin
        ? $"{Definition.Vendor ?? "Plugin"} · runs in a plugin worker"
        : Definition.Category switch
        {
            DeviceCategory.MidiEffect => "MIDI effect",
            DeviceCategory.Instrument => "Instrument",
            DeviceCategory.AudioEffect => "Audio effect",
            _ => "Device",
        };

    public override string ToString() => Name;
}

/// <summary>
/// A device strip: the selected track's chain, or a rack being edited, first device to last (ADR 0022).
/// Devices are inserted, removed, reordered, and bypassed here; their routing taps are shown as indicators,
/// which open the same connection list the routing inspector edits.
/// </summary>
public sealed partial class DeviceStripViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private TrackId? _track;
    private DeviceChainId? _rack;
    private DeviceChainId? _chain;

    internal DeviceStripViewModel(MainViewModel owner) => _owner = owner;

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    public ObservableCollection<DeviceChoice> AvailableDevices { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddDeviceCommand))]
    public partial DeviceChoice? DeviceToAdd { get; set; }

    [ObservableProperty]
    public partial DeviceViewModel? SelectedDevice { get; set; }

    /// <summary>Whether there is a chain to show: a single selected track, or a rack being edited.</summary>
    [ObservableProperty]
    public partial bool HasTrack { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackToTrackCommand))]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    public partial bool IsEditingRack { get; private set; }

    public string EmptyText => IsEditingRack
        ? "No devices. Events go straight to the rack's connections."
        : "No devices. Events go straight to the track's connections.";

    /// <summary>"Devices" for a track; the rack's name while a rack is edited.</summary>
    [ObservableProperty]
    public partial string Heading { get; private set; } = "Devices";

    /// <summary>The rack being edited, if one is.</summary>
    internal DeviceChainId? Rack => _rack;

    public bool IsEmpty => Devices.Count == 0;

    internal TrackId? Track => _track;

    internal DeviceDefinitionLookup Definitions => _owner.Devices.Lookup;

    /// <summary>Shows a rack's chain instead of the selected track's, until <see cref="BackToTrackCommand"/> or a new selection.</summary>
    internal void EditRack(DeviceChainId rack)
    {
        _rack = rack;
        _owner.SyncDeviceViews();
    }

    /// <summary>Stops editing a rack without refreshing, when the track selection changes (which refreshes).</summary>
    internal void LeaveRack() => _rack = null;

    [RelayCommand(CanExecute = nameof(IsEditingRack))]
    internal void BackToTrack()
    {
        _rack = null;
        _owner.SyncDeviceViews();
    }

    internal void Sync(Project project, TrackId? track, DeviceCatalog catalog)
    {
        if (_rack is { } editing && project.FindChain(editing) is not { Owner.Kind: ChainOwnerKind.Rack })
        {
            _rack = null;
        }

        _track = track;
        HasTrack = track is not null || _rack is not null;
        IsEditingRack = _rack is not null;
        Heading = _rack is { } rackId ? $"Rack: {RackName(project.FindChain(rackId)!)}" : "Devices";
        if (!AvailableDevices.Select(c => c.Definition).SequenceEqual(catalog.Definitions))
        {
            AvailableDevices.Clear();
            foreach (var definition in catalog.Definitions)
            {
                AvailableDevices.Add(new DeviceChoice(definition));
            }
        }

        var chain = _rack is { } shown ? project.FindChain(shown) : track is { } id ? project.ChainOf(id) : null;
        _chain = chain?.Id;
        var devices = chain?.Devices ?? [];
        var selected = SelectedDevice?.Id;
        while (Devices.Count > devices.Length)
        {
            Devices.RemoveAt(Devices.Count - 1);
        }

        for (var i = 0; i < devices.Length; i++)
        {
            if (i == Devices.Count)
            {
                Devices.Add(new DeviceViewModel(this));
            }

            Devices[i].Sync(project, devices[i], i, devices.Length, catalog.Find(devices[i].Definition.Id), _owner.DeviceStatus(devices[i]));
        }

        SelectedDevice = Devices.FirstOrDefault(d => d.Id == selected);
        OnPropertyChanged(nameof(IsEmpty));
        SavePresetCommand.NotifyCanExecuteChanged();
        LoadPresetCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAddDevice))]
    private void AddDevice()
    {
        if (DeviceToAdd is not { } choice || (_rack is null && _track is null))
        {
            return;
        }

        var device = DeviceInstance.Create(choice.Definition.ToReference());
        var at = SelectedDevice is { } after ? after.Index + 1 : (int?)null;
        var command = _rack is { } rack
            ? DeviceCommands.InsertDevice(rack, device, Definitions, at)
            : DeviceCommands.InsertDevice(_track!.Value, device, Definitions, at);
        if (_owner.Execute(command))
        {
            SelectedDevice = Devices.FirstOrDefault(d => d.Id == device.Id);
        }
    }

    private bool CanAddDevice() => DeviceToAdd is not null;

    [RelayCommand(CanExecute = nameof(HasChain))]
    private async Task SavePresetAsync()
    {
        if (_chain is not { } chain || await _owner.UserInteraction.PickSaveFileAsync("Save Chain Preset", "Chain.bluestone-chain", FileFilters.ChainPreset) is not { } path)
        {
            return;
        }

        var preset = DeviceCommands.SavePreset(_owner.Project, chain, Path.GetFileNameWithoutExtension(path));
        await File.WriteAllBytesAsync(path, ChainPresetSerializer.Serialize(preset)).ConfigureAwait(true);
        _owner.AddMessage(MessageSeverity.Info, "Presets", $"Saved {preset.Devices.Length} devices as \"{preset.Name}\".");
    }

    private bool HasChain() => _chain is not null;

    [RelayCommand(CanExecute = nameof(CanLoadPreset))]
    private async Task LoadPresetAsync()
    {
        if ((_rack is null && _track is null) || await _owner.UserInteraction.PickOpenFileAsync("Load Chain Preset", [FileFilters.ChainPreset]) is not { } path)
        {
            return;
        }

        try
        {
            var preset = ChainPresetSerializer.Deserialize(await File.ReadAllBytesAsync(path).ConfigureAwait(true));
            _owner.Execute(_rack is { } rack ? DeviceCommands.LoadPreset(rack, preset, Definitions) : DeviceCommands.LoadPreset(_track!.Value, preset, Definitions));
        }
        catch (Exception ex) when (ex is ProjectFormatException or IOException or UnauthorizedAccessException)
        {
            _owner.AddMessage(MessageSeverity.Warning, "Presets", $"{Path.GetFileName(path)} could not be loaded: {ex.Message}");
        }
    }

    private bool CanLoadPreset() => _track is not null || _rack is not null;

    internal static string RackName(DeviceChain rack) => rack.Name.Length > 0 ? rack.Name : "Rack";

    internal void Remove(DeviceViewModel device) => _owner.Execute(DeviceCommands.RemoveDevice(device.Id, Definitions));

    internal void Move(DeviceViewModel device, int index) => _owner.Execute(DeviceCommands.MoveDevice(device.Id, index, Definitions));

    internal void SetBypassed(DeviceViewModel device, bool bypassed) => _owner.Execute(DeviceCommands.SetBypassed(device.Id, bypassed, Definitions));

    internal void SetParameter(DeviceViewModel device, ParameterId parameter, ControlValue value) => _owner.Execute(DeviceCommands.SetParameter(device.Id, parameter, value));

    internal void ShowRouting(DeviceViewModel device) => _owner.Connections.RouteFrom(device.Id);

    internal Task RestartAsync(DeviceViewModel device) => _owner.RestartDeviceAsync(device.Id);

    internal Task OpenEditorAsync(DeviceViewModel device) => _owner.OpenDeviceEditorAsync(device.Id);
}

/// <summary>One device in the strip, with its status in words, its routing taps, and its parameters.</summary>
public sealed partial class DeviceViewModel : ObservableObject
{
    private readonly DeviceStripViewModel _strip;
    private bool _syncing;

    internal DeviceViewModel(DeviceStripViewModel strip) => _strip = strip;

    public DeviceId Id { get; private set; }

    public int Index { get; private set; }

    [ObservableProperty]
    public partial string Name { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Kind { get; private set; } = string.Empty;

    /// <summary>How the device is doing, as text: "Built-in", "Not installed", "Crashed".</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    /// <summary>The device as the strip shows it, with its status when it needs attention: "[Vital: Crashed]".</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool NeedsAttention { get; private set; }

    /// <summary>A plugin that crashed or stopped responding can be restarted from its last saved state.</summary>
    [ObservableProperty]
    public partial bool CanRestart { get; private set; }

    /// <summary>A running VST3 or CLAP plugin can show its own editor, in a window of its worker process.</summary>
    [ObservableProperty]
    public partial bool CanOpenEditor { get; private set; }

    [ObservableProperty]
    public partial bool IsBypassed { get; set; }

    /// <summary>Where this device's output is also sent ("→ Strings, MU2000 B"), or empty.</summary>
    [ObservableProperty]
    public partial string RoutingIndicator { get; private set; } = string.Empty;

    public bool HasRouting => RoutingIndicator.Length > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    public partial bool CanMoveUp { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    public partial bool CanMoveDown { get; private set; }

    public ObservableCollection<ParameterViewModel> Parameters { get; } = [];

    internal void Sync(Project project, DeviceInstance device, int index, int count, DeviceDefinition? definition, DeviceStatus status)
    {
        _syncing = true;
        try
        {
            Id = device.Id;
            Index = index;
            Name = device.DisplayName;
            Kind = definition is null ? "Unknown device" : new DeviceChoice(definition).Detail;
            Status = status.Text;
            NeedsAttention = status.NeedsAttention;
            CanRestart = status.CanRestart;
            CanOpenEditor = status.CanOpenEditor;
            Title = status.NeedsAttention ? $"[{device.DisplayName}: {status.Text}]" : device.DisplayName;
            IsBypassed = device.IsBypassed;
            CanMoveUp = index > 0;
            CanMoveDown = index < count - 1;
            var destinations = project.ConnectionsFrom(SignalNode.Device(device.Id)).Select(c => SignalRoutingValidator.Describe(project, c.Destination)).ToList();
            RoutingIndicator = destinations.Count == 0 ? string.Empty : "→ " + string.Join(", ", destinations);
            OnPropertyChanged(nameof(HasRouting));

            var descriptors = definition?.Parameters ?? [];
            if (!Parameters.Select(p => p.Id).SequenceEqual(descriptors.Select(d => d.Id)))
            {
                Parameters.Clear();
                foreach (var descriptor in descriptors)
                {
                    Parameters.Add(new ParameterViewModel(this, descriptor));
                }
            }

            foreach (var parameter in Parameters)
            {
                parameter.Sync(device.ValueOf(parameter.Id) ?? parameter.Descriptor.DefaultValue);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnIsBypassedChanged(bool value)
    {
        if (!_syncing)
        {
            _strip.SetBypassed(this, value);
        }
    }

    [RelayCommand]
    private void Remove() => _strip.Remove(this);

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => _strip.Move(this, Index - 1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => _strip.Move(this, Index + 1);

    /// <summary>Opens the routing inspector with this device's output as the source.</summary>
    [RelayCommand]
    private void ShowRouting() => _strip.ShowRouting(this);

    [RelayCommand]
    private Task RestartAsync() => _strip.RestartAsync(this);

    [RelayCommand]
    private Task OpenEditorAsync() => _strip.OpenEditorAsync(this);

    internal void SetParameter(ParameterId parameter, ControlValue value)
    {
        if (!_syncing)
        {
            _strip.SetParameter(this, parameter, value);
        }
    }
}

/// <summary>One parameter, shown and edited in its own units, stored normalized.</summary>
public sealed partial class ParameterViewModel : ObservableObject
{
    private readonly DeviceViewModel _device;
    private bool _syncing;

    internal ParameterViewModel(DeviceViewModel device, ParameterDescriptor descriptor)
    {
        _device = device;
        Descriptor = descriptor;
    }

    public ParameterDescriptor Descriptor { get; }

    public ParameterId Id => Descriptor.Id;

    public string Name => Descriptor.Name;

    public double Minimum => Descriptor.Minimum;

    public double Maximum => Descriptor.Maximum;

    /// <summary>The slider's step: one position for a stepped parameter, otherwise 1% of the range.</summary>
    public double Step => Descriptor.Steps > 0 ? (Descriptor.Maximum - Descriptor.Minimum) / Descriptor.Steps : (Descriptor.Maximum - Descriptor.Minimum) / 100;

    [ObservableProperty]
    public partial double Value { get; set; }

    public string ValueText => Descriptor.Steps > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{Value:0.##} {Descriptor.Unit}").Trim()
        : string.Create(CultureInfo.InvariantCulture, $"{Value:0.###} {Descriptor.Unit}").Trim();

    internal void Sync(ControlValue stored)
    {
        _syncing = true;
        try
        {
            Value = Descriptor.ToPlain(stored);
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnValueChanged(double value)
    {
        OnPropertyChanged(nameof(ValueText));
        if (!_syncing)
        {
            _device.SetParameter(Id, Descriptor.ToStored(Math.Clamp(value, Minimum, Maximum)));
        }
    }
}

/// <summary>A device's status in words, and whether it needs the musician's attention.</summary>
public sealed record DeviceStatus(string Text, bool NeedsAttention = false, bool CanRestart = false, bool CanOpenEditor = false)
{
    public static DeviceStatus Of(DeviceInstance device, DeviceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Find(device.Definition.Id) is not { } definition)
        {
            return new("Not installed", NeedsAttention: true);
        }

        if (device.IsBypassed)
        {
            return new("Bypassed");
        }

        return definition.Origin == DeviceOrigin.Plugin ? new("Plugin") : new("Built-in");
    }
}
