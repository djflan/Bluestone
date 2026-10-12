using System.Collections.Immutable;
using Avalonia;
using Bluestone.Application.Sessions;
using Bluestone.Desktop.Views;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// Opening another project restores that project's local layout (ADR 0036), window bounds included: the main window and a
/// detached Browser go where that layout has them, and the next save does not give the new project the old one's windows.
/// </summary>
public sealed class ProjectLayoutTests : IDisposable
{
    private static readonly WindowBounds OtherMain = new(120, 80, 1200, 800);
    private static readonly WindowBounds OtherBrowser = new(700, 150, 360, 600);

    private readonly string _settings = Path.Combine(Path.GetTempPath(), $"bluestone-layout-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_settings))
        {
            Directory.Delete(_settings, recursive: true);
        }
    }

    [Fact]
    public Task OpeningAnotherProject_MovesTheMainWindowAndTheDetachedBrowser_AndQuittingSavesThatProjectsBounds() => HeadlessApp.RunAsync(async () =>
    {
        var store = new WorkspaceStore(_settings);
        var app = await AppUnderTest.OpenAsync(layoutStore: store);
        var display = DisplayConfiguration.Signature(app.ViewModel.Screens());
        var other = await OtherProjectAsync(store, ViewPlacement.Detached, display);
        app.ViewModel.Workspace.Browser.DetachCommand.Execute(null);
        app.Settle();
        var browserWindow = Assert.Single(app.Window.OwnedWindows.OfType<ViewWindow>());
        browserWindow.Position = new PixelPoint(40, 30);
        app.Window.Position = new PixelPoint(10, 20);

        await app.ViewModel.OpenFileAsync(other);
        app.Settle();

        Assert.Equal(new PixelPoint(120, 80), app.Window.Position);
        Assert.Equal((1200.0, 800.0), (app.Window.Width, app.Window.Height));
        var window = Assert.Single(app.Window.OwnedWindows.OfType<ViewWindow>());
        Assert.Equal(new PixelPoint(700, 150), window.Position);
        Assert.Equal((360.0, 600.0), (window.Width, window.Height));

        app.Window.Close();
        app.Settle();
        var saved = store.Load(other)!;
        Assert.Equal(OtherMain, saved.MainWindow[display]);
        Assert.Equal(OtherBrowser, saved.Views.Single(v => v.Kind == WorkspaceViewKind.Browser).DetachedBounds[display]);
        await app.ViewModel.DisposeAsync();
    });

    [Fact]
    public Task OpeningAProjectWithTheBrowserDocked_ClosesTheBrowsersWindow_WithoutGivingItThatWindowsBounds() => HeadlessApp.RunAsync(async () =>
    {
        var store = new WorkspaceStore(_settings);
        await using var app = await AppUnderTest.OpenAsync(layoutStore: store);
        var display = DisplayConfiguration.Signature(app.ViewModel.Screens());
        var other = await OtherProjectAsync(store, ViewPlacement.Docked, display);
        app.ViewModel.Workspace.Browser.DetachCommand.Execute(null);
        app.Settle();
        Assert.Single(app.Window.OwnedWindows.OfType<ViewWindow>()).Position = new PixelPoint(40, 30);

        await app.ViewModel.OpenFileAsync(other);
        app.Settle();

        Assert.True(app.ViewModel.Workspace.Browser.IsDocked);
        Assert.Empty(app.Window.OwnedWindows.OfType<ViewWindow>());
        Assert.Equal(OtherBrowser, app.ViewModel.Workspace.Browser.DetachedBounds[display]);
    });

    [Fact]
    public Task OpeningAProjectWithNoBoundsForTheseDisplays_LeavesTheMainWindowWhereItIs() => HeadlessApp.RunAsync(async () =>
    {
        var store = new WorkspaceStore(_settings);
        await using var app = await AppUnderTest.OpenAsync(layoutStore: store);
        var other = await OtherProjectAsync(store, ViewPlacement.Docked, "another display setup", new WindowBounds(9000, 0, 1000, 700));
        app.Window.Position = new PixelPoint(10, 20);

        await app.ViewModel.OpenFileAsync(other);
        app.Settle();

        Assert.Equal(new PixelPoint(10, 20), app.Window.Position);
    });

    /// <summary>A saved project with a local layout of its own, its windows saved for <paramref name="display"/>.</summary>
    private async Task<string> OtherProjectAsync(WorkspaceStore store, ViewPlacement browser, string display, WindowBounds? main = null)
    {
        Directory.CreateDirectory(_settings);
        var path = Path.Combine(_settings, "Other.bluestone");
        await new ProjectSession().SaveAsync(path);
        store.Save(path, new WorkspaceState
        {
            Views =
            [
                new ViewState { Id = "browser", Kind = WorkspaceViewKind.Browser, Placement = browser, DetachedBounds = ImmutableDictionary<string, WindowBounds>.Empty.Add(display, OtherBrowser) },
                new ViewState { Id = "device-view-1", Kind = WorkspaceViewKind.DeviceView, Placement = ViewPlacement.Docked },
            ],
            MainWindow = ImmutableDictionary<string, WindowBounds>.Empty.Add(display, main ?? OtherMain),
        });
        return path;
    }
}
