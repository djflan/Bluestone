using System.Collections.ObjectModel;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Projects;
using Bluestone.Profiles;
using Bluestone.Signal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>The Browser's filters. Every category draws on content Bluestone already has; nothing is indexed for it.</summary>
public enum BrowserCategory
{
    All,
    Instruments,
    NoteEffects,
    AudioEffects,
    Plugins,
    BuiltIn,
    Racks,
    Hardware,
}

/// <summary>What a Browser item is.</summary>
public enum BrowserItemKind
{
    /// <summary>A device definition, built-in or plugin: inserted into a chain.</summary>
    Device,

    /// <summary>A rack in this project: opened in the active Device View.</summary>
    Rack,

    /// <summary>A hardware instrument profile: assigned to a track's output in the inspector, not inserted into a chain.</summary>
    HardwareProfile,
}

/// <summary>A category the Browser filters by, with its label.</summary>
public sealed record BrowserCategoryOption(BrowserCategory Category, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One thing in the Browser.</summary>
public sealed partial class BrowserItem : ObservableObject
{
    internal BrowserItem(BrowserItemKind kind, string key, string name, string detail, string group)
    {
        Kind = kind;
        Key = key;
        Name = name;
        Detail = detail;
        Group = group;
    }

    public BrowserItemKind Kind { get; }

    /// <summary>Stable across refreshes: the definition ID, rack ID, or profile ID.</summary>
    public string Key { get; }

    public string Name { get; }

    public string Detail { get; }

    /// <summary>"Instrument", "Note FX", "Rack", "Hardware".</summary>
    public string Group { get; }

    public DeviceDefinition? Definition { get; init; }

    public DeviceChainId? Rack { get; init; }

    public DeviceRole Role => Definition?.Category switch
    {
        DeviceCategory.MidiEffect => DeviceRole.Note,
        DeviceCategory.Instrument => DeviceRole.Instrument,
        DeviceCategory.AudioEffect => DeviceRole.Audio,
        _ => DeviceRole.Other,
    };

    /// <summary>Whether the item can be dropped into a Device View's chain.</summary>
    public bool IsInsertable => Kind == BrowserItemKind.Device;

    /// <summary>Suggested for the chain in the active Device View. Others are still allowed: unusual chains are the musician's choice.</summary>
    [ObservableProperty]
    public partial bool IsRecommended { get; internal set; }
}

/// <summary>
/// The Browser (ADR 0036): one dockable palette for everything that can go into a project, filtered by category and search and
/// ranked for the chain in the active Device View. The Device View is the canvas: dragging an item onto it picks the chain and
/// the exact place; Enter or a double-click inserts into the last Device View used, and does nothing but say so when there is none.
/// </summary>
public sealed partial class BrowserViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly List<BrowserItem> _all = [];
    private bool _dragging;

    internal BrowserViewModel(MainViewModel owner)
    {
        _owner = owner;
        SelectedCategory = Categories[0];
    }

    public IReadOnlyList<BrowserCategoryOption> Categories { get; } =
    [
        new(BrowserCategory.All, "All"),
        new(BrowserCategory.Instruments, "Instruments"),
        new(BrowserCategory.NoteEffects, "Note FX"),
        new(BrowserCategory.AudioEffects, "Audio FX"),
        new(BrowserCategory.Plugins, "Plugins"),
        new(BrowserCategory.BuiltIn, "Built-in"),
        new(BrowserCategory.Racks, "Racks"),
        new(BrowserCategory.Hardware, "Hardware"),
    ];

    /// <summary>The items that match the category and search, recommended first.</summary>
    public ObservableCollection<BrowserItem> Items { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial BrowserCategoryOption SelectedCategory { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InsertCommand))]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    public partial BrowserItem? SelectedItem { get; set; }

    /// <summary>
    /// What Return, a double-click, or the footer's button does with the selected item, and where: "Into Bass, after
    /// Arpeggiator", "Opens in the Device View", "Sets Bass's MIDI profile", or why it cannot.
    /// </summary>
    [ObservableProperty]
    public partial string TargetText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasTarget { get; private set; }

    /// <summary>The footer button's word for the selected item: Insert a device, Open a rack, Use a hardware profile on a track.</summary>
    public string ActionText => SelectedItem?.Kind switch
    {
        BrowserItemKind.Rack => "Open",
        BrowserItemKind.HardwareProfile => "Use on Track",
        _ => "Insert",
    };

    partial void OnSelectedItemChanged(BrowserItem? value) => UpdateTarget();

    public bool IsEmpty => Items.Count == 0;

    /// <summary>"No matches for “xyz”." or "No plugins found. Scan in Settings." when the list is empty.</summary>
    [ObservableProperty]
    public partial string EmptyText { get; private set; } = string.Empty;

    /// <summary>The empty list is one that scanning plugins would fill, so it offers the plugin settings.</summary>
    [ObservableProperty]
    public partial bool CanOpenPluginSettings { get; private set; }

    /// <summary>Raised to show Settings ▸ Plugins; the host decides how.</summary>
    public event EventHandler? PluginSettingsRequested;

    [RelayCommand]
    private void OpenPluginSettings() => PluginSettingsRequested?.Invoke(this, EventArgs.Empty);

    partial void OnSearchTextChanged(string value) => Filter();

    partial void OnSelectedCategoryChanged(BrowserCategoryOption value) => Filter();

    /// <summary>The search and filter, for local view state.</summary>
    public BrowserState Capture() => new() { Search = SearchText, Category = SelectedCategory?.Category ?? BrowserCategory.All };

    public void Restore(BrowserState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        SearchText = state.Search;
        SelectedCategory = Categories.FirstOrDefault(c => c.Category == state.Category) ?? Categories[0];
    }

    /// <summary>Shows <paramref name="category"/> with the search cleared, as the inspector's "Add an instrument" does.</summary>
    public void ShowCategory(BrowserCategory category)
    {
        SearchText = string.Empty;
        SelectedCategory = Categories.First(c => c.Category == category);
    }

    /// <summary>Rebuilds the item list from the device catalog, the project's racks, and the installed hardware profiles.</summary>
    internal void Sync(DeviceCatalog catalog, Project project, ProfileCatalog profiles)
    {
        _all.Clear();
        foreach (var definition in catalog.Definitions)
        {
            _all.Add(new BrowserItem(BrowserItemKind.Device, definition.Id.Value, definition.Name, DeviceDescriptions.Kind(definition), DeviceDescriptions.Category(definition)) { Definition = definition });
        }

        foreach (var rack in project.Chains.Where(c => c.Owner.Kind == ChainOwnerKind.Rack))
        {
            var count = rack.Devices.Length;
            _all.Add(new BrowserItem(BrowserItemKind.Rack, rack.Id.Value.ToString(), DeviceViewViewModel.RackName(rack), $"Rack · {count} device{(count == 1 ? string.Empty : "s")}", "Rack") { Rack = rack.Id });
        }

        foreach (var profile in profiles.Profiles)
        {
            _all.Add(new BrowserItem(BrowserItemKind.HardwareProfile, profile.Id, profile.Name, $"MIDI profile · {profile.Manufacturer ?? "Hardware"}", "Hardware"));
        }

        Retarget();
    }

    /// <summary>Re-ranks for the active Device View's chain and says where an insertion would go.</summary>
    internal void Retarget()
    {
        var target = _owner.Workspace?.InsertionTarget;
        UpdateTarget();
        var chain = target?.ChainDefinitions.ToList() ?? [];
        var hasInstrument = chain.Any(d => d?.Category == DeviceCategory.Instrument);
        foreach (var item in _all)
        {
            item.IsRecommended = target is not null && item.Definition is { } definition && definition.Category switch
            {
                // An empty or note-only chain most likely wants something to make sound; after that, effects.
                DeviceCategory.Instrument => !hasInstrument,
                DeviceCategory.AudioEffect => hasInstrument,
                DeviceCategory.MidiEffect => !hasInstrument,
                _ => false,
            };
        }

        if (!_dragging)
        {
            Filter();
        }

        InsertCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Says where the selected item's action lands, after the selection, the active view, or its selected device changed.</summary>
    internal void UpdateTarget()
    {
        if (SelectedItem?.Kind == BrowserItemKind.Rack)
        {
            HasTarget = true;
            TargetText = "Opens in the Device View";
            return;
        }

        if (SelectedItem?.Kind == BrowserItemKind.HardwareProfile)
        {
            var tracks = _owner.SelectedTracks;
            HasTarget = tracks.Count > 0;
            TargetText = tracks.Count switch
            {
                0 => "Select a track to use this profile",
                1 => $"Sets {tracks[0].Name}'s MIDI profile",
                _ => $"Sets the MIDI profile of {tracks.Count} tracks",
            };
            return;
        }

        var target = _owner.Workspace?.InsertionTarget;
        HasTarget = target is not null;
        TargetText = target is null ? "Open a Device View on a track to insert"
            : target.SelectedDevice is { } after ? $"Into {target.Heading}, after {after.Name}"
            : target.Devices.Count == 0 ? $"Into {target.Heading}, as its first device"
            : $"Into {target.Heading}, at the end";
    }

    /// <summary>
    /// Keeps the list as it is while one of its items is being dragged: a drop that changes the chain re-ranks the items, and
    /// rebuilding the list would remove the drag's source mid-session (on macOS that ends the app).
    /// </summary>
    public void BeginDrag() => _dragging = true;

    /// <summary>Ends <see cref="BeginDrag"/> and applies any re-ranking the drag held back.</summary>
    public void EndDrag()
    {
        _dragging = false;
        Filter();
    }

    /// <summary>Inserts <paramref name="item"/> into the last Device View used, after its selected device. Never guesses a target.</summary>
    public bool InsertIntoActive(BrowserItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind == BrowserItemKind.Rack && item.Rack is { } rack)
        {
            _owner.Workspace.ShowRack(rack);
            return true;
        }

        if (item.Kind == BrowserItemKind.HardwareProfile)
        {
            if (_owner.Selection.UseProfile(item.Key))
            {
                return true;
            }

            _owner.AddMessage(MessageSeverity.Info, "Browser", $"{item.Name} is a hardware profile: select a track to use it, then choose the track's MIDI output in the inspector.");
            return false;
        }

        if (item.Definition is not { } definition)
        {
            return false;
        }

        if (_owner.Workspace.InsertionTarget is not { } target)
        {
            _owner.AddMessage(MessageSeverity.Info, "Browser", $"{item.Name} was not inserted: there is no Device View showing a track or rack. Drag it onto a Device View instead.");
            return false;
        }

        return target.Insert(definition);
    }

    [RelayCommand(CanExecute = nameof(CanInsert))]
    private void Insert()
    {
        if (SelectedItem is { } item)
        {
            InsertIntoActive(item);
        }
    }

    private bool CanInsert() => SelectedItem is not null;

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    private void Filter()
    {
        var category = SelectedCategory?.Category ?? BrowserCategory.All;
        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = _all
            .Where(i => InCategory(i, category))
            .Where(i => terms.All(t => i.Name.Contains(t, StringComparison.CurrentCultureIgnoreCase) || i.Detail.Contains(t, StringComparison.CurrentCultureIgnoreCase)))
            .OrderByDescending(i => i.IsRecommended)
            .ThenBy(i => i.Kind)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (!Items.SequenceEqual(matches))
        {
            var selected = SelectedItem?.Key;
            Items.Clear();
            foreach (var item in matches)
            {
                Items.Add(item);
            }

            SelectedItem = Items.FirstOrDefault(i => i.Key == selected);
        }

        EmptyText = terms.Length > 0 ? $"Nothing matches \u201c{SearchText.Trim()}\u201d."
            : category switch
            {
                BrowserCategory.Plugins => "No plugins found. Scan for plugins in Settings.",
                BrowserCategory.Instruments => "No instruments yet. Plugin instruments appear here once they are scanned in Settings.",
                BrowserCategory.AudioEffects => "No audio effects yet. Plugin effects appear here once they are scanned in Settings.",
                BrowserCategory.Racks => "This project has no racks. Create one in the inspector's Racks section.",
                BrowserCategory.Hardware => "No hardware profiles are installed.",
                _ => "Nothing here yet.",
            };
        CanOpenPluginSettings = Items.Count == 0 && terms.Length == 0 && category is BrowserCategory.Plugins or BrowserCategory.Instruments or BrowserCategory.AudioEffects;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static bool InCategory(BrowserItem item, BrowserCategory category) => category switch
    {
        // Hardware profiles describe external instruments; they are not chain devices, so they have their own category.
        BrowserCategory.All => item.Kind != BrowserItemKind.HardwareProfile,
        BrowserCategory.Instruments => item.Definition?.Category == DeviceCategory.Instrument,
        BrowserCategory.NoteEffects => item.Definition?.Category == DeviceCategory.MidiEffect,
        BrowserCategory.AudioEffects => item.Definition?.Category == DeviceCategory.AudioEffect,
        BrowserCategory.Plugins => item.Definition?.Origin == DeviceOrigin.Plugin,
        BrowserCategory.BuiltIn => item.Definition?.Origin == DeviceOrigin.BuiltIn,
        BrowserCategory.Racks => item.Kind == BrowserItemKind.Rack,
        BrowserCategory.Hardware => item.Kind == BrowserItemKind.HardwareProfile,
        _ => true,
    };
}
