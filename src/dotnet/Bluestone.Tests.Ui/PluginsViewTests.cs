using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Desktop.Services;
using Bluestone.Desktop.Views;
using Bluestone.Plugins;
using Bluestone.Plugins.Scanning;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// The plugins view opened from the menu, with real scanner processes: it lists what the scan found, a module that
/// crashed its scanner shows as quarantined, and its buttons work with the mouse.
/// </summary>
public sealed class PluginsViewTests : IDisposable
{
    private const string GoodManifest = """
        {
          "format": "bluestone-reference-plugin",
          "version": 1,
          "moduleId": "bluestone.reference",
          "plugins": [
            { "pluginId": "reference.gain", "name": "Reference Gain", "vendor": "Bluestone", "kind": "AudioEffect", "version": "1.0.0" },
            { "pluginId": "reference.transpose", "name": "Reference Transpose", "vendor": "Bluestone", "kind": "MidiEffect", "version": "1.0.0" }
          ]
        }
        """;

    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "plugin-data", Guid.NewGuid().ToString("N"));
    private readonly PluginScanner _scanner;
    private readonly PluginLibrary _library;

    public PluginsViewTests()
    {
        var folder = Path.Combine(_directory, "plugins");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "reference.bluestone-reference-plugin"), GoodManifest);
        File.WriteAllText(Path.Combine(folder, "crash.bluestone-reference-plugin"), "#bluestone-test: crash-scanner\n{}");
        _scanner = new PluginScanner(new PluginScannerOptions(Path.Combine(_directory, "cache")) { ScanTimeout = TimeSpan.FromSeconds(30) });
        _library = new PluginLibrary(_scanner, [folder]);
    }

    public void Dispose()
    {
        _library.Dispose();
        _scanner.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>The Plugins page of Settings, shown in the main window in place of the workspace.</summary>
    private static PluginSettingsPage OpenedPlugins(AppUnderTest app)
    {
        app.Settle();
        Assert.True(app.Window.Settings.IsOpen, "Settings should be open.");
        Assert.False(app.Find<Grid>("WorkspaceGrid").IsVisible);
        return app.Window.GetVisualDescendants().OfType<PluginSettingsPage>().SingleOrDefault(p => p.IsEffectivelyVisible)
            ?? throw new InvalidOperationException("The plugins page did not open.");
    }

    [Fact]
    public Task PluginsFromTheFileMenu_ListsScannedPluginsAndTheQuarantinedModule_AndRescansWithTheMouse() => HeadlessApp.RunAsync(async () =>
    {
        await _library.ScanAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var app = await AppUnderTest.OpenAsync(pluginLibrary: new PluginsViewModel(_library, new AvaloniaDispatcher()));

        app.Click(app.Find<DropDownButton>("FileMenuButton"));
        app.Click(OpenedMenuItem(app, "Plugins…"));

        var view = OpenedPlugins(app);
        var plugins = Assert.IsType<PluginsViewModel>(view.DataContext);
        Assert.Equal(["Reference Gain", "Reference Transpose"], Texts(view.FindControl<ItemsControl>("PluginList")!).Where(t => t.StartsWith("Reference", StringComparison.Ordinal)));
        Assert.Contains("crash.bluestone-reference-plugin", Texts(view.FindControl<ItemsControl>("QuarantinedList")!));
        Assert.Equal("2 plugins available from 2 modules. 1 module quarantined.", view.FindControl<TextBlock>("StatusLine")!.Text);
        app.SaveFrame("settings-plugins");

        app.Click(AppUnderTest.Within<Button>(view, b => Equals(b.Content, "Rescan all")));
        await AppUnderTest.WaitUntilAsync(() => !plugins.IsBusy, "the rescan to finish");

        Assert.Equal("Rescanned. 2 plugins available from 2 modules. 1 module quarantined.", view.FindControl<TextBlock>("StatusLine")!.Text);
        Assert.Single(plugins.Quarantined);

        app.Click(AppUnderTest.Within<Button>(view, b => Equals(b.Content, "Retry")));
        await AppUnderTest.WaitUntilAsync(() => !plugins.IsBusy, "the retry to finish");

        Assert.StartsWith("crash.bluestone-reference-plugin is still quarantined.", view.FindControl<TextBlock>("StatusLine")!.Text, StringComparison.Ordinal);
        app.Window.Settings.Close();
        app.Settle();
        Assert.True(app.Find<Grid>("WorkspaceGrid").IsVisible);
    });

    [Fact]
    public Task PluginsInTheMacOSWindowMenu_OpensTheSameView() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(pluginLibrary: new PluginsViewModel(_library, new AvaloniaDispatcher()), nativeMenuBar: true);
        var window = NativeMenu.GetMenu(app.Window)!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Window");
        var item = window.Menu!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Plugins…");

        item.Command!.Execute(null);
        app.Settle();

        Assert.IsType<PluginsViewModel>(OpenedPlugins(app).DataContext);
        Assert.Equal(SettingsPage.Plugins, app.Window.Settings.SelectedCategory?.Page);
    });

    [Fact]
    public Task TheHostingSettings_AreChosenWithTheMouse_SavedAndAppliedToTheDeviceHost() => HeadlessApp.RunAsync(async () =>
    {
        await _library.ScanAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var manager = new PluginHostManager(new PluginHostOptions { DataDirectory = Path.Combine(_directory, "data") });
        var store = new PluginHostingSettingsStore(Path.Combine(_directory, "plugin-hosting.json"));
        var session = new ProjectSession();
        await using var bridge = new PluginDeviceHost(manager, session, _library, hosting: store.Load());
        var dispatcher = new AvaloniaDispatcher();
        await using var app = await AppUnderTest.OpenAsync(session, _ => bridge, pluginLibrary: new PluginsViewModel(_library, dispatcher, bridge, store));

        app.Click(app.Find<DropDownButton>("FileMenuButton"));
        app.Click(OpenedMenuItem(app, "Plugins…"));
        var view = OpenedPlugins(app);
        var plugins = Assert.IsType<PluginsViewModel>(view.DataContext);
        Assert.True(view.FindControl<StackPanel>("HostingSection")!.IsEffectivelyVisible);
        Assert.Equal("By plugin", view.FindControl<ComboBox>("GlobalHosting")!.SelectionBoxItem?.ToString());

        app.Choose(view.FindControl<ComboBox>("GlobalHosting")!, i => i is HostingChoice { Mode: HostingMode.Together });
        await AppUnderTest.WaitUntilAsync(() => !plugins.IsBusy, "the hosting change to finish");
        app.Click(view.FindControl<CheckBox>("AutoRestart")!);
        await AppUnderTest.WaitUntilAsync(() => !plugins.IsBusy, "the restart setting to apply");
        var gainRow = AppUnderTest.Within<ComboBox>(view.FindControl<ItemsControl>("PluginList")!, c => c.DataContext is PluginRow { Name: "Reference Gain" });
        Assert.Equal("Global (Together)", gainRow.SelectionBoxItem?.ToString());
        app.Choose(gainRow, i => i is HostingChoice { Mode: HostingMode.Individually });
        await AppUnderTest.WaitUntilAsync(() => !plugins.IsBusy, "the plugin's hosting to apply");
        app.SaveFrame("settings-plugins-hosting");

        var saved = store.Load();
        Assert.Equal(HostingMode.Together, saved.Mode);
        Assert.True(saved.AutoRestart);
        Assert.Equal(HostingMode.Individually, saved.ModeFor(plugins.Plugins.Single(p => p.Name == "Reference Gain").Definition));
        Assert.Equal(HostingMode.Together, saved.ModeFor(plugins.Plugins.Single(p => p.Name == "Reference Transpose").Definition));
        Assert.Equal(saved, bridge.Hosting);
        Assert.Equal("Reference Gain is now hosted individually.", view.FindControl<TextBlock>("StatusLine")!.Text);
        Assert.Equal(0, manager.WorkerLaunchCount);
        app.Window.Settings.Close();
        app.Settle();
        Assert.True(app.Find<Grid>("WorkspaceGrid").IsVisible);
    });

    private static MenuItem OpenedMenuItem(AppUnderTest app, string header)
    {
        var button = app.Find<DropDownButton>("FileMenuButton");
        var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
        Assert.True(flyout.IsOpen, "The File menu should open when clicked.");
        return flyout.Items.OfType<MenuItem>().Single(i => Equals(i.Header, header));
    }

    private static List<string> Texts(Control scope) =>
        [.. scope.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];
}
