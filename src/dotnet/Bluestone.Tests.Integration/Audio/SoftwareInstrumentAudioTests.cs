using Bluestone.Application.Audio;
using Bluestone.Application.Editing;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Audio;
using Bluestone.Audio.Plugins;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Signal;
using Bluestone.Tests.Integration.Plugins;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Audio;

/// <summary>
/// A software-instrument track through the whole path: its notes become the instrument's feed (signal graph), the
/// plugin device runs in a real worker (plugin bridge), and the track-owned strip's Output reaches the Master
/// (project audio graph), rendered by the offline driver.
/// </summary>
[Collection(PluginProcessTests.Name)]
public sealed class SoftwareInstrumentAudioTests
{
    private const int Rate = 48_000;
    private const int Block = 256;

    [Fact]
    public async Task AnInstrumentTrack_WithTheReferenceSine_IsHeardThroughItsTrackStrip_WhileItsNotesPlay()
    {
        await using var host = new PluginTestHost();
        var (session, device, track) = InstrumentProject();
        await using var bridge = new PluginDeviceHost(host.Manager, session, [Sine], Rate, Block);
        await bridge.SyncAsync(session.Project, Ct);
        host.Track(bridge.InstanceOf(device.Id)!);
        var devices = bridge.Extend(DeviceCatalog.BuiltIn);
        var project = session.Project;
        var signal = SignalGraph.Evaluate(project, devices);
        Assert.Single(signal.SoftwareInstruments);

        var graphs = new ProjectAudioGraph(Rate, Block, bridge.InstanceOf);
        var result = graphs.Build(project, signal, devices, null);
        Assert.Empty(result.Problems);
        Assert.Equal(Block, result.LatencyFrames[device.Id]);
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(result.Graph);
        engine.Transport.Play(0);
        var output = new OfflineAudioDriver(engine).Render(Rate * 2, Block);
        var left = output.Where((_, i) => i % 2 == 0).ToArray();

        // The note plays from 0.5 s to 1 s (ticks 480 to 960 at 960 ticks a second), one block late.
        Assert.All(left.AsSpan(..(Rate / 2)).ToArray(), s => Assert.Equal(0f, s));
        Assert.True(left.Take((Rate / 2 + Block + 100)..(Rate - 100)).Max() > 0.2f);
        Assert.All(left.AsSpan((Rate + Block + 2048)..).ToArray(), s => Assert.Equal(0f, s));
        Assert.True(graphs.TrackStripOf(track)!.Meter.Read().PeakLeft > 0f);
    }

    [Fact]
    public async Task ATrackStripEdit_WhileANoteIsHeld_KeepsTheSchedule_AndTheNoteSounding()
    {
        await using var host = new PluginTestHost();
        var (session, device, track) = InstrumentProject(noteTicks: 1920);
        await using var bridge = new PluginDeviceHost(host.Manager, session, [Sine], Rate, Block);
        await bridge.SyncAsync(session.Project, Ct);
        host.Track(bridge.InstanceOf(device.Id)!);
        var devices = bridge.Extend(DeviceCatalog.BuiltIn);
        using var graphs = new ProjectAudioGraph(Rate, Block, bridge.InstanceOf);
        var project = session.Project;
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(graphs.Build(project, SignalGraph.Evaluate(project, devices), devices, null).Graph);
        engine.Transport.Play(0);
        var driver = new OfflineAudioDriver(engine);
        driver.Render(Rate, Block);
        var schedule = graphs.PluginNodeOf(device.Id)!.Schedule;

        // The note sounds from 0.5 s to 2.5 s; a fader move at 1 s rebuilds the graph.
        var source = project.Sequence.FindTrack(track)!;
        var sourceStrip = source.AudioStrip ?? AudioStrip.Default;
        var quieterTrack = source.WithAudioStrip(sourceStrip with { GainDecibels = -1 });
        var quieter = project with { Sequence = project.Sequence.WithTrack(quieterTrack) };
        engine.Publish(graphs.Build(quieter, SignalGraph.Evaluate(quieter, devices), devices, null).Graph);
        var after = driver.Render(Rate / 2, Block).Where((_, i) => i % 2 == 0).ToArray();

        Assert.All(after.Chunk(Block), block => Assert.True(block.Max() > 0.2f));
        Assert.Same(schedule, graphs.PluginNodeOf(device.Id)!.Schedule);
    }

    [Fact]
    public async Task APluginRunningAtAnotherRate_IsReported_AndLeftSilent()
    {
        await using var host = new PluginTestHost();
        var (session, device, _) = InstrumentProject();
        await using var bridge = new PluginDeviceHost(host.Manager, session, [Sine], 44_100, Block);
        await bridge.SyncAsync(session.Project, Ct);
        host.Track(bridge.InstanceOf(device.Id)!);
        var devices = bridge.Extend(DeviceCatalog.BuiltIn);

        var result = new ProjectAudioGraph(Rate, Block, bridge.InstanceOf).Build(session.Project, SignalGraph.Evaluate(session.Project, devices), devices, null);

        Assert.Contains(result.Problems, p => p.Contains("44100 Hz", StringComparison.Ordinal));
        Assert.Empty(result.Graph.Nodes.OfType<PluginNode>());
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(result.Graph);
        var output = new OfflineAudioDriver(engine).Render(Rate / 10, Block);
        Assert.All(output, sample => Assert.Equal(0f, sample));
    }

    private static (ProjectSession Session, DeviceInstance Device, TrackId Track) InstrumentProject(long noteTicks = 480)
    {
        var session = new ProjectSession();
        var note = new NoteEvent(new Tick(480), new TickSpan(noteTicks), MidiChannel.FromIndex(0), new NoteNumber(69), new Velocity(127));
        var track = Track.FromEvents(TrackId.New(), "Lead", [note]).WithAudioStrip(AudioStrip.Default);
        var device = DeviceInstance.Create(PluginDeviceHost.DefinitionOf(Sine).ToReference());
        var project = session.Project with
        {
            Sequence = new Sequence(TempoMap.Constant(new Ppqn(480), Tempo.Default), MeterMap.Constant(new Ppqn(480), TimeSignature.CommonTime), [track], []),
            Connections = [SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Master)],
        };
        project = project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(track.Id)) with { Devices = [device] });
        session.Execute(new ProjectCommand("Set up", _ => project));
        return (session, device, track.Id);
    }
}
