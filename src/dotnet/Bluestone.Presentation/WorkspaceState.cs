using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bluestone.Presentation;

/// <summary>A window's position and size in screen pixels, as the platform reports them.</summary>
public readonly record struct WindowBounds(double X, double Y, double Width, double Height)
{
    [JsonIgnore]
    public double Right => X + Width;

    [JsonIgnore]
    public double Bottom => Y + Height;
}

/// <summary>A display's working area (excluding task bars and menu bars) in screen pixels.</summary>
public readonly record struct ScreenArea(double X, double Y, double Width, double Height)
{
    [JsonIgnore]
    public double Right => X + Width;

    [JsonIgnore]
    public double Bottom => Y + Height;

    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>A Device View's binding and presentation: what it shows and how, never the chain itself (ADR 0036).</summary>
public sealed record DeviceViewState
{
    /// <summary>The track the view is locked to, or null when it follows the selection (or is locked to a rack).</summary>
    public Guid? LockedTrack { get; init; }

    /// <summary>The rack the view is locked to, if any.</summary>
    public Guid? LockedRack { get; init; }

    /// <summary>The devices this view shows collapsed. Another view of the same chain keeps its own set.</summary>
    public ImmutableArray<Guid> Collapsed { get; init; } = [];

    public Guid? SelectedDevice { get; init; }

    public double ScrollOffset { get; init; }
}

/// <summary>One open view instance: where it lives and, for a detached view, where its window was on each display setup.</summary>
public sealed record ViewState
{
    public required string Id { get; init; }

    public required WorkspaceViewKind Kind { get; init; }

    public required ViewPlacement Placement { get; init; }

    /// <summary>The detached window's bounds, keyed by <see cref="DisplayConfiguration.Signature"/>.</summary>
    public ImmutableDictionary<string, WindowBounds> DetachedBounds { get; init; } = ImmutableDictionary<string, WindowBounds>.Empty;

    public DeviceViewState? Device { get; init; }
}

/// <summary>The Browser's remembered search and filter.</summary>
public sealed record BrowserState
{
    public string Search { get; init; } = string.Empty;

    public BrowserCategory Category { get; init; } = BrowserCategory.All;
}

/// <summary>
/// The local presentation of a workspace: open views, where they are docked, sizes, and windows (ADR 0036). It is not part of
/// the project: saving it never marks the project edited, and a project opened elsewhere falls back to the user's defaults.
/// Everything refers to tracks, racks, and devices by stable ID.
/// </summary>
public sealed record WorkspaceState
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public ImmutableArray<ViewState> Views { get; init; } = [];

    public string? ActiveDeviceView { get; init; }

    public double LeftExtent { get; init; } = WorkspaceViewModel.DefaultLeftExtent;

    public double RightExtent { get; init; } = WorkspaceViewModel.DefaultRightExtent;

    public double DetailExtent { get; init; } = WorkspaceViewModel.DefaultDetailExtent;

    public bool IsDetailVisible { get; init; } = true;

    public LowerPane DetailPane { get; init; } = LowerPane.Devices;

    public BrowserState Browser { get; init; } = new();

    /// <summary>The main window's bounds, keyed by <see cref="DisplayConfiguration.Signature"/>.</summary>
    public ImmutableDictionary<string, WindowBounds> MainWindow { get; init; } = ImmutableDictionary<string, WindowBounds>.Empty;

    /// <summary>
    /// This state as a reusable default for any project: the layout, sizes, windows, and Browser filter, without bindings to this
    /// project's tracks, racks, and devices.
    /// </summary>
    public WorkspaceState AsDefaults() => this with
    {
        Views = [.. Views.Select(v => v.Device is null ? v : v with { Device = new DeviceViewState() })],
    };
}

/// <summary>Identifies the set of displays attached, so a layout made for two monitors is not lost while working on one.</summary>
public static class DisplayConfiguration
{
    public static string Signature(IEnumerable<ScreenArea> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        var parts = screens
            .Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.X:0},{s.Y:0},{s.Width:0}x{s.Height:0}"))
            .Order(StringComparer.Ordinal);
        return string.Join(";", parts);
    }
}

/// <summary>
/// Puts saved windows back safely on the displays that are attached now: never off-screen, the layout for this display setup when
/// it is known, and nothing lost when a monitor is missing for a while (ADR 0036).
/// </summary>
public static class LayoutRecovery
{
    /// <summary>How much of a window's top edge must be on a display for it to count as reachable.</summary>
    public const double MinimumVisible = 48;

