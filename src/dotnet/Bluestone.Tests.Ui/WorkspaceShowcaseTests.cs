using Avalonia.Controls;
using Bluestone.Application.Editing;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;

namespace Bluestone.Tests.Ui;

/// <summary>
/// Representative scenes of the whole workspace, rendered at narrow, laptop, normal, and wide window sizes and saved as frames
/// (<c>frames/showcase-*.png</c> in the test output) so a change to the design can be compared with what came before. Each scene
/// also checks the few things that must hold at every size.
/// </summary>
public sealed class WorkspaceShowcaseTests
{
    private const int Beat = 480;

    private static readonly PluginIdentity Sine = new("bluestone-reference", "bluestone.reference", "reference.sine", "Reference Sine", "Bluestone", PluginKind.Instrument, "1.0.0");

    private static readonly PluginIdentity Gain = new("bluestone-reference", "bluestone.reference", "reference.gain", "Reference Gain", "Bluestone", PluginKind.AudioEffect, "1.0.0");

    private static DeviceDefinition BuiltIn(string name) => Signal.DeviceCatalog.BuiltIn.Definitions.Single(d => d.Name == name);

    private static ProjectSession Song()
    {
        var session = new ProjectSession();
        NoteEvent Note(int beat, int note, int length = Beat) =>
            new(new Tick(beat * Beat), new TickSpan(length), MidiChannel.FromNumber(1), new NoteNumber((byte)note), new Velocity(100));
        session.Execute(ProjectCommands.AddTrack(Track.FromEvents(TrackId.New(), "Lead", [.. Enumerable.Range(0, 16).Select(i => Note(i, 60 + ((i * 5) % 12)))])));
        session.Execute(ProjectCommands.AddTrack(Track.FromEvents(TrackId.New(), "Bass", [.. Enumerable.Range(0, 8).Select(i => Note(i * 2, 36 + (i % 3 * 5), Beat * 2))])));
        session.Execute(ProjectCommands.AddTrack(Track.Create("Pad", TrackRole.Hybrid)));
        return session;
    }

    private static PluginHostManager Plugins()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "plugin-data", Guid.NewGuid().ToString("N"));
        return new PluginHostManager(new PluginHostOptions { DataDirectory = directory, RequestTimeout = TimeSpan.FromSeconds(15) });
    }

    [Theory]
    [InlineData(1100, 720, "narrow")]
    [InlineData(1280, 800, "laptop")]
    [InlineData(1440, 900, "normal")]
    [InlineData(1920, 1080, "wide")]
    public Task TheWorkspace_WithAPopulatedChain(double width, double height, string size) => HeadlessApp.RunAsync(async () =>
    {
        await using var plugins = Plugins();
        await using var app = await AppUnderTest.OpenAsync(Song(), s => new PluginDeviceHost(plugins, s, [Sine, Gain]));
        app.Window.Width = width;
        app.Window.Height = height;
        app.ViewModel.Select(app.ViewModel.Tracks[0]);
        app.ViewModel.Workspace.Browser.ShowCommand.Execute(null);
        app.Settle();
        var view = app.ViewModel.DeviceView!;
        view.InsertAt(0, BuiltIn("Arpeggiator"));
        view.InsertAt(1, PluginDeviceHost.DefinitionOf(Sine));
        view.InsertAt(2, BuiltIn("Transpose"));
        app.Settle();
        await AppUnderTest.WaitUntilAsync(() => view.Devices.Count == 3, "the chain");
        view.SelectedDevice = view.Devices[1];
        app.Settle();

        Assert.True(File.Exists(app.SaveFrame($"showcase-{size}-chain")));
        Assert.True(app.Find<ContentControl>("DockedDeviceView").IsEffectivelyVisible);
    });

    [Theory]
    [InlineData(1280, 800, "laptop")]
    [InlineData(1440, 900, "normal")]
    public Task TheWorkspace_AfterAddingANewTrack(double width, double height, string size) => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(Song());
        app.Window.Width = width;
        app.Window.Height = height;
        app.ViewModel.Workspace.Browser.ShowCommand.Execute(null);
        app.ViewModel.AddTrackCommand.Execute(null);
        app.Settle();

        Assert.True(File.Exists(app.SaveFrame($"showcase-{size}-new-track")));
    });

    [Fact]
    public Task TheWorkspace_WithSeveralTracksSelected() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(Song());
        app.Window.Width = 1440;
        app.Window.Height = 900;
        app.ViewModel.Workspace.Browser.ShowCommand.Execute(null);
        app.ViewModel.Select(app.ViewModel.Tracks[0]);
        app.ViewModel.SelectAdjacent(1, extend: true);
        app.Settle();

        Assert.True(File.Exists(app.SaveFrame("showcase-normal-multiple")));
    });
}
