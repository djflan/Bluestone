using Bluestone.Application.Editing;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Profiles;

namespace Bluestone.Tests.Integration.Sessions;

/// <summary>Quitting must be clean whether or not playback is running on the real playback thread.</summary>
public sealed class PlaybackControllerShutdownTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_WithRealThread_DoesNotThrowAndReleasesSoundingNotes(bool playing)
    {
        var clock = SystemMonotonicClock.Instance;
        using var provider = new LoopbackMidiProvider(clock);
        var port = provider.CreatePort("Synth", "s");
        using var directory = new EndpointDirectory([provider]);
        var session = new ProjectSession();
        var track = Track.FromEvents(TrackId.New(), "t", [new NoteEvent(Tick.Zero, new TickSpan(100_000), MidiChannel.FromIndex(0), NoteNumber.MiddleC, Velocity.Max)]);
        session.Execute(ProjectCommands.AddTrack(track));
        session.Execute(ProjectCommands.SetTrackOutput(track.Id, new TrackOutput { Endpoint = new EndpointReference(LoopbackMidiProvider.ProviderId, port.OutputId.Value) }));

        for (var i = 0; i < 20; i++)
        {
            var controller = new PlaybackController(session, directory, ProfileCatalog.Empty, clock);
            await controller.RefreshAsync(Ct);
            if (playing)
            {
                await controller.PlayAsync(Tick.Zero, Ct);
                await Task.Delay(30, Ct);
            }

            port.ClearSent();
            await controller.DisposeAsync();

            if (playing)
            {
                // The long note was sounding when the app quit; it must have been released.
                Assert.Contains(port.Sent, m => m.Bytes[0] == 0x80);
            }
        }
    }
}
