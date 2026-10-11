using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;
using Bluestone.PluginWorker;
using Bluestone.PluginWorker.Plugins;

namespace Bluestone.Tests.Unit.Plugins;

/// <summary>
/// The worker's reference plugins, exercised directly. They are Bluestone's own test plugins, not third-party code;
/// the host never runs them in its process (see the integration tests for the same plugins across a process boundary).
/// </summary>
public sealed class ReferencePluginTests
{
    [Fact]
    public void Gain_AParameterChangeMidBlock_TakesEffectOnExactlyThatSample()
    {
        var gain = new GainPlugin();
        gain.Prepare(48_000, 64, 2, 2);
        var input = Enumerable.Repeat(1.0f, 128).ToArray();
        var output = new float[128];

        gain.Process(Args(64, input, output, changes: [new ParameterChange(GainPlugin.GainParameter, 37, 0.25)]), new OutputEventList(4));

        for (var c = 0; c < 2; c++)
        {
            Assert.All(output[(c * 64)..((c * 64) + 37)], s => Assert.Equal(1.0f, s));
            Assert.All(output[((c * 64) + 37)..((c + 1) * 64)], s => Assert.Equal(0.5f, s));
        }
    }

    [Fact]
    public void Gain_Invert_FlipsPolarity()
    {
        var gain = new GainPlugin();
        gain.Prepare(48_000, 8, 1, 1);
        Assert.True(gain.TrySetParameter(GainPlugin.InvertParameter, 1.0));
        var output = new float[8];

        gain.Process(Args(8, [1, 2, 3, 4, 5, 6, 7, 8], output), new OutputEventList(1));

        Assert.Equal([-1f, -2, -3, -4, -5, -6, -7, -8], output);
    }

    [Fact]
    public void Gain_MismatchedChannels_IsNotSupported() =>
        Assert.Throws<NotSupportedException>(() => new GainPlugin().Prepare(48_000, 64, 1, 2));

    [Fact]
    public void Sine_PlaysAtLeastFourVoices_AndIsDeterministic()
    {
        PluginEvent[] chord = [PluginEvent.NoteOn(0, 0, 60, 127), PluginEvent.NoteOn(0, 0, 64, 127), PluginEvent.NoteOn(0, 0, 67, 127), PluginEvent.NoteOn(0, 0, 71, 127)];
        var first = new SinePlugin();
        var second = new SinePlugin();
        first.Prepare(48_000, 512, 0, 1);
        second.Prepare(48_000, 512, 0, 1);
        var a = new float[512];
        var b = new float[512];

        first.Process(Args(512, [], a, chord), new OutputEventList(1));
        second.Process(Args(512, [], b, chord), new OutputEventList(1));

        Assert.Equal(4, first.ActiveVoices);
        Assert.Equal(a, b);
        Assert.True(SinePlugin.Polyphony >= 4);
    }

    [Fact]
    public void Sine_AttackRisesFromSilence_AndReleaseEndsInExactSilence()
    {
        var sine = new SinePlugin();
        sine.Prepare(48_000, 1024, 0, 1);
        var output = new float[1024];

        sine.Process(Args(1024, [], output, [PluginEvent.NoteOn(0, 0, 69, 127), PluginEvent.NoteOff(512, 0, 69)]), new OutputEventList(1));

        Assert.Equal(0.0f, output[0]);
        Assert.True(output[..SinePlugin.AttackFrames].Max() < output[SinePlugin.AttackFrames..512].Max());
        Assert.Contains(output[512..(512 + SinePlugin.ReleaseFrames)], s => s != 0);
        Assert.All(output[(512 + SinePlugin.ReleaseFrames)..], s => Assert.Equal(0.0f, s));
        Assert.Equal(0, sine.ActiveVoices);
    }

