using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>A page of the Settings surface.</summary>
public enum SettingsPage
{
    Audio,
    Plugins,
}

/// <summary>One category in the Settings list: its page, name, one-line description, the words a search finds it by, and its content.</summary>
public sealed record SettingsCategory(SettingsPage Page, string Name, string Description, string Keywords, object? Content)
{
    /// <summary>Whether the page has something to show in this session (audio and plugins are composed by the host).</summary>
    public bool IsAvailable => Content is not null;

    public string UnavailableText => Page switch
    {
        SettingsPage.Audio => "Audio output is not available in this session.",
        _ => "Plugin hosting is not available in this session.",
    };

    public bool Matches(string search) =>
        search.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(term =>
            Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || Description.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || Keywords.Contains(term, StringComparison.CurrentCultureIgnoreCase));

    public override string ToString() => Name;
}

/// <summary>
/// Bluestone's Settings: application preferences, not project content, in a two-pane surface (categories on the left, the chosen
/// page on the right) with a search that narrows the categories. The view model knows nothing of where it is shown: the main
/// window shows it in place of the workspace and puts the workspace back unchanged on <see cref="Close"/>; a window could host it
/// the same way. Each page is an existing view model, so settings are applied and saved exactly as before.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly List<SettingsCategory> _all = [];
    private AudioViewModel? _audio;
    private PluginsViewModel? _plugins;

    public SettingsViewModel() => Rebuild();

    /// <summary>The audio output settings, once the host has composed the audio engine.</summary>
    public AudioViewModel? Audio
    {
        get => _audio;
        set
        {
            _audio = value;
            Rebuild();
        }
    }

    /// <summary>Plugin scanning and hosting, once the host has composed the plugin library.</summary>
    public PluginsViewModel? Plugins
    {
        get => _plugins;
        set
        {
            _plugins = value;
            Rebuild();
        }
    }

    /// <summary>The categories that match <see cref="SearchText"/>.</summary>
    public ObservableCollection<SettingsCategory> Categories { get; } = [];

    [ObservableProperty]
    public partial SettingsCategory? SelectedCategory { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Whether Settings is showing. The host shows and hides it; the workspace behind it is untouched.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>Nothing matches the search.</summary>
    public bool IsEmpty => Categories.Count == 0;

    /// <summary>Shows Settings at <paramref name="page"/>, or at the page shown last.</summary>
    public void Open(SettingsPage? page = null)
    {
        if (page is { } wanted)
        {
            SearchText = string.Empty;
            SelectedCategory = Categories.FirstOrDefault(c => c.Page == wanted) ?? SelectedCategory;
        }
        else if (IsEmpty)
        {
            // A search that found nothing is not worth coming back to.
            SearchText = string.Empty;
        }

        SelectedCategory ??= Categories.FirstOrDefault();
        IsOpen = true;
    }

    [RelayCommand]
    private void OpenAt(SettingsPage page) => Open(page);

    /// <summary>Hides Settings; the host puts the workspace back as it was.</summary>
    [RelayCommand]
    public void Close() => IsOpen = false;

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    partial void OnSearchTextChanged(string value) => Filter();

    private void Rebuild()
    {
        _all.Clear();
        _all.Add(new SettingsCategory(
            SettingsPage.Audio,
            "Audio",
            "The output device, sample rate, buffer size, and render-ahead; starting audio and a test tone.",
            "output device sample rate buffer latency render-ahead queue monitoring test tone engine master",
            _audio));
        _all.Add(new SettingsCategory(
            SettingsPage.Plugins,
            "Plugins",
            "Scanned VST3 and CLAP plugins, how they share worker processes, crash recovery, and quarantined modules.",
            "vst3 clap scan rescan hosting worker process sandbox crash restart quarantine folders",
            _plugins));
        Filter();
    }

    private void Filter()
    {
        var matches = _all.Where(c => c.Matches(SearchText)).ToList();
        if (!matches.SequenceEqual(Categories))
        {
            var selected = SelectedCategory?.Page;
            Categories.Clear();
            foreach (var category in matches)
            {
                Categories.Add(category);
            }

            SelectedCategory = Categories.FirstOrDefault(c => c.Page == selected) ?? Categories.FirstOrDefault();
        }

        OnPropertyChanged(nameof(IsEmpty));
    }
}
