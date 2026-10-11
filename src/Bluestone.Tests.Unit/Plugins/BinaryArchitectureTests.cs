using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Bluestone.PluginWorker.Native;
using CpuArchitecture = System.Runtime.InteropServices.Architecture;

namespace Bluestone.Tests.Unit.Plugins;

/// <summary>A worker loads only plugin binaries of its own architecture; the check reads headers, never code.</summary>
public sealed class BinaryArchitectureTests
{
    private const uint CpuArm64 = 0x0100000C;
    private const uint CpuX64 = 0x01000007;

    private static CpuArchitecture Other => RuntimeInformation.ProcessArchitecture == CpuArchitecture.Arm64 ? CpuArchitecture.X64 : CpuArchitecture.Arm64;

    private static uint MachCpu(CpuArchitecture architecture) => architecture == CpuArchitecture.Arm64 ? CpuArm64 : CpuX64;

    private static byte[] ThinMachO(CpuArchitecture architecture)
    {
        var bytes = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), MachCpu(architecture));
        return bytes;
    }

    private static byte[] FatMachO(params CpuArchitecture[] architectures)
    {
        var bytes = new byte[8 + (20 * architectures.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)architectures.Length);
        for (var i = 0; i < architectures.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8 + (i * 20)), MachCpu(architectures[i]));
        }

        return bytes;
    }

    private static byte[] Elf(CpuArchitecture architecture)
    {
        var bytes = new byte[64];
        "\u007FELF"u8.CopyTo(bytes);
        bytes[5] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), (ushort)(architecture == CpuArchitecture.Arm64 ? 183 : 62));
        return bytes;
    }

    private static byte[] Pe(CpuArchitecture architecture)
    {
        var bytes = new byte[256];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C), 0x80);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x84), (ushort)(architecture == CpuArchitecture.Arm64 ? 0xAA64 : 0x8664));
        return bytes;
    }

    public static TheoryData<string> Formats() => ["mach-o", "fat", "elf", "pe"];

    private static byte[] Binary(string format, CpuArchitecture architecture) => format switch
    {
        "mach-o" => ThinMachO(architecture),
        "fat" => FatMachO(architecture),
        "elf" => Elf(architecture),
        _ => Pe(architecture),
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void Check_ABinaryForThisArchitecture_Passes_AndOneForAnother_IsRefusedWithAReason(string format)
    {
        using var folder = new TestDirectory();
        File.WriteAllBytes(folder.File("own.clap"), Binary(format, RuntimeInformation.ProcessArchitecture));
        File.WriteAllBytes(folder.File("other.clap"), Binary(format, Other));

        Assert.Null(BinaryArchitecture.Check(folder.File("own.clap")));
        var reason = BinaryArchitecture.Check(folder.File("other.clap"));
        Assert.NotNull(reason);
        Assert.Contains("cannot load it", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_AUniversalBinary_PassesWhenItContainsThisArchitecture()
    {
        using var folder = new TestDirectory();
        File.WriteAllBytes(folder.File("universal.clap"), FatMachO(CpuArchitecture.X64, CpuArchitecture.Arm64));

        Assert.Null(BinaryArchitecture.Check(folder.File("universal.clap")));
    }

    [Fact]
    public void Check_ABundle_ReadsTheBinaryForThisPlatform_AndOneWithoutIt_IsRefused()
    {
        using var folder = new TestDirectory();
        var bundle = folder.File("Synth.vst3");
        var binary = OperatingSystem.IsMacOS() ? Path.Combine(bundle, "Contents", "MacOS", "Synth")
            : OperatingSystem.IsWindows() ? Path.Combine(bundle, "Contents", RuntimeInformation.ProcessArchitecture == CpuArchitecture.Arm64 ? "arm64-win" : "x86_64-win", "Synth.vst3")
            : Path.Combine(bundle, "Contents", RuntimeInformation.ProcessArchitecture == CpuArchitecture.Arm64 ? "aarch64-linux" : "x86_64-linux", "Synth.so");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllBytes(binary, OperatingSystem.IsMacOS() ? ThinMachO(Other) : OperatingSystem.IsWindows() ? Pe(Other) : Elf(Other));
        var empty = folder.File("Empty.vst3");
        Directory.CreateDirectory(Path.Combine(empty, "Contents"));

        Assert.NotNull(BinaryArchitecture.Check(bundle));
        Assert.Contains("no binary", BinaryArchitecture.Check(empty), StringComparison.Ordinal);
    }
}
