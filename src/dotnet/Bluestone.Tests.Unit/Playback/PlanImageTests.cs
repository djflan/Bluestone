using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Files;
using Bluestone.Playback;
using Bluestone.Signal;
using CsCheck;
using static Bluestone.Tests.Unit.Playback.PlanDump;
using static Bluestone.Tests.Unit.Playback.PlaybackFixture;

namespace Bluestone.Tests.Unit.Playback;

/// <summary>
/// A plan written as a flat image and read back (ADR 0044) must play exactly like the plan it came from, view the
/// image instead of copying it, and refuse damaged images.
/// </summary>
public sealed class PlanImageTests
{
    [Theory]
    [MemberData(nameof(MidiOneOutputGoldenTests.Samples), MemberType = typeof(MidiOneOutputGoldenTests))]
    public void ASampleReadFromItsImage_SendsTheGoldenBytes(string sample, string playback, string exported)
    {
        _ = exported;
        var file = SmfReader.Read(File.ReadAllBytes(Path.Combine(SamplesDirectory, sample))).File;
        var plan = Compile(Routed(SmfImporter.Import(file).Sequence));

        var read = PlanImage.Read(PlanImage.ToArray(plan));

        Assert.Equal(playback, Hash(PlaybackStream(read)));
        Assert.Equal(plan.EventCount, read.EventCount);
        Assert.Equal(plan.TempoMap.Changes, read.TempoMap.Changes);
        Assert.Equal(plan.MeterMap.Changes, read.MeterMap.Changes);
    }

    [Fact]
    public void APlanReadFromItsImage_PlaysTheSame_FromMidSong_AndAcrossALoopWrap()
    {
        var plan = RichPlan();
        var read = PlanImage.Read(PlanImage.ToArray(plan));

        var original = Play(plan);
        var fromImage = Play(read);

        Assert.Equal(original, fromImage);
        Assert.Contains(original, m => m.StartsWith("F043", StringComparison.Ordinal));
        Assert.Contains(original, m => m.StartsWith("C0", StringComparison.Ordinal));
        Assert.True(original.Count > 20, $"Only {original.Count} messages were sent.");
    }

    [Fact]
    public void ReadingAPlan_ViewsTheImage_AndAllocatesNothingForItsEvents()
    {
        var sequence = Sequence.CreateEmpty(Resolution);
        for (var t = 0; t < 16; t++)
        {
            sequence = sequence.WithTrack(Track.FromEvents(TrackId.New(), $"T{t}", Enumerable.Range(0, 10_000).Select(i => (TrackEvent)Note(i * 120L, 100, 36 + (i % 48)))));
        }

        var plan = Compile(Routed(sequence));
        var image = PlanImage.ToArray(plan);
        _ = PlanImage.Read(image); // warm-up

        var before = GC.GetAllocatedBytesForCurrentThread();
        var read = PlanImage.Read(image);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(160_000, read.EventCount);
        Assert.True(allocated < 16 * 1024, $"Reading a {image.Length / 1024} KB image allocated {allocated} bytes.");
        ref readonly var firstEvent = ref MemoryMarshal.GetReference(read.Events);
        Assert.True(Unsafe.AreSame(ref Unsafe.As<PlanEvent, byte>(ref Unsafe.AsRef(in firstEvent)), ref image[EventOffset(image)]));
    }

    [Fact]
    public void AnEmptyPlan_RoundTrips()
    {
        var read = PlanImage.Read(PlanImage.ToArray(PlaybackPlan.Empty(TempoMap.Constant(Resolution, Tempo.Default))));

        Assert.Equal(0, read.EventCount);
        Assert.Equal(0, read.PayloadCount);
        Assert.Empty(read.ParameterChases);
    }

    [Fact]
    public void Write_RefusesADestinationThatIsTooSmall()
    {
        var plan = RichPlan();
        var image = new byte[PlanImage.Measure(plan) - 1];

        Assert.Throws<ArgumentException>(() => PlanImage.Write(plan, image));
    }

