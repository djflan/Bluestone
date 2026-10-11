namespace Bluestone.Presentation;

public sealed record FileFilter(string Name, IReadOnlyList<string> Extensions);

public enum UnsavedChangesChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>Everything the view models need from the user, implemented by the UI layer.</summary>
public interface IUserInteraction
{
    Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters);

    Task<string?> PickSaveFileAsync(string title, string suggestedName, FileFilter filter);

    /// <summary>Asks a yes/no question. <paramref name="destructive"/> styles the confirm button as a warning.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive);

    Task<UnsavedChangesChoice> AskToSaveChangesAsync(string projectName);
}

/// <summary>Runs work on the UI thread.</summary>
public interface IUiDispatcher
{
    /// <summary>True when called on the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Queues work to run later on the UI thread.</summary>
    void Post(Action action);
}

public static class UiDispatcherExtensions
{
    /// <summary>Runs <paramref name="action"/> now when on the UI thread, otherwise queues it there.</summary>
    public static void Run(this IUiDispatcher dispatcher, Action action)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Post(action);
        }
    }
}

public static class FileFilters
{
    public static readonly FileFilter Project = new("Bluestone project", ["bluestone"]);
    public static readonly FileFilter Midi = new("Standard MIDI file", ["mid", "midi", "smf"]);
    public static readonly FileFilter ChainPreset = new("Bluestone chain preset", ["bluestone-chain"]);
}