    [Fact]
    public void Transpose_ShiftsNotes_AndPassesControllersAndSystemExclusiveThroughUnchanged()
    {
        var transpose = new TransposePlugin();
        transpose.Prepare(48_000, 64, 0, 0);
        Assert.True(transpose.TrySetParameter(TransposePlugin.SemitonesParameter, TransposePlugin.NormalizedFor(-5)));
        Assert.True(PluginEvent.TryCreateSystemExclusive(2, [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7], out var sysEx));
        var output = new OutputEventList(16);

        transpose.Process(
            Args(64, [], [], [PluginEvent.NoteOn(0, 9, 64, 90), PluginEvent.ControlChange(1, 9, 64, 127), sysEx, PluginEvent.PitchBend(3, 9, 9000), PluginEvent.PolyPressure(4, 9, 64, 30), PluginEvent.NoteOff(5, 9, 64)]),
            output);

        Assert.Equal(
            [PluginEvent.NoteOn(0, 9, 59, 90), PluginEvent.ControlChange(1, 9, 64, 127), sysEx, PluginEvent.PitchBend(3, 9, 9000), PluginEvent.PolyPressure(4, 9, 59, 30), PluginEvent.NoteOff(5, 9, 59)],
            output.Events.ToArray());
    }

    [Fact]
    public void Transpose_ANoteOff_FollowsItsNoteOnsTransposition_AndOutOfRangeNotesAreDropped()
    {
        var transpose = new TransposePlugin();
        transpose.Prepare(48_000, 64, 0, 0);
        transpose.TrySetParameter(TransposePlugin.SemitonesParameter, TransposePlugin.NormalizedFor(12));
        var output = new OutputEventList(16);

        transpose.Process(Args(64, [], [], [PluginEvent.NoteOn(0, 0, 60, 100), PluginEvent.NoteOn(1, 0, 120, 100)]), output);
        transpose.TrySetParameter(TransposePlugin.SemitonesParameter, TransposePlugin.NormalizedFor(0));
        transpose.Process(Args(64, [], [], [PluginEvent.NoteOff(0, 0, 60), PluginEvent.NoteOff(1, 0, 120)]), output);

        Assert.Equal([PluginEvent.NoteOn(0, 0, 72, 100), PluginEvent.NoteOff(0, 0, 72)], output.Events.ToArray());
    }

    [Fact]
    public void State_RoundTripsParameterValues()
    {
        var gain = new GainPlugin();
        gain.TrySetParameter(GainPlugin.GainParameter, 0.125);
        gain.TrySetParameter(GainPlugin.InvertParameter, 1);
        var state = gain.SaveState();
        var other = new GainPlugin();

        other.LoadState(state);

        Assert.Equal(0.125, other.GetParameter(GainPlugin.GainParameter));
        Assert.Equal(1.0, other.GetParameter(GainPlugin.InvertParameter));
        Assert.Equal(ReferencePlugin.StateFormat, state.Format);
    }

    [Theory]
    [InlineData("bluestone.reference-state", new byte[] { 0x43, 0x52, 0x50, 0x53, 0x02, 0x00, 0x00, 0x00 })]
    [InlineData("bluestone.reference-state", new byte[] { 0x43, 0x52, 0x50, 0x53, 0x01, 0x00, 0x01, 0x00, 0x00 })]
    [InlineData("bluestone.reference-state", new byte[] { 1, 2, 3 })]
    [InlineData("another-format", new byte[] { 0x43, 0x52, 0x50, 0x53, 0x01, 0x00, 0x00, 0x00 })]
    public void State_CorruptOrUnknownVersion_FailsCleanly_AndLeavesDefaults(string format, byte[] data)
    {
        var gain = new GainPlugin();
        gain.TrySetParameter(GainPlugin.GainParameter, 0.9);

        Assert.Throws<InvalidDataException>(() => gain.LoadState(new PluginStateData(format, [.. data])));

        Assert.Equal(0.5, gain.GetParameter(GainPlugin.GainParameter));
    }

    [Fact]
    public void State_WithAnOutOfRangeValue_IsRejected()
    {
        var bytes = new GainPlugin().SaveState().Data.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(12), 3.0);

