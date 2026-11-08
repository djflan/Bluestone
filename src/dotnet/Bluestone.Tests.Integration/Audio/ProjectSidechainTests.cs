using System.Collections.Immutable;
using Bluestone.Application.Audio;
using Bluestone.Application.Editing;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Audio;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Plugins.Protocol;
using Bluestone.Signal;
using Bluestone.Tests.Integration.Plugins;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Audio;

/// <summary>
/// A plugin effect's sidechain routed in a project (ADR 0037), rendered through the project audio graph with Bluestone's
/// native Test Key plugin, which at its default mix puts out exactly its sidechain and not its main input. The level and
/// timing of what comes out show which junction the routing resolved, and that the main input stayed apart.
/// </summary>
[Collection(PluginProcessTests.Name)]
public sealed class ProjectSidechainTests
{
    private const int Rate = 48_000;
    private const int Block = 256;

    public enum KeySource
    {
        EarlierInTheSameChain,
        AnotherTracksTap,
        AnotherTracksOutput,
    }

    public static TheoryData<string, KeySource> Cases()
    {
        var cases = new TheoryData<string, KeySource>();
        foreach (var format in new[] { "vst3", "clap" })
        {
            foreach (var source in Enum.GetValues<KeySource>())
            {
                cases.Add(format, source);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ASidechain_IsTakenFromTheRoutedJunction_AndKeptApartFromTheMainInput(string format, KeySource source)
    {
        await using var host = new PluginTestHost();
        var key = (await NativePluginTests.ScanAsync(host, format))["Test Key"];

        // Lead plays 0.5 to 1 s through [Sine, Gain at half, Test Key]; Other plays 1.5 to 2 s through [Sine, Gain at half].
        var session = new ProjectSession();
        var lead = Track.FromEvents(TrackId.New(), "Lead", [Note(480)]);
        var other = Track.FromEvents(TrackId.New(), "Other", [Note(1440)]);
        var leadSine = Plugin(Sine);
        var leadGain = HalfGain();
        var keyed = Plugin(key);
        var otherSine = Plugin(Sine);
        var otherGain = HalfGain();
        var sidechain = source switch
        {
            KeySource.EarlierInTheSameChain => SignalNode.Device(leadSine.Id),
            KeySource.AnotherTracksTap => SignalNode.Device(otherSine.Id),
            _ => SignalNode.Track(other.Id),
        };
        var project = session.Project with
        {
            Sequence = new Sequence(TempoMap.Constant(new Ppqn(480), Tempo.Default), MeterMap.Constant(new Ppqn(480), TimeSignature.CommonTime), [lead, other], []),
            Connections =
            [
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(lead.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Audio, sidechain, SignalNode.DeviceInput(keyed.Id, DeviceInputPort.Sidechain)),
            ],
        };
        project = project
            .WithChain(DeviceChain.Create(ChainOwner.ForTrack(lead.Id)) with { Devices = [leadSine, leadGain, keyed] })
            .WithChain(DeviceChain.Create(ChainOwner.ForTrack(other.Id)) with { Devices = [otherSine, otherGain] });
        session.Execute(new ProjectCommand("Set up", _ => project));

        await using var bridge = new PluginDeviceHost(host.Manager, session, [Sine, Gain, key], Rate, Block);
        bridge.SetAvailable([Sine, Gain, key], new Dictionary<PluginIdentity, string> { [key] = NativePluginTests.ModulePath(format) }.ToImmutableDictionary());
        await bridge.SyncAsync(session.Project, Ct);
        foreach (var device in new[] { leadSine, leadGain, keyed, otherSine, otherGain })
        {
            host.Track(bridge.InstanceOf(device.Id)!);
        }

        Assert.True(bridge.InstanceOf(keyed.Id)!.PluginSidechainChannels > 0, "Test Key has a sidechain bus");
        var devices = bridge.Extend(DeviceCatalog.BuiltIn);
        using var graphs = new ProjectAudioGraph(Rate, Block, bridge.InstanceOf);
        var result = graphs.Build(session.Project, SignalGraph.Evaluate(session.Project, devices), devices, null);
        Assert.Empty(result.Problems);
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(result.Graph);
        engine.Transport.Play(0);
        var left = new OfflineAudioDriver(engine).Render(Rate * 5 / 2, Block).Where((_, i) => i % 2 == 0).ToArray();

        // Lead's own note (its main input) is never heard; only what the sidechain brings is, when and as loud as it was there.
        var leadWindow = Peak(left, 0.6, 0.95);
        var otherWindow = Peak(left, 1.6, 1.95);
        // The reference sine peaks at 0.25 (its default level, full velocity); after a gain at half, at 0.125.
        var (sounding, silent, expected) = source switch
        {
            KeySource.EarlierInTheSameChain => (leadWindow, otherWindow, 0.25),
            KeySource.AnotherTracksTap => (otherWindow, leadWindow, 0.25),
            _ => (otherWindow, leadWindow, 0.125),
        };
        Assert.True(silent < 0.001f, $"Nothing should be heard outside the key's note, but the peak was {silent}.");
        Assert.InRange(sounding, expected - 0.02, expected + 0.02);
    }

    private static float Peak(float[] samples, double from, double to) => Window(samples, from, to).Select(Math.Abs).DefaultIfEmpty(0).Max();

    private static float[] Window(float[] samples, double from, double to) => samples[(int)(from * Rate)..Math.Min(samples.Length, (int)(to * Rate))];

    private static NoteEvent Note(long tick) => new(new Tick(tick), new TickSpan(480), MidiChannel.FromIndex(0), new NoteNumber(69), new Velocity(127));

    private static DeviceInstance Plugin(PluginIdentity plugin) => DeviceInstance.Create(PluginDeviceHost.DefinitionOf(plugin).ToReference());

    private static DeviceInstance HalfGain() => Plugin(Gain).WithParameter(new ParameterId(0), ControlValue.FromFraction(0.25));
}
