using BenchmarkDotNet.Attributes;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Infrastructure.Projects;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Files;
using Bluestone.Midi.Timing;
using Bluestone.Playback;
using Bluestone.Profiles;

namespace Bluestone.Benchmarks;

/// <summary>Preparing a plan from the project, which happens after every edit during playback.</summary>
[MemoryDiagnoser]
public class PlanCompileBenchmarks
{
    private Project _project = null!;

    [Params(16)]
    public int Tracks { get; set; }

    [Params(1_000, 10_000)]
    public int NotesPerTrack { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _project = Workloads.Routed(Workloads.Dense(Tracks, NotesPerTrack));
    }

    /// <summary>Routing, signal graph evaluation, and plan compilation together, as after every edit.</summary>
    [Benchmark]
    public PlaybackPlan Compile() => Workloads.Compile(_project);
}

/// <summary>
/// The real-time path: pumping a playing engine through dense material on a virtual clock in 1 ms
/// steps. Reported per pump; allocation must be zero.
/// </summary>
[MemoryDiagnoser]
public class SchedulerBenchmarks
{
    private const int StepsPerInvoke = 1000;
    private PlaybackPlan _plan = null!;
    private PlaybackEngine _engine = null!;
    private VirtualClock _clock = null!;
    private CountingOutput _output = null!;

    [Params(EndpointCapabilities.None, EndpointCapabilities.ScheduledDelivery)]
    public EndpointCapabilities Delivery { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var sequence = Workloads.Dense(16, 20_000);
        _plan = Workloads.Compile(Workloads.Routed(sequence));
        _clock = new VirtualClock(TimeSpan.FromSeconds(1));
        _output = new CountingOutput(Delivery);
        _engine = new PlaybackEngine(_clock, sequence.TempoMap);
        _engine.SetOutputs([_output]);
        _engine.Load(_plan);
        _engine.Play(Tick.Zero);
        _engine.Pump();
    }

    [GlobalCleanup]
    public void Cleanup() => _engine.Dispose();

    /// <summary>1000 pumps, each 1 ms apart: one second of dense playback (16 tracks × 16ths at 120 BPM).</summary>
    [Benchmark(OperationsPerInvoke = StepsPerInvoke)]
    public long PumpOneSecond()
    {
        for (var i = 0; i < StepsPerInvoke; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(1));
            _engine.Pump();
        }

        return _output.Count;
    }
}

[MemoryDiagnoser]
public class TempoMapBenchmarks
{
    private TempoMap _map = null!;

    [GlobalSetup]
    public void Setup() =>
        _map = new TempoMap(Workloads.Resolution, Enumerable.Range(0, 500).Select(i => new TempoChange(new Tick(i * 3840L), new Tempo(400_000 + (i * 1000)))));

    [Benchmark]
    public TimeSpan TimeAt() => _map.TimeAt(new Tick(987_654));

    [Benchmark]
    public Tick TickAt() => _map.TickAt(TimeSpan.FromSeconds(321.123));
}

/// <summary>Interchange and persistence for a large song: 16 tracks × 10,000 notes plus automation.</summary>
[MemoryDiagnoser]
public class FileBenchmarks
{
    private Sequence _sequence = null!;
    private byte[] _midi = null!;
    private byte[] _project = null!;
    private ProjectDocument _document = null!;
    private byte[] _profile = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sequence = Workloads.Dense(16, 10_000);
        _midi = SmfWriter.Write(SmfExporter.Export(_sequence).File);
        _document = new ProjectDocument(Workloads.ProjectFor(_sequence));
        _project = ProjectSerializer.Default.Serialize(_document);
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "global.json")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        _profile = File.ReadAllBytes(Path.Combine(root, "profiles", "general-midi.bluestone-profile.json"));
    }

    [Benchmark]
    public Sequence ImportMidi() => SmfImporter.Import(SmfReader.Read(_midi).File).Sequence;

    [Benchmark]
    public byte[] ExportMidi() => SmfWriter.Write(SmfExporter.Export(_sequence).File);

    [Benchmark]
    public byte[] SaveProject() => ProjectSerializer.Default.Serialize(_document);

    [Benchmark]
    public ProjectDocument OpenProject() => ProjectSerializer.Default.Deserialize(_project);

    [Benchmark]
    public DeviceProfile? LoadGeneralMidiProfile() => ProfileLoader.Load(_profile).Profile;
}
