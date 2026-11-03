using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace Bluestone.Benchmarks;

/// <summary>
/// A child process that plays keys on a virtual Core MIDI source, as a MIDI keyboard would: the engine opens the source
/// as an input, so the keys reach it the way hardware input does, without passing through the probe process (ADR 0044
/// step 8, <c>--live-input midi</c>). Each key's packet is timestamped with the host clock when it is sent.
/// </summary>
/// <remarks>
/// Protocol on standard input and output: prints <c>ready</c>; on <c>play SECONDS</c> it plays keys 100–250 ms apart,
/// each released after 40 ms, prints <c>key TIMESTAMP</c> (a <see cref="Stopwatch"/> timestamp, the same clock in every
/// process) as each goes down, then <c>done</c>; on <c>stop</c> it ends.
/// </remarks>
[SupportedOSPlatform("macos")]
internal static unsafe class MidiKeys
{
    public const string SourceName = "Bluestone Probe Keys";

    public static void Run()
    {
        var name = Create("Bluestone probe keys");
        Check(SinkNative.MIDIClientCreate(name, IntPtr.Zero, IntPtr.Zero, out var client), "MIDIClientCreate");
        Check(SinkNative.MIDISourceCreate(client, Create(SourceName), out var source), "MIDISourceCreate");
        Console.WriteLine("ready");
        while (Console.ReadLine() is { } line)
        {
            var parts = line.Split(' ');
            if (parts[0] == "play")
            {
                Play(source, int.Parse(parts[1], CultureInfo.InvariantCulture));
                Console.WriteLine("done");
            }
            else if (parts[0] == "stop")
            {
                break;
            }
        }

        _ = SinkNative.MIDIClientDispose(client);
    }

    private static void Play(uint source, int seconds)
    {
        var random = new Random(8);
        var until = Stopwatch.GetTimestamp() + (seconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < until)
        {
            var note = (byte)(60 + random.Next(12));
            var sent = Send(source, 0x90, note, 127);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"key {sent}"));
            Thread.Sleep(40);
            Send(source, 0x80, note, 64);
            Thread.Sleep(60 + random.Next(150));
        }
    }

    // One message in a packet stamped now; returns when it was sent, on the Stopwatch clock.
    private static long Send(uint source, byte status, byte data1, byte data2)
    {
        var packetList = stackalloc byte[64];
        var stamp = Stopwatch.GetTimestamp();
        *(uint*)packetList = 1;
        *(ulong*)(packetList + 4) = SinkNative.mach_absolute_time();
        *(ushort*)(packetList + 12) = 3;
        packetList[14] = status;
        packetList[15] = data1;
        packetList[16] = data2;
        _ = SinkNative.MIDIReceived(source, packetList);
        return stamp;
    }

    private static IntPtr Create(string value)
    {
        fixed (char* chars = value)
        {
            return SinkNative.CFStringCreateWithCharacters(IntPtr.Zero, chars, value.Length);
        }
    }

    private static void Check(int status, string call)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"{call} failed: {status}");
        }
    }
}
