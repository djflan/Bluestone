namespace Bluestone.Audio.Clips;

/// <summary>
/// Plays one track's audio clips (<see cref="AudioClipRegion"/>s) on the song timeline while the transport plays. Each
/// clip is converted from its file's rate to the engine's (see <see cref="ResamplingKernel"/>) and streamed from
/// disk by the engine's disk thread; the audio thread never reads a file.
/// </summary>
public sealed class ClipPlayerNode : AudioNode, IStreamedSource, IDisposable
{
    private readonly ClipSource[] _sources;
    private readonly Lock _gate = new();
    private bool _closed;
    private long _readFailures;

    private ClipPlayerNode(string name, ClipSource[] sources)
    {
        Name = name;
        _sources = sources;
    }

    public override string Name { get; }

    /// <summary>The regions that opened; clips whose file is missing or unreadable are in the problems list of <see cref="Open"/>.</summary>
    public IReadOnlyList<AudioClipRegion> Regions => [.. _sources.Select(s => s.Region)];

    /// <summary>Frames played as silence because the disk was late, over every clip.</summary>
    public long UnderrunFrames => _sources.Sum(s => s.UnderrunFrames);

    /// <summary>Default amount of each clip decoded in memory when the graph is built.</summary>
    public static TimeSpan DefaultPreload { get; } = TimeSpan.FromSeconds(1);

    /// <summary>Default read-ahead per clip.</summary>
    public static TimeSpan DefaultRingLength { get; } = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Opens every region's file and preloads its start. Control thread (it reads files). A region whose file is missing
    /// or not a supported audio file is left out and described in <paramref name="problems"/>; it never throws for that.
    /// </summary>
    public static ClipPlayerNode Open(string name, IEnumerable<AudioClipRegion> regions, int outputRate, int blockFrames, out IReadOnlyList<string> problems, TimeSpan? preload = null, TimeSpan? ringLength = null)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var sources = new List<ClipSource>();
        var found = new List<string>();
        foreach (var region in regions)
        {
            try
            {
                sources.Add(ClipSource.Open(region, outputRate, blockFrames, preload ?? DefaultPreload, ringLength ?? DefaultRingLength));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Files.AudioFileException)
            {
                found.Add($"{System.IO.Path.GetFileName(region.Path)}: {Describe(ex)}");
            }
        }

        problems = found;
        return new ClipPlayerNode(name, [.. sources]);
    }

    /// <summary>
    /// Takes the gain and fades of <paramref name="regions"/> for the clips that play the same streams, without reopening
    /// anything. Control thread; the audio thread picks each one up at its next block.
    /// </summary>
    public void UpdateEnvelopes(IEnumerable<AudioClipRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var list = regions.ToList();
        foreach (var source in _sources)
        {
            if (list.Find(source.Region.SameStream) is { } region)
            {
                source.Envelope = region.Envelope;
            }
        }
    }

    public override void Render(in AudioRenderContext context, StereoBuffer input, StereoBuffer output)
    {
        if (!context.Playing)
        {
            return;
        }

        foreach (var source in _sources)
        {
            source.Render(context, output);
        }
    }

    /// <summary>Whether the files are closed: the node plays silence and must not be used in a new graph.</summary>
    public bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    /// <summary>Times reading a file failed (it was deleted, or the disk went away); those clips play silence.</summary>
    public long ReadFailures => Interlocked.Read(ref _readFailures);

    void IStreamedSource.Service()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            foreach (var source in _sources)
            {
                try
                {
                    source.Fill();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Files.AudioFileException)
                {
                    // The data stays missing, so the clip plays silence and counts underruns; the disk thread carries on.
                    Interlocked.Increment(ref _readFailures);
                }
            }
        }
    }

    /// <summary>
    /// Closes the files. Any thread, any number of times: the engine's disk thread closes a node that leaves its graph,
    /// and whoever built the node closes it when it is replaced or no longer wanted.
    /// </summary>
    public void Dispose() => ((IStreamedSource)this).Close();

    void IStreamedSource.Close()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            foreach (var source in _sources)
            {
                source.Dispose();
            }
        }
    }

    /// <summary>For tests: limits each clip's reading per disk pass, as a slow disk would.</summary>
    internal void LimitReading(int framesPerPass)
    {
        foreach (var source in _sources)
        {
            source.MaxFramesPerFill = framesPerPass;
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => "the file is missing",
        Files.AudioFileException => ex.Message,
        _ => $"the file cannot be read ({ex.Message})",
    };
}