        Assert.Throws<InvalidDataException>(() => new GainPlugin().LoadState(new PluginStateData(ReferencePlugin.StateFormat, [.. bytes])));
    }

    [Fact]
    public void Delay_PutsOutItsInputExactlyItsReportedLatencyLate_AcrossBlocks()
    {
        var delay = new DelayPlugin();
        delay.Prepare(48_000, 64, 1, 1);
        Assert.True(delay.TrySetParameter(DelayPlugin.LatencyParameter, 100.0 / DelayPlugin.MaxLatency));
        Assert.Equal(100, delay.LatencyFrames);
        var input = new float[64];
        input[5] = 1;
        var heard = new List<float>();
        for (var block = 0; block < 4; block++)
        {
            var output = new float[64];
            delay.Process(Args(64, block == 0 ? input : new float[64], output), new OutputEventList(1));
            heard.AddRange(output);
        }

        Assert.Equal([105], Enumerable.Range(0, heard.Count).Where(i => heard[i] != 0));
    }

    [Fact]
    public void Catalog_KnowsTheReferencePlugins_AndNothingElse()
    {
        Assert.Equal(["reference.gain", "reference.sine", "reference.transpose", "reference.delay"], ReferencePluginCatalog.Identities.Select(i => i.PluginId));
        Assert.IsType<SinePlugin>(ReferencePluginCatalog.Create(ReferencePluginCatalog.Identities[1]));
        Assert.Null(ReferencePluginCatalog.Create(new PluginIdentity("another-format", "m", "reference.gain", "x", "y", PluginKind.AudioEffect, "1")));
    }

    private static ProcessArgs Args(int frames, float[] input, float[] output, PluginEvent[]? events = null, ParameterChange[]? changes = null) =>
        new(frames, input, output, events ?? [], changes ?? [], new TransportState(0, 120, true));
}

public sealed class ModuleScannerTests : IDisposable
{
    private readonly TestDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void AManifest_ListsItsPlugins()
    {
        var path = Write("ok.bluestone-reference-plugin", """
            {"format": "bluestone-reference-plugin", "version": 1, "moduleId": "acme",
             "plugins": [{"pluginId": "acme.verb", "name": "Verb", "vendor": "Acme", "kind": "AudioEffect", "version": "2.1"}]}
            """);

        var result = ModuleScanner.Scan(path);

        Assert.Null(result.Failure);
        var plugin = Assert.Single(result.Plugins);
        Assert.Equal(new PluginIdentity("bluestone-reference", "acme", "acme.verb", "Verb", "Acme", PluginKind.AudioEffect, "2.1"), plugin);
    }

    [Theory]
    [InlineData("library.dylib", "{}")]
    [InlineData("bad.bluestone-reference-plugin", "{ not json")]
    [InlineData("bad.bluestone-reference-plugin", "{\"format\": \"other\", \"version\": 1, \"moduleId\": \"m\", \"plugins\": []}")]
    [InlineData("bad.bluestone-reference-plugin", "{\"format\": \"bluestone-reference-plugin\", \"version\": 1, \"moduleId\": \"m\", \"plugins\": [{\"pluginId\": \"p\", \"name\": \"n\", \"kind\": \"Synth\"}]}")]
    [InlineData("bad.bluestone-reference-plugin", "{\"format\": \"bluestone-reference-plugin\", \"version\": 1, \"moduleId\": \"m\"}")]
    public void AnythingElse_IsMalformed(string name, string content)
    {
        var result = ModuleScanner.Scan(Write(name, content));

        Assert.Equal(ScanFailureKind.Malformed, result.Failure);
        Assert.Empty(result.Plugins);
    }

    [Fact]
    public void AMissingFile_IsMalformed() =>
        Assert.Equal(ScanFailureKind.Malformed, ModuleScanner.Scan(_directory.File("gone.bluestone-reference-plugin")).Failure);

    private string Write(string name, string content)
    {
        var path = _directory.File(name);
        File.WriteAllText(path, content);
        return path;
    }
}
