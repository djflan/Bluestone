using Avalonia.Automation;
using Avalonia.Controls;
using Bluestone.Application.Editing;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;
using Bluestone.Signal;

namespace Bluestone.Tests.Ui;

/// <summary>
/// The inspector's Devices, Racks, and Mixer sections in the real window on Avalonia's headless platform: the views
/// load, their compiled bindings work, and mouse clicks and slider moves reach the project through the view models.
/// </summary>
public sealed class InspectorTests
{
    private static ProjectSession OneTrack()
    {
        var session = new ProjectSession();
        session.Execute(ProjectCommands.AddTrack(Track.FromEvents(TrackId.New(), "Lead", [new NoteEvent(Tick.Zero, new TickSpan(240), MidiChannel.FromNumber(1), new NoteNumber(60), new Velocity(100))])));
        return session;
    }

    private static string? Header(Expander section) => section.Header?.ToString();

    [Fact]
    public Task TheWindow_Loads_WithTheInspectorsSections_AndRendersAFrame() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(OneTrack());
        app.ViewModel.Select(app.ViewModel.Tracks[0]);
        app.Settle();

        Assert.Equal("Devices", Header(app.Find<Expander>("DevicesSection")));
        Assert.True(app.Find<Expander>("DevicesSection").IsEffectivelyVisible);
        Assert.Equal("Racks", Header(app.Find<Expander>("RacksSection")));
        Assert.Equal("Mixer", Header(app.Find<Expander>("MixerSection")));
        Assert.True(File.Exists(app.SaveFrame("inspector")));
    });

    [Fact]
    public Task NewRack_SwitchesTheDevicesSectionToTheRack_AndBackToTheTrack_ByMouse() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(OneTrack());
        app.ViewModel.Select(app.ViewModel.Tracks[0]);
        var racks = app.Find<Expander>("RacksSection");
        var devices = app.Find<Expander>("DevicesSection");
        racks.IsExpanded = true;
        app.Settle();

        app.Click(AppUnderTest.Within<Button>(racks, b => Equals(b.Content, "New rack")));

        Assert.Equal("Rack: Rack 1", Header(devices));
        var back = AppUnderTest.Within<Button>(devices, b => b.Content is string text && text.Contains("Back to the selected track", StringComparison.Ordinal));
        Assert.True(back.IsEffectivelyVisible);
        Assert.Single(app.Session.Project.Chains, c => c.Owner.Kind == ChainOwnerKind.Rack);
        app.SaveFrame("rack-in-devices");

        app.Click(back);

        Assert.Equal("Devices", Header(devices));
        Assert.False(back.IsEffectivelyVisible);
    });

    [Fact]
    public Task AMixerFader_MovedSeveralTimes_IsOneUndoStep_AndShowsItsValue() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(OneTrack());
        var mixer = app.Find<Expander>("MixerSection");
        mixer.IsExpanded = true;
        app.Settle();
        app.Click(AppUnderTest.Within<Button>(mixer, b => Equals(b.Content, "New mixer channel")));
        var fader = AppUnderTest.Within<Slider>(mixer, s => AutomationProperties.GetName(s) == "Channel gain");

        foreach (var value in new[] { -2.0, -4.0, -6.0 })
        {
            fader.Value = value;
            app.Settle();
        }

        Assert.Equal(-6, app.Session.Project.Mixer.Channels[0].GainDecibels);
        Assert.NotNull(AppUnderTest.Within<TextBlock>(mixer, t => t.Text == "-6.0 dB"));
        app.SaveFrame("mixer-channel");

        app.ViewModel.UndoCommand.Execute(null);
        app.Settle();

        Assert.Equal(0, app.Session.Project.Mixer.Channels[0].GainDecibels);
        Assert.Equal(0, fader.Value);
    });

    [Fact]
    public Task APluginMidiEffect_ShapesThePlan_AndTheAppRecompilesWhenItsWorkerDiesAndIsRestarted() => HeadlessApp.RunAsync(async () =>
    {
        var transpose = new PluginIdentity("bluestone-reference", "bluestone.reference", "reference.transpose", "Reference Transpose", "Bluestone", PluginKind.MidiEffect, "1.0.0");
        var directory = Path.Combine(AppContext.BaseDirectory, "plugin-data", Guid.NewGuid().ToString("N"));
        await using var manager = new PluginHostManager(new PluginHostOptions { DataDirectory = directory, RequestTimeout = TimeSpan.FromSeconds(15) });
        var session = OneTrack();
        PluginDeviceHost? bridge = null;
        await using var app = await AppUnderTest.OpenAsync(session, s => bridge = new PluginDeviceHost(manager, s, [transpose]));
        var lead = session.Project.Sequence.Tracks[0];
        var device = DeviceInstance.Create(PluginDeviceHost.DefinitionOf(transpose).ToReference()).WithParameter(new ParameterId(0), ControlValue.FromFraction((7 + 24) / 48.0));
        var synth = ExternalInstrument.Create("Synth", new EndpointReference(LoopbackMidiProvider.ProviderId, app.Port.OutputId.Value, "Synth"));
        session.Execute(new ProjectCommand("Route through the plugin", p => (p with
        {
            Instruments = [synth],
            Connections = [SignalConnection.Create(SignalKind.Events, SignalNode.Track(lead.Id), SignalNode.ExternalPart(synth.Id, synth.Ports[0].Id))],
        }).WithChain(DeviceChain.Create(ChainOwner.ForTrack(lead.Id)) with { Devices = [device] })));

        int? Played() => app.Playback.Routing?.Graph.ExternalParts.SingleOrDefault()?.Events.Select(e => e.Event).OfType<NoteEvent>().SingleOrDefault()?.Note.Value;
        await AppUnderTest.WaitUntilAsync(() => Played() == 67, "the plan to hold the transposed note");

        app.ViewModel.Select(app.ViewModel.Tracks[0]);
        app.Settle();
        var strip = app.ViewModel.DeviceStrip.Devices.Single();
        await AppUnderTest.WaitUntilAsync(() => strip.Status == "Running", "the plugin to run");
        using (var worker = System.Diagnostics.Process.GetProcessById(bridge!.InstanceOf(device.Id)!.WorkerProcessId!.Value))
        {
            worker.Kill();
        }

        await AppUnderTest.WaitUntilAsync(() => Played() == 60, "the plan to fall back to the untransposed note");
        Assert.Contains(app.Playback.Routing!.Graph.Diagnostics, d => d.Device == device.Id && d.Code == SignalDiagnosticCode.NotProcessedHere);
        Assert.Equal("Crashed", strip.Status);

        await strip.RestartCommand.ExecuteAsync(null);
        await AppUnderTest.WaitUntilAsync(() => Played() == 67, "the plan to hold the transposed note again");
    });
}
