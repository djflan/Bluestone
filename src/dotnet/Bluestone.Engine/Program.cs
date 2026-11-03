using Bluestone.Engine;

// The engine process (ADR 0044). Bluestone starts it with the name of the pipe to connect to and a per-launch token:
//   Bluestone.Engine --pipe NAME --token TOKEN [--restarted] [--allow-test-commands]
// With --spike it runs the ahead-of-time compilation smoke test instead.
if (args.Contains("--spike"))
{
    return AotSpike.Run();
}

if (Option("--pipe") is not { } pipe || Option("--token") is not { } token)
{
    Console.Error.WriteLine("Bluestone.Engine is started by Bluestone: --pipe NAME --token TOKEN [--restarted]. Use --spike for the AOT smoke test.");
    return 2;
}

return await EngineHost.RunAsync(pipe, token, args.Contains("--restarted"), args.Contains("--allow-test-commands")).ConfigureAwait(false);

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
