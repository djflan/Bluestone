using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Bluestone.PluginWorker.Native;

/// <summary>
/// Whether a plugin binary can load into this worker: its processor architecture must match the worker's. Reads only
/// the file headers (Mach-O, PE, ELF), never runs anything. A worker cannot host another architecture's plugins
/// (no Rosetta or bridged worker is built), so such a module is reported, not loaded.
/// </summary>
internal static class BinaryArchitecture
{
    private const uint MachO64 = 0xFEEDFACF;
    private const uint FatMagic = 0xCAFEBABE;
    private const uint FatMagic64 = 0xCAFEBABF;
    private const uint CpuArm64 = 0x0100000C;
    private const uint CpuX64 = 0x01000007;

    /// <summary>Null when the module can load here, else the reason it cannot.</summary>
    public static string? Check(string modulePath)
    {
        string? binary;
        try
        {
            binary = BinaryOf(modulePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The module cannot be read: {ex.Message}";
        }

        if (binary is null)
        {
            return $"The module has no binary for {Describe(RuntimeInformation.ProcessArchitecture)} on this operating system.";
        }

        var found = ArchitecturesOf(binary);
        if (found.Count == 0 || found.Contains(RuntimeInformation.ProcessArchitecture))
        {
            // Unknown formats are left to the loader, which reports its own error.
            return null;
        }

        return $"The module is built for {string.Join(" and ", found.Select(Describe))} only; this worker runs on {Describe(RuntimeInformation.ProcessArchitecture)} and cannot load it.";
    }

    private static string? BinaryOf(string modulePath)
    {
        if (File.Exists(modulePath))
        {
            return modulePath;
        }

        if (!Directory.Exists(modulePath))
        {
            return null;
        }

        var contents = Path.Combine(modulePath, "Contents");
        var name = Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(modulePath));
        if (OperatingSystem.IsMacOS())
        {
            var macOs = Path.Combine(contents, "MacOS");
            var named = Path.Combine(macOs, name);
            return File.Exists(named) ? named : Directory.Exists(macOs) ? Directory.EnumerateFiles(macOs).FirstOrDefault() : null;
        }

        var folders = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => ["x86_64"],
            Architecture.Arm64 => OperatingSystem.IsWindows() ? ["arm64", "arm64ec", "arm64x"] : ["aarch64"],
            _ => new[] { "i386", "x86" },
        };
        var suffix = OperatingSystem.IsWindows() ? "-win" : "-linux";
        var extension = OperatingSystem.IsWindows() ? ".vst3" : ".so";
        return folders.Select(f => Path.Combine(contents, f + suffix, name + extension)).FirstOrDefault(File.Exists);
    }

    private static List<Architecture> ArchitecturesOf(string binary)
    {
        Span<byte> header = stackalloc byte[4096];
        int read;
        using (var stream = File.OpenRead(binary))
        {
            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        }

        header = header[..read];
        var result = new List<Architecture>();
        if (header.Length < 8)
        {
            return result;
        }

        var bigEndianMagic = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (bigEndianMagic is FatMagic or FatMagic64)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            var entry = bigEndianMagic == FatMagic ? 20 : 32;
            for (var i = 0; i < count && 8 + ((i + 1) * entry) <= header.Length; i++)
            {
                AddMachO(result, BinaryPrimitives.ReadUInt32BigEndian(header[(8 + (i * entry))..]));
            }

            return result;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) == MachO64)
        {
            AddMachO(result, BinaryPrimitives.ReadUInt32LittleEndian(header[4..]));
            return result;
        }

        if (header[0] == 'M' && header[1] == 'Z' && header.Length >= 0x40)
        {
            var peOffset = BinaryPrimitives.ReadInt32LittleEndian(header[0x3C..]);
            if (peOffset > 0 && peOffset + 6 <= header.Length && header[peOffset] == 'P' && header[peOffset + 1] == 'E')
            {
                var machine = BinaryPrimitives.ReadUInt16LittleEndian(header[(peOffset + 4)..]);
                switch (machine)
                {
                    case 0x8664:
                        result.Add(Architecture.X64);
                        break;
                    case 0xAA64:
                        result.Add(Architecture.Arm64);
                        break;
                    case 0x14C:
                        result.Add(Architecture.X86);
                        break;
                }
            }

            return result;
        }

        if (header[0] == 0x7F && header[1] == 'E' && header[2] == 'L' && header[3] == 'F' && header.Length >= 20)
        {
            var machine = header[5] == 2 ? BinaryPrimitives.ReadUInt16BigEndian(header[18..]) : BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
            switch (machine)
            {
                case 62:
                    result.Add(Architecture.X64);
                    break;
                case 183:
                    result.Add(Architecture.Arm64);
                    break;
                case 3:
                    result.Add(Architecture.X86);
                    break;
            }
        }

        return result;
    }

    private static void AddMachO(List<Architecture> result, uint cpu)
    {
        if (cpu == CpuArm64)
        {
            result.Add(Architecture.Arm64);
        }
        else if (cpu == CpuX64)
        {
            result.Add(Architecture.X64);
        }
    }

    private static string Describe(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => architecture.ToString(),
    };
}
