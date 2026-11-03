using System.Diagnostics;
using System.IO.Pipes;
using Bluestone.Audio.Devices;
using Bluestone.Engine.Protocol;

namespace Bluestone.Engine;

/// <summary>
/// The engine process's side of the control channel (ADR 0044): connect to Bluestone's pipe, introduce itself with the
/// launch token, report Ready, then send a heartbeat with the process's figures every
/// <see cref="EngineLimits.HeartbeatInterval"/> and answer commands until told to shut down or until Bluestone goes away.
/// </summary>
/// <remarks>
/// The engine owns MIDI playback, outputs, inputs, and live routes to external instruments (<see cref="MidiEngine"/>,
/// migration step 5), and audio: the device, the graph, clips, meters, and the plugin data plane (<see cref="AudioHost"/>,
/// step 6). Bluestone sends the outputs, plan, routes, inputs, audio settings, and graph after Ready, and again after a
/// restart. A restarted engine sends All Notes Off on every output as it opens it, and to every plugin before its first
/// block, before anything else.
/// </remarks>
internal static class EngineHost
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync(string pipeName, string token, bool restarted, bool allowTestCommands, bool testAudio)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            await Console.Error.WriteLineAsync($"Could not connect to Bluestone: {ex.Message}").ConfigureAwait(false);
            return 3;
        }

        using var channel = new EngineChannel(pipe);
        using var stop = new CancellationTokenSource();
        try
        {
            await channel.WriteAsync(new Hello(EngineLimits.Version, token, Environment.ProcessId, restarted), stop.Token).ConfigureAwait(false);
            if (await channel.ReadAsync(stop.Token).ConfigureAwait(false) is not Welcome { Version: EngineLimits.Version })
            {
                await Console.Error.WriteLineAsync("Bluestone did not accept the engine.").ConfigureAwait(false);
                return 4;
            }

            using var midi = new MidiEngine(restarted);
            using var audio = new AudioHost(midi.Playback, testAudio && allowTestCommands ? new TestAudioBackend() : SystemAudioBackend.Instance, restarted, test: testAudio && allowTestCommands);
            await channel.WriteAsync(new Ready(), stop.Token).ConfigureAwait(false);
            var hung = false;
            var heartbeats = Task.Run(() => HeartbeatAsync(channel, () => Volatile.Read(ref hung), stop.Token), CancellationToken.None);
            var sender = new Thread(() => Report(midi, audio, channel, () => Volatile.Read(ref hung), stop.Token)) { IsBackground = true, Name = "Bluestone engine reports" };
            sender.Start();
            while (await channel.ReadAsync(stop.Token).ConfigureAwait(false) is { } message)
            {
                switch (message)
                {
                    case Ping ping when !Volatile.Read(ref hung):
                        await channel.WriteAsync(new Pong(ping.Id), stop.Token).ConfigureAwait(false);
                        break;
                    case Shutdown:
                        await stop.CancelAsync().ConfigureAwait(false);
                        await heartbeats.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                        sender.Join();
                        return 0;
                    case TestCommand test when allowTestCommands:
                        switch (test.Action)
                        {
                            case TestAction.Crash:
                                Process.GetCurrentProcess().Kill();
                                break;
                            case TestAction.Hang:
                                Volatile.Write(ref hung, true);
                                break;
                            case TestAction.Fault:
                                await channel.WriteAsync(new Fault("A test fault on the playback thread."), stop.Token).ConfigureAwait(false);
                                break;
                        }

                        break;
                    case TestCommand or Ping:
                        break;
                    case TestRender when !allowTestCommands:
                        break;
                    case var command when AudioHost.Handles(command):
                        audio.Apply(command);
                        break;
                    default:
                        foreach (var problem in midi.Apply(message))
                        {
                            await channel.WriteAsync(problem, stop.Token).ConfigureAwait(false);
                        }

                        break;
                }
            }

            // Bluestone closed the channel without a Shutdown: it has gone, so the engine goes too.
            await stop.CancelAsync().ConfigureAwait(false);
            await heartbeats.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            sender.Join();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or EngineProtocolException or ObjectDisposedException)
        {
            await Console.Error.WriteLineAsync($"The control channel failed: {ex.Message}").ConfigureAwait(false);
            return 5;
        }
    }

    // The sender thread: MIDI for forwarded outputs and from inputs as it comes, the transport, timing, and plan
    // acknowledgements; the audio device's state, graph acknowledgements, and meters.
    private static void Report(MidiEngine midi, AudioHost audio, EngineChannel channel, Func<bool> hung, CancellationToken stop)
    {
        var faulted = false;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                midi.Outbound.Signal.WaitOne(2);
                if (hung())
                {
                    continue;
                }

                void Send(EngineMessage message) => channel.WriteAsync(message, stop).GetAwaiter().GetResult();
                midi.Report(Send);
                audio.Report(Send);
                if (!faulted && midi.Fault is { } fault)
                {
                    faulted = true;
                    channel.WriteAsync(new Fault($"The playback thread failed: {fault.Message}"), stop).GetAwaiter().GetResult();
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Stopping, or the channel is gone: the read loop ends the process.
        }
    }

    private static async Task HeartbeatAsync(EngineChannel channel, Func<bool> hung, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(EngineLimits.HeartbeatInterval);
        var sequence = 0L;
        try
        {
            do
            {
                if (!hung())
                {
                    await channel.WriteAsync(new Heartbeat(sequence++, Telemetry()), stop).ConfigureAwait(false);
                }
            }
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Stopping, or the channel is gone: the read loop ends the process.
        }
    }

    private static EngineTelemetry Telemetry() => new(
        GC.CollectionCount(0),
        GC.GetTotalPauseDuration(),
        GC.GetTotalAllocatedBytes(),
        Environment.WorkingSet,
        System.Runtime.JitInfo.GetCompiledMethodCount());
}