    /// <summary>Whether enough of <paramref name="bounds"/>' title area is on one of <paramref name="screens"/> to grab and move it.</summary>
    public static bool IsReachable(WindowBounds bounds, IReadOnlyList<ScreenArea> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        var titleBottom = bounds.Y + Math.Min(MinimumVisible, bounds.Height);
        foreach (var screen in screens)
        {
            var width = Math.Min(bounds.Right, screen.Right) - Math.Max(bounds.X, screen.X);
            var height = Math.Min(titleBottom, screen.Bottom) - Math.Max(bounds.Y, screen.Y);
            if (width >= MinimumVisible && height >= Math.Min(MinimumVisible, bounds.Height) / 2 && bounds.Y >= screen.Y - 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The bounds to restore a window at: those saved for the current display setup, otherwise any saved bounds that still land on an
    /// attached display, made to fit it. Null when none do, such as when the window's monitor is missing.
    /// </summary>
    public static WindowBounds? Restore(ImmutableDictionary<string, WindowBounds> saved, IReadOnlyList<ScreenArea> screens)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(screens);
        if (screens.Count == 0)
        {
            return null;
        }

        if (saved.TryGetValue(DisplayConfiguration.Signature(screens), out var exact) && IsReachable(exact, screens))
        {
            return Fit(exact, screens);
        }

        foreach (var (_, bounds) in saved.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (IsReachable(bounds, screens))
            {
                return Fit(bounds, screens);
            }
        }

        return null;
    }

    /// <summary>Shrinks and moves <paramref name="bounds"/> so it lies within the display holding most of its title area.</summary>
    public static WindowBounds Fit(WindowBounds bounds, IReadOnlyList<ScreenArea> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        var screen = screens
            .OrderByDescending(s => Overlap(bounds, s))
            .ThenBy(s => s.Contains(bounds.X, bounds.Y) ? 0 : 1)
            .First();
        var width = Math.Min(bounds.Width, screen.Width);
        var height = Math.Min(bounds.Height, screen.Height);
        var x = Math.Clamp(bounds.X, screen.X, screen.Right - width);
        var y = Math.Clamp(bounds.Y, screen.Y, screen.Bottom - height);
        return new WindowBounds(x, y, width, height);
    }

    /// <summary>A sensible window of <paramref name="width"/> by <paramref name="height"/> centred on the first display.</summary>
    public static WindowBounds Centered(double width, double height, IReadOnlyList<ScreenArea> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        var screen = screens.Count > 0 ? screens[0] : new ScreenArea(0, 0, width, height);
        var w = Math.Min(width, screen.Width);
        var h = Math.Min(height, screen.Height);
        return new WindowBounds(screen.X + ((screen.Width - w) / 2), screen.Y + ((screen.Height - h) / 2), w, h);
    }

    private static double Overlap(WindowBounds bounds, ScreenArea screen)
    {
        var width = Math.Min(bounds.Right, screen.Right) - Math.Max(bounds.X, screen.X);
        var height = Math.Min(bounds.Bottom, screen.Bottom) - Math.Max(bounds.Y, screen.Y);
        return width > 0 && height > 0 ? width * height : 0;
    }
}

/// <summary>
/// Reads and writes workspace state as small, versioned JSON files outside the project: the user's defaults for every project, and
/// the local view state of each project, keyed by its path. A file that cannot be read is ignored, never fatal.
/// </summary>
public sealed class WorkspaceStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;

    public WorkspaceStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
    }

    public string DefaultsPath => Path.Combine(_directory, "workspace-defaults.json");

    /// <summary>Where the view state of the project at <paramref name="projectPath"/> is kept.</summary>
    public string PathFor(string projectPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectPath);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(projectPath))))[..16];
        return Path.Combine(_directory, "workspaces", key + ".json");
    }

    /// <summary>The state for a project: its own if it has been opened here before, otherwise the user's defaults, otherwise null.</summary>
    public WorkspaceState? Load(string? projectPath) =>
        (projectPath is null ? null : Read(PathFor(projectPath))) ?? Read(DefaultsPath);

    /// <summary>Saves <paramref name="state"/> for the project and as the user's defaults.</summary>
    public void Save(string? projectPath, WorkspaceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Write(DefaultsPath, state.AsDefaults());
        if (projectPath is not null)
        {
            Write(PathFor(projectPath), state);
        }
    }

    public static string Serialize(WorkspaceState state) => JsonSerializer.Serialize(state, Json);

    /// <summary>The state in <paramref name="json"/>, or null if it is not a workspace this version understands.</summary>
    public static WorkspaceState? Deserialize(string json)
    {
        try
        {
            var state = JsonSerializer.Deserialize<WorkspaceState>(json, Json);
            return state is { Version: WorkspaceState.CurrentVersion } && IsWellFormed(state) ? state : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="state"/> has everything applying it relies on: JSON can null out or omit what the records
    /// default, and name enum values that do not exist, so a hand-edited or damaged file is caught here rather than when applied.
    /// </summary>
    private static bool IsWellFormed(WorkspaceState state) =>
        state.Browser is { Search: not null } browser && Enum.IsDefined(browser.Category)
        && Enum.IsDefined(state.DetailPane)
        && double.IsFinite(state.LeftExtent) && double.IsFinite(state.RightExtent) && double.IsFinite(state.DetailExtent)
        && AreWellFormed(state.MainWindow)
        && !state.Views.IsDefault
        && state.Views.All(IsWellFormed)
        && state.Views.Select(v => v.Id).Distinct(StringComparer.Ordinal).Count() == state.Views.Length;

    private static bool IsWellFormed(ViewState? view) =>
        view is not null
        && !string.IsNullOrEmpty(view.Id)
        && Enum.IsDefined(view.Kind)
        && Enum.IsDefined(view.Placement)
        && AreWellFormed(view.DetachedBounds)
        && (view.Device is null || (!view.Device.Collapsed.IsDefault && double.IsFinite(view.Device.ScrollOffset)));

    private static bool AreWellFormed(ImmutableDictionary<string, WindowBounds>? bounds) =>
        bounds is not null
        && bounds.Values.All(b => double.IsFinite(b.X) && double.IsFinite(b.Y) && double.IsFinite(b.Width) && double.IsFinite(b.Height) && b.Width > 0 && b.Height > 0);

    private static WorkspaceState? Read(string path)
    {
        try
        {
            return File.Exists(path) ? Deserialize(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Write(string path, WorkspaceState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, Serialize(state));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // View state is a convenience; failing to save it must never get in the way.
        }
    }
}
