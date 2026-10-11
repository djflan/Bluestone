using Bluestone.Audio;
using Bluestone.Audio.Plugins;
using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Tests.Unit.Audio;

public sealed class LiveSourceSetTests
{
    private static readonly AudioRenderContext Context = new(64, 48_000, false, 0, false);

    [Fact]
    public void EverySource_IsRendered_AndARemovedOne_ReleasesAtTheNextBlock()
    {
        var set = new LiveSourceSet();
        var a = new FakeSource(60);
        var b = new FakeSource(64);
        set.Add(a);
        set.Add(b);
        set.Add(a);
        var events = new PluginEvent[16];

        Assert.Equal(2, set.Render(Context, events));
        Assert.Equal([60, 64], events[..2].Select(e => (int)e.Data1));

        set.Remove(a);
        var count = set.Render(Context, events);

        Assert.Equal(1, set.Count);
        Assert.Equal([(PluginEventKind.NoteOff, 60), (PluginEventKind.NoteOn, 64)], events[..count].Select(e => (e.Kind, (int)e.Data1)));
        Assert.Equal(1, a.Releases);
        Assert.Equal(0, b.Releases);
    }

    [Fact]
    public void Release_ReleasesEverySourceItRendered()
    {
        var set = new LiveSourceSet();
        var a = new FakeSource(60);
        var b = new FakeSource(64);
        set.Add(a);
        set.Add(b);
        var events = new PluginEvent[16];
        set.Render(Context, events);

        Assert.Equal(2, set.Release(events));
        Assert.Equal((1, 1), (a.Releases, b.Releases));
    }

    [Fact]
    public void Rendering_AllocatesNothing()
    {
        var set = new LiveSourceSet();
        set.Add(new FakeSource(60));
        set.Add(new FakeSource(64));
        var events = new PluginEvent[16];
        set.Render(Context, events);

        Assert.Equal(0, Allocations.InSteadyState(() => set.Render(Context, events), 100));
    }

    // Puts out a note-on for its note every block; a release is one note-off.
    private sealed class FakeSource(int note) : ILiveEventSource
    {
        public int Releases { get; private set; }

        public int Render(in AudioRenderContext context, Span<PluginEvent> events)
        {
            events[0] = PluginEvent.NoteOn(0, 0, note, 100);
            return 1;
        }

        public int Release(Span<PluginEvent> events)
        {
            Releases++;
            events[0] = PluginEvent.NoteOff(0, 0, note);
            return 1;
        }
    }
}
