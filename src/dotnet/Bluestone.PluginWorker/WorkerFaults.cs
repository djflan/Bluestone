using System.Diagnostics;
using Bluestone.Plugins.Protocol;

namespace Bluestone.PluginWorker;

/// <summary>
/// Test-only failure behaviours, reachable only through an <see cref="InduceTestFault"/> message (and, for a crash at
/// startup, the documented <see cref="CrashAtStartupVariable"/> environment variable, because no message can arrive
/// before the connection exists).
/// </summary>
internal static class WorkerFaults
{
    public const string CrashAtStartupVariable = "BLUESTONE_PLUGINWORKER_TEST_CRASH_AT_STARTUP";

    private static volatile bool _hung;

    public static bool IsHung => _hung;

    /// <summary>
    /// Dies abruptly before connecting when <see cref="CrashAtStartupVariable"/> is "1". It kills itself (SIGKILL,
    /// TerminateProcess) rather than calling FailFast, so repeated restart tests do not fill the OS crash-report folder.
    /// </summary>
    public static void CrashAtStartupIfRequested()
    {
        if (Environment.GetEnvironmentVariable(CrashAtStartupVariable) == "1")
        {
            using var self = Process.GetCurrentProcess();
            self.Kill();
            Environment.Exit(70);
        }
    }

    /// <summary>Applies a fault. Returns only for <see cref="TestFault.GarbageFrames"/>.</summary>
    public static async Task InduceAsync(TestFault fault, Stream pipe, CancellationToken cancellationToken)
    {
        switch (fault)
        {
            case TestFault.FailFast:
                Environment.FailFast("Bluestone plugin worker: FailFast induced by a test message.");
                break;
            case TestFault.Hang:
                _hung = true;
                Thread.Sleep(Timeout.Infinite);
                break;
            case TestFault.GarbageFrames:
                // A length prefix far beyond the limit, then noise.
                byte[] garbage = [0xFF, 0xFF, 0xFF, 0x7F, 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x13, 0x37];
                await pipe.WriteAsync(garbage, cancellationToken).ConfigureAwait(false);
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ProtocolException($"Unknown test fault {fault}.");
        }
    }
}
