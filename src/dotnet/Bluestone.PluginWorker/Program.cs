using System.IO.Pipes;
using Bluestone.Plugins.Protocol;

namespace Bluestone.PluginWorker;

/// <summary>
/// Entry point. Usage: <c>Bluestone.PluginWorker --mode host|scan --pipe NAME</c>, with the per-launch token as the
/// first line on standard input (not on the command line, where other users could read it). The worker connects to
/// the host's pipe, completes the Hello handshake, and serves until Shutdown or until the pipe closes. The control
/// plane runs on the thread pool; the process's main thread is kept for plugins and their editors
/// (<see cref="WorkerMainThread"/>).
/// </summary>
internal static class Program
{
    public const int ExitOk = 0;
    public const int ExitUsage = 2;
    public const int ExitProtocolError = 3;
    public const int ExitConnectFailed = 4;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public static int Main(string[] args)
    {
        using var mainThread = WorkerMainThread.CreateForThisThread();
        var serving = Task.Run(() => RunAsync(args, mainThread));
        mainThread.Run(serving);
        return serving.GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(string[] args, WorkerMainThread mainThread)
    {
        WorkerFaults.CrashAtStartupIfRequested();
        if (!TryParse(args, out var mode, out var pipeName))
        {
            await Console.Error.WriteLineAsync("Usage: Bluestone.PluginWorker --mode host|scan --pipe NAME (token on standard input)").ConfigureAwait(false);
            return ExitUsage;
        }

        var token = await Console.In.ReadLineAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
        {
            await Console.Error.WriteLineAsync("No launch token on standard input.").ConfigureAwait(false);
            return ExitUsage;
        }

        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync($"Could not connect to the host: {ex.Message}").ConfigureAwait(false);
            return ExitConnectFailed;
        }

        try
        {
            using (var handshake = new CancellationTokenSource(ConnectTimeout))
            {
                await FrameCodec.WriteAsync(pipe, 0, Handshake.CreateHello(token, mode), handshake.Token).ConfigureAwait(false);
                var reply = await FrameCodec.ReadAsync(pipe, handshake.Token).ConfigureAwait(false)
                    ?? throw new ProtocolException("The host closed the pipe during the handshake.");
                Handshake.ValidateAck(reply.Message);
            }

            if (mode == WorkerMode.InstanceHost)
            {
                using var session = new InstanceHostSession(pipe, mainThread);
                await session.RunAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await RunScannerAsync(pipe).ConfigureAwait(false);
            }

            return ExitOk;
        }
        catch (ProtocolException ex)
        {
            await Console.Error.WriteLineAsync($"Protocol error: {ex.Message}").ConfigureAwait(false);
            return ExitProtocolError;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The host went away; nothing is left to serve.
            return ExitOk;
        }
    }

    private static async Task RunScannerAsync(Stream pipe)
    {
        while (await FrameCodec.ReadAsync(pipe, CancellationToken.None).ConfigureAwait(false) is { } frame)
        {
            switch (frame.Message)
            {
                case Shutdown:
                    return;
                case ScanModule scan:
                    await FrameCodec.WriteAsync(pipe, frame.RequestId, ModuleScanner.Scan(scan.ModulePath), CancellationToken.None).ConfigureAwait(false);
                    break;
                case Ping ping:
                    await FrameCodec.WriteAsync(pipe, frame.RequestId, new Pong(ping.Sequence), CancellationToken.None).ConfigureAwait(false);
                    break;
                default:
                    await FrameCodec.WriteAsync(pipe, frame.RequestId, new ErrorReply(ErrorCode.InvalidRequest, $"A scanner does not serve {frame.Message.Type}."), CancellationToken.None).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static bool TryParse(string[] args, out WorkerMode mode, out string pipeName)
    {
        mode = default;
        pipeName = string.Empty;
        string? modeText = null;
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--mode":
                    modeText = args[i + 1];
                    break;
                case "--pipe":
                    pipeName = args[i + 1];
                    break;
                default:
                    return false;
            }
        }

        mode = modeText switch
        {
            "host" => WorkerMode.InstanceHost,
            "scan" => WorkerMode.Scanner,
            _ => default,
        };
        return mode != default && pipeName.Length > 0 && args.Length % 2 == 0;
    }
}
