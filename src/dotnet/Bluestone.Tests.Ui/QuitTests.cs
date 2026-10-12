using System.Reflection;
using Avalonia.Controls;
using Bluestone.Application.Editing;
using Bluestone.Application.Sessions;
using Bluestone.Desktop.Views;
using Bluestone.Domain.Sequencing;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// Quitting with views detached (ADR 0036): the detached windows are kept across launches, while closing a detached window by
/// itself docks its view. Quitting is driven as the desktop lifetime drives it on ⌘Q, the app menu's Quit, or logging out: it
/// asks each window it does not own to close with a shutdown reason, and the main window's detached windows are asked first.
/// </summary>
public sealed class QuitTests : IDisposable
{
    private static readonly MethodInfo CloseCore =
        typeof(Window).GetMethod("CloseCore", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Avalonia's Window.CloseCore was not found.");

    private readonly string _settings = Path.Combine(Path.GetTempPath(), $"bluestone-quit-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_settings))
        {
            Directory.Delete(_settings, recursive: true);
        }
    }

    [Theory]
    [InlineData(WindowCloseReason.ApplicationShutdown)]
    [InlineData(WindowCloseReason.OSShutdown)]
    [InlineData(WindowCloseReason.WindowClosing)]
    public Task QuittingWithTheBrowserDetached_SavesItDetached_AndTheNextLaunchOpensItsWindowAgain(WindowCloseReason reason) => HeadlessApp.RunAsync(async () =>
    {
        var store = new WorkspaceStore(_settings);
        var app = await AppUnderTest.OpenAsync(layoutStore: store);
        app.ViewModel.Workspace.Browser.DetachCommand.Execute(null);
        app.Settle();
        Assert.Single(app.Window.OwnedWindows.OfType<ViewWindow>());

        Quit(app, reason);

        Assert.False(app.Window.IsVisible);
        Assert.Equal(ViewPlacement.Detached, Saved(store, WorkspaceViewKind.Browser));
        await app.ViewModel.DisposeAsync();

        await using var relaunched = await AppUnderTest.OpenAsync(layoutStore: store);
        Assert.True(relaunched.ViewModel.Workspace.Browser.IsDetached);
        var window = Assert.Single(relaunched.Window.OwnedWindows.OfType<ViewWindow>());
        Assert.Same(relaunched.ViewModel.Workspace.Browser, window.View);
        Assert.True(window.IsVisible);
    });

    [Fact]
    public Task ClosingADetachedWindowByItself_DocksItsView_AndQuittingThenSavesItDocked() => HeadlessApp.RunAsync(async () =>
    {
        var store = new WorkspaceStore(_settings);
        var app = await AppUnderTest.OpenAsync(layoutStore: store);
        var browser = app.ViewModel.Workspace.Browser;
        browser.DetachCommand.Execute(null);
        app.Settle();

        Assert.Single(app.Window.OwnedWindows.OfType<ViewWindow>()).Close();
        app.Settle();

        Assert.True(browser.IsDocked);
        Assert.Empty(app.Window.OwnedWindows.OfType<ViewWindow>());
        Quit(app, WindowCloseReason.ApplicationShutdown);
        Assert.Equal(ViewPlacement.Docked, Saved(store, WorkspaceViewKind.Browser));
        await app.ViewModel.DisposeAsync();
    });

    [Fact]
    public Task CancellingTheQuitAtTheUnsavedChangesPrompt_LeavesTheDetachedWindowOpenAndTheViewDetached() => HeadlessApp.RunAsync(async () =>
    {
        var session = new ProjectSession();
        session.Execute(ProjectCommands.AddTrack(Track.Create("Lead", TrackRole.Instrument)));
        var dialogs = new UnsavedChanges { Answer = UnsavedChangesChoice.Cancel };
        await using var app = await AppUnderTest.OpenAsync(session, dialogs: dialogs, layoutStore: new WorkspaceStore(_settings));
        var browser = app.ViewModel.Workspace.Browser;
        browser.DetachCommand.Execute(null);
        app.Settle();
        var window = Assert.Single(app.Window.OwnedWindows.OfType<ViewWindow>());

        Quit(app, WindowCloseReason.ApplicationShutdown);

        Assert.Equal(1, dialogs.Asked);
        Assert.True(app.Window.IsVisible);
        Assert.True(browser.IsDetached);
        Assert.True(window.IsVisible);
        Assert.Same(window, Assert.Single(app.Window.OwnedWindows.OfType<ViewWindow>()));

        // Let the window close when the test is done.
        dialogs.Answer = UnsavedChangesChoice.Discard;
    });

    /// <summary>
    /// Quits as the desktop lifetime does: the main window, which owns the detached windows, is asked to close for
    /// <paramref name="reason"/> (<see cref="WindowCloseReason.WindowClosing"/> is the main window's own close button or ⌘W).
    /// </summary>
    private static void Quit(AppUnderTest app, WindowCloseReason reason)
    {
        if (reason == WindowCloseReason.WindowClosing)
        {
            app.Window.Close();
        }
        else
        {
            CloseCore.Invoke(app.Window, [reason, false, false]);
        }

        app.Settle();
    }

    private static ViewPlacement? Saved(WorkspaceStore store, WorkspaceViewKind kind) =>
        store.Load(null)?.Views.Single(v => v.Kind == kind).Placement;

    private sealed class UnsavedChanges : IUserInteraction
    {
        public UnsavedChangesChoice Answer { get; set; }

        public int Asked { get; private set; }

        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, FileFilter filter) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive) => Task.FromResult(true);

        public Task<UnsavedChangesChoice> AskToSaveChangesAsync(string projectName)
        {
            Asked++;
            return Task.FromResult(Answer);
        }
    }
}
