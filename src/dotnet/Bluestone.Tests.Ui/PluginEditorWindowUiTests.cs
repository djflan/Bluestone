using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Bluestone.Application.Editing;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Desktop.Views;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Scanning;

namespace Bluestone.Tests.Ui;

/// <summary>
/// A real plugin editor (an installed VST3, in its worker) following the selected track, pinned, and sending its keys to
/// Bluestone, with the device panel's pin and keyboard toggles. Runs on macOS desktops where the plugin is installed.
/// </summary>
public sealed class PluginEditorWindowUiTests
{
    private const string ModulePath = "/Library/Audio/Plug-Ins/VST3/DPA_Reverside.vst3";
    private const string Name = "REVERSIDE";

    [Fact]
    public Task AnEditor_FollowsTheSelectedTrack_UnlessPinned_AndItsPanelShowsPinAndKeyboard() => HeadlessApp.RunAsync(async () =>
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Editor windows are verified on macOS only.");
        Assert.SkipWhen(Environment.GetEnvironmentVariable("CI") == "true", "CI runners have no plugins and no desktop session.");
        Assert.SkipUnless(Directory.Exists(ModulePath), $"{Name} is not installed at {ModulePath}.");
        var directory = Path.Combine(AppContext.BaseDirectory, "plugin-data", Guid.NewGuid().ToString("N"));
        using var scanner = new PluginScanner(new PluginScannerOptions(Path.Combine(directory, "scan")) { ScanTimeout = TimeSpan.FromSeconds(60) });
        var plugin = (await scanner.RescanAsync(ModulePath, ignoreQuarantine: true, TestContext.Current.CancellationToken)).Plugins.Single(p => p.DisplayName == Name);
        await using var manager = new PluginHostManager(new PluginHostOptions { DataDirectory = directory, RequestTimeout = TimeSpan.FromSeconds(30) });
        var session = new ProjectSession();
        session.Execute(ProjectCommands.AddTrack(Bluestone.Domain.Sequencing.Track.Create("Vocal", Bluestone.Domain.Sequencing.TrackRole.Hybrid)));
        session.Execute(ProjectCommands.AddTrack(Bluestone.Domain.Sequencing.Track.Create("Drums", Bluestone.Domain.Sequencing.TrackRole.Hybrid)));
        PluginDeviceHost? host = null;
        await using var app = await AppUnderTest.OpenAsync(session, s =>
        {
            host = new PluginDeviceHost(manager, s, [plugin]);
            host.SetAvailable([plugin], ImmutableDictionary<PluginIdentity, string>.Empty.Add(plugin, ModulePath));
            return host;
        });
        app.Window.Width = 1440;
        app.Window.Height = 900;
        app.ViewModel.Select(app.ViewModel.Tracks[0]);
        app.Settle();
        var view = app.ViewModel.DeviceView!;
        view.InsertAt(0, PluginDeviceHost.DefinitionOf(plugin));
        app.Settle();
        await AppUnderTest.WaitUntilAsync(() => view.Devices.Count == 1 && view.Devices[0].CanOpenEditor, "the plugin to run");
        var device = view.Devices[0].Id;
        var windows = app.ViewModel.EditorWindows!;

        try
        {
            await view.Devices[0].OpenEditorCommand.ExecuteAsync(null);
            app.Settle();
            Assert.True(host!.IsEditorOpen(device));
            var panel = view.Devices[0];
            Assert.True(panel.IsEditorOpen);
            Assert.True(panel.CanPinEditor);
            Assert.True(panel.CanRouteEditorKeys);

            // Another track: the editor hides (its instance and editor stay); back: it returns.
            app.ViewModel.Select(app.ViewModel.Tracks[1]);
            await AppUnderTest.WaitUntilAsync(() => windows.IsHiddenByFollow(device), "the editor to hide");
            Assert.True(host.IsEditorOpen(device));
            app.ViewModel.Select(app.ViewModel.Tracks[0]);
            await AppUnderTest.WaitUntilAsync(() => !windows.IsHiddenByFollow(device), "the editor to come back");

            // Pinned, it stays; keys go to Bluestone on request.
            windows.SetPinned(device, true);
            Assert.True(await windows.SetKeysToBluestoneAsync(device, true));
            app.Settle();
            Assert.True(view.Devices[0].IsEditorPinned);
            Assert.True(view.Devices[0].EditorKeysToBluestone);
            var docked = app.Find<ContentControl>("DockedDeviceView").GetVisualDescendants().OfType<DeviceViewControl>().Single();
            var toggles = docked.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.IsEffectivelyVisible && t.Classes.Contains("tiny")).ToList();
            Assert.Equal(2, toggles.Count);
            Assert.All(toggles, t => Assert.True(t.IsChecked));
            Assert.True(File.Exists(app.SaveFrame("editor-pinned-keys")));

            app.ViewModel.Select(app.ViewModel.Tracks[1]);
            app.Settle();
            await Task.Delay(300);
            Assert.False(windows.IsHiddenByFollow(device));
        }
        finally
        {
            await host!.CloseEditorAsync(device);
        }
    });
}