    [Fact]
    public void DamagedOrForeignImages_AreRefused()
    {
        var image = PlanImage.ToArray(RichPlan());

        Assert.Throws<InvalidDataException>(() => PlanImage.Read(image.AsMemory(0, 100)));
        Assert.Throws<InvalidDataException>(() => PlanImage.Read(With(image, 0, 0x00)));
        Assert.Throws<InvalidDataException>(() => PlanImage.Read(WithInt(image, 4, 2)));
        Assert.Throws<InvalidDataException>(() => PlanImage.Read(WithInt(image, 8, Unsafe.SizeOf<PlanEvent>() + 8)));
        Assert.Throws<InvalidDataException>(() => PlanImage.Read(WithInt(image, 12, 0)));
        Assert.Throws<InvalidDataException>(() => PlanImage.Read(image.AsMemory(0, image.Length - 8)));
    }

    [Fact]
    public void AnyCorruptedByte_IsEitherReadOrRefused_NeverAnotherFailure()
    {
        var image = PlanImage.ToArray(RichPlan());
        Gen.Select(Gen.Int[0, image.Length - 1], Gen.Byte).Sample((at, value) =>
        {
            try
            {
                var read = PlanImage.Read(With(image, at, value));
                _ = PlaybackStream(read).Count();
            }
            catch (InvalidDataException)
            {
            }
        }, iter: 2_000);
    }

    // Notes, a program, controllers, a SysEx message, a chased hardware parameter sent as SysEx and as a controller,
    // and tempo and meter changes.
    private static PlaybackPlan RichPlan()
    {
        var tempo = new TempoMap(Resolution, [new TempoChange(Tick.Zero, Tempo.Default), new TempoChange(new Tick(2_000), Tempo.FromBeatsPerMinute(90))]);
        var meter = new MeterMap(Resolution, [new MeterChange(Tick.Zero, TimeSignature.CommonTime), new MeterChange(new Tick(4_000), new TimeSignature(3, 4))]);
        TrackEvent[] events =
        [
            Program(0, 5),
            Cc(0, 7, 100),
            new SysExEvent(EventId.New(), new Tick(10), SysExMessage.Create([0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7])),
            .. Enumerable.Range(0, 24).Select(i => (TrackEvent)Note(i * 250L, 200, 60 + (i % 12))),
            Cc(1_500, 1, 64),
            Cc(3_000, 1, 0),
        ];
        var track = Track.FromEvents(TrackId.New(), "t", events);
        var sequence = new Sequence(tempo, meter, [track], []);
        var parts = new List<PlanPart> { new(0, [.. track.ArrangedEvents.Select((e, i) => new SignalEvent(e, 0, i))]) };
        ParameterSend Send(long at, int value) => new(new Tick(at), 1_000, [
            new SysExEvent(EventId.New(), new Tick(at), SysExMessage.Create([0xF0, 0x43, 0x10, 0x4C, 0x08, 0x00, 0x0B, (byte)value, 0xF7])),
            new ControllerEvent(new Tick(at), One, new ControllerNumber(74), ControlValue.FromSevenBit(value)),
        ]);
        var parameters = new List<ParameterPart> { new(0, 0, [Send(500, 10), Send(1_800, 90), Send(3_500, 40)]) { Chased = true } };
        return PlaybackPlanCompiler.Compile(sequence, parts, parameters: parameters);
    }

    // Plays from mid-song (so the chase is sent), then loops 1,000-3,000 twice, and lists everything sent.
    private static List<string> Play(PlaybackPlan plan)
    {
        using var f = new PlaybackFixture();
        f.Engine.Load(plan);
        f.Engine.SetLoop(new LoopRegion(new Tick(1_000), new Tick(3_000)));
        f.Engine.Play(new Tick(2_200));
        for (var ms = 0; ms <= 9_000; ms += 5)
        {
            f.PumpAt(ms);
        }

        f.Engine.Stop();
        f.PumpAt(9_005);
        return f.Sent();
    }

    private static int EventOffset(byte[] image) => BitConverter.ToInt32(image, 4 + ((3 + (2 * 2) + 1) * 4));

    private static byte[] With(byte[] image, int at, byte value)
    {
        var copy = (byte[])image.Clone();
        copy[at] = value;
        return copy;
    }

    private static byte[] WithInt(byte[] image, int at, int value)
    {
        var copy = (byte[])image.Clone();
        BitConverter.TryWriteBytes(copy.AsSpan(at), value);
        return copy;
    }
}
