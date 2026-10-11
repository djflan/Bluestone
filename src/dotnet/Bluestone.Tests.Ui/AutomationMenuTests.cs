using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Bluestone.Application.Editing;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Profiles;

namespace Bluestone.Tests.Ui;

/// <summary>The track's "Add automation lane" menu offers the parameters its instrument's profile maps, by name (ADR 0031).</summary>
public sealed class AutomationMenuTests
{
    private static readonly ProfileCatalog Profiles = ProfileCatalog.Empty.Add("hw", ProfileLoader.Load(Encoding.UTF8.GetBytes("""
        { "format": "bluestone-device-profile", "schemaVersion": 1, "id": "test.hw", "version": "1", "name": "Test Hardware",
          "provenance": { "sources": ["t"], "contributors": ["t"], "license": "MIT", "redistributionConfirmed": true, "verification": "unverified" },
          "parameterMap": [
            { "id": "cutoff", "name": "Filter Cutoff", "kind": "nrpn", "msb": 1, "lsb": 32 },
            { "id": "part-volume", "name": "Part Volume", "kind": "sysex", "bytes": "F0 43 {device} 4C 08 {channel} 0B {value} F7", "device": 16 }
          ] }
        """)));

    [Fact]
    public Task TheAddLaneMenu_OffersInstrumentParametersByName_AndAddsALane() => HeadlessApp.RunAsync(async () =>
    {
        var session = new ProjectSession();
        var lead = Track.FromEvents(TrackId.New(), "Lead", [new NoteEvent(Tick.Zero, new TickSpan(240), MidiChannel.FromNumber(2), new NoteNumber(60), new Velocity(100))]);
        session.Execute(ProjectCommands.AddTrack(lead));
        await using var app = await AppUnderTest.OpenAsync(session);
        app.Playback.UseProfiles(Profiles);
        var synth = ExternalInstrument.Create("Synth", new EndpointReference(LoopbackMidiProvider.ProviderId, app.Port.OutputId.Value, "Synth"), new ProfileReference("test.hw"));
        session.Execute(new ProjectCommand("Route to the synth", p => p with
        {
            Instruments = [synth],
            Connections = [SignalConnection.Create(SignalKind.Events, SignalNode.Track(lead.Id), SignalNode.ExternalPart(synth.Id, synth.Ports[0].Id))],
        }));
        await AppUnderTest.WaitUntilAsync(() => app.ViewModel.Tracks[0].HasHardwareParameters, "the track to offer its instrument's parameters");

        var add = AppUnderTest.Within<Button>(app.Window, b => AutomationProperties.GetName(b) == "Add automation lane");
        app.Click(add);
        var menu = Assert.IsType<MenuFlyout>(add.Flyout);
        var parameters = menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Instrument parameters"));
        Assert.True(parameters.IsVisible);
        parameters.IsSubMenuOpen = true;
        app.Settle();
        var items = Enumerable.Range(0, parameters.ItemCount).Select(i => Assert.IsType<MenuItem>(parameters.ContainerFromIndex(i))).ToList();

        Assert.Equal(["Filter Cutoff", "Part Volume"], items.Select(i => i.Header?.ToString()));
        items[0].Command!.Execute(items[0].CommandParameter);
        app.Settle();

        var lane = Assert.Single(session.Project.Sequence.Tracks[0].Automation);
        Assert.Equal(AutomationTarget.ForInstrument(synth.Id, "A", MidiChannel.FromNumber(2), "cutoff"), lane.Target);
        Assert.Equal("Filter Cutoff · ch 2", Assert.Single(app.ViewModel.Tracks[0].Lanes).Name);
        menu.Hide();
    });
}
