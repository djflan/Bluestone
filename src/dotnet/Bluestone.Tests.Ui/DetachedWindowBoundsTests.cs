using Avalonia;
using Avalonia.Controls;
using Bluestone.Desktop.Views;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// A detached window's bounds are remembered only while it is in its normal state, as the main window's are: a minimized
/// window can report an off-screen position on Windows, and a maximized one the display's size.
/// </summary>
public sealed class DetachedWindowBoundsTests : IDisposable
{
    private static readonly WindowBounds Normal = new(40, 30, 360, 600);
    private static readonly PixelPoint OffScreen = new(-32000, -32000);

    private readonly string _settings = Path.Combine(Path.GetTempPath(), $"bluestone-detached-bounds-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_settings))
        {
            Directory.Delete(_settings, recursive: true);
        }
    }

    [Theory]
    [InlineData(WindowState.Minimized)]
    [InlineData(WindowState.Maximized)]
    public Task Quitting_WithADetachedWindowNotInItsNormalState_KeepsItsNormalBounds(WindowState state) => HeadlessApp.RunAsync(async () =>
    {
        var store = new WorkspaceStore(_settings);
        var app = await AppUnderTest.OpenAsync(layoutStore: store);
        var display = DisplayConfiguration.Signature(app.ViewModel.Screens());
        var browser = app.ViewModel.Workspace.Browser;
        browser.DetachCommand.Execute(null);
        app.Settle();
        browser.RememberBounds(display, Normal);
        var window = Assert.Single(app.Window.OwnedWindows.OfType<ViewWindow>());
        window.WindowState = state;
        window.Position = OffScreen;

        app.Window.Close();
        app.Settle();

        var saved = store.Load(null)!.Views.Single(v => v.Kind == WorkspaceViewKind.Browser);
        Assert.Equal(ViewPlacement.Detached, saved.Placement);
        Assert.Equal(Normal, saved.DetachedBounds[display]);
        await app.ViewModel.DisposeAsync();
    });

    [Theory]
    [InlineData(WindowState.Minimized)]
    [InlineData(WindowState.Maximized)]
    public Task ClosingADetachedWindowNotInItsNormalState_KeepsItsNormalBounds(WindowState state) => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(layoutStore: new WorkspaceStore(_settings));
        var display = DisplayConfiguration.Signature(app.ViewModel.Screens());
        var browser = app.ViewModel.Workspace.Browser;
        browser.DetachCommand.Execute(null);
        app.Settle();
        browser.RememberBounds(display, Normal);
        var window = Assert.Single(app.Window.OwnedWindows.OfType<ViewWindow>());
        window.WindowState = state;
        window.Position = OffScreen;

        window.Close();
        app.Settle();

        Assert.True(browser.IsDocked);
        Assert.Equal(Normal, browser.DetachedBounds[display]);
    });
}
