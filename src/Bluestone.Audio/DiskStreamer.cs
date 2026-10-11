namespace Bluestone.Audio;

/// <summary>
/// The engine's disk thread: every few milliseconds (or when a graph is published) it lets each disk source of the
/// current graph read ahead, and closes the files of sources that left the graph. The audio thread only reads
/// buffers this thread has filled.
/// </summary>
internal sealed class DiskStreamer : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(5);
    private readonly AudioEngine _engine;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<IStreamedSource> _open = [];

    public DiskStreamer(AudioEngine engine)
    {
        _engine = engine;
        _thread = new Thread(Run) { IsBackground = true, Name = "Bluestone audio disk streamer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Wake() => _wake.Set();

    public void Dispose()
    {
        _stop.Cancel();
        _wake.Set();
        _thread.Join();
        foreach (var source in _open)
        {
            source.Close();
        }

        _open.Clear();
        _wake.Dispose();
        _stop.Dispose();
    }

    /// <summary>Services every source of <paramref name="graph"/>; closes sources in <paramref name="open"/> that are no longer in it.</summary>
    public static void ServiceOnce(AudioGraph graph, HashSet<IStreamedSource> open)
    {
        foreach (var source in graph.Streams)
        {
            source.Service();
        }

        var current = graph.Streams;
        open.RemoveWhere(source =>
        {
            if (current.Contains(source))
            {
                return false;
            }

            source.Close();
            return true;
        });
        open.UnionWith(current);
    }

    private void Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            ServiceOnce(_engine.Graph, _open);
            _wake.WaitOne(Interval);
        }
    }
}
