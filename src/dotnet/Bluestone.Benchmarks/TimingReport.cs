using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bluestone.Benchmarks;

/// <summary>
/// The machine-readable result of one probe run (ADR 0043): what ran, where, under which settings, and each metric's
/// distribution with its target and verdict. Written as JSON so runs can be compared (<c>--compare a.json b.json</c>).
/// </summary>
internal sealed class TimingReport
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public TimingReport(string probe, IReadOnlyDictionary<string, string> settings)
    {
        Probe = probe;
        Settings = new SortedDictionary<string, string>(settings.ToDictionary(), StringComparer.Ordinal);
        Started = DateTimeOffset.UtcNow;
        Machine = MachineInfo.Capture();
    }

    public string Probe { get; }

    public DateTimeOffset Started { get; }

    public SortedDictionary<string, string> Settings { get; }

    public MachineInfo Machine { get; }

    public List<Metric> Metrics { get; } = [];

    public List<string> Notes { get; } = [];

    public bool Passed => Metrics.All(m => m.Pass != false);

    public Metric Add(Metric metric)
    {
        Metrics.Add(metric);
        return metric;
    }

    public void Print()
    {
        Console.WriteLine();
        Console.WriteLine($"== {Probe} ({string.Join(", ", Settings.Select(kv => $"{kv.Key}={kv.Value}"))})");
        foreach (var metric in Metrics)
        {
            Console.WriteLine("  " + metric.Describe());
        }

        foreach (var note in Notes)
        {
            Console.WriteLine("  note: " + note);
        }

        if (Metrics.Any(m => m.Pass is not null))
        {
            Console.WriteLine(Passed ? "  RESULT: all targets met" : $"  RESULT: {Metrics.Count(m => m.Pass == false)} target(s) missed");
        }
    }

    /// <summary>Writes the report to <paramref name="destination"/>: a file, or a directory that gets a timestamped name.</summary>
    public string Write(string destination)
    {
        var path = destination.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? destination
            : Path.Combine(destination, $"{Probe}-{Started:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(ToJson(), Json));
        return path;
    }

    private JsonObject ToJson() => new()
    {
        ["probe"] = Probe,
        ["started"] = Started.ToString("O", CultureInfo.InvariantCulture),
        ["passed"] = Metrics.Any(m => m.Pass is not null) ? Passed : null,
        ["settings"] = new JsonObject(Settings.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        ["machine"] = JsonSerializer.SerializeToNode(Machine),
        ["metrics"] = new JsonArray([.. Metrics.Select(m => JsonSerializer.SerializeToNode(m))]),
        ["notes"] = new JsonArray([.. Notes.Select(n => (JsonNode?)n)]),
    };

    /// <summary>Prints each metric of <paramref name="after"/> next to the same metric in <paramref name="before"/>.</summary>
    public static void Compare(string before, string after)
    {
        var a = JsonNode.Parse(File.ReadAllText(before))!;
        var b = JsonNode.Parse(File.ReadAllText(after))!;
        Console.WriteLine($"before: {before} ({a["probe"]}, {a["started"]})");
        Console.WriteLine($"after : {after} ({b["probe"]}, {b["started"]})");
        var old = a["metrics"]!.AsArray().ToDictionary(m => (string)m!["Name"]!, m => m!);
        Console.WriteLine("metric | field | before | after | change");
        foreach (var metric in b["metrics"]!.AsArray())
        {
            var name = (string)metric!["Name"]!;
            if (!old.TryGetValue(name, out var previous))
            {
                Console.WriteLine($"{name} | (new) | | |");
                continue;
            }

            foreach (var field in new[] { "Value", "P50", "P99", "P999", "Max" })
            {
                var x = Number(previous, field);
                var y = Number(metric, field);
                if (x is null || y is null)
                {
                    continue;
                }

                var change = x == 0 ? (y == 0 ? "0%" : "new") : ((y - x) / Math.Abs(x.Value)).Value.ToString("+0%;-0%;0%", CultureInfo.InvariantCulture);
                Console.WriteLine($"{name} | {field} | {x:0.###} | {y:0.###} | {change}");
            }
        }
    }

    private static double? Number(JsonNode metric, string field) =>
        field == "Value" ? (double?)metric["Value"] : (double?)metric["Distribution"]?[field];
}

/// <summary>
/// One measured quantity. <see cref="Target"/> is the largest acceptable value (of <see cref="Value"/>, or of the
/// distribution's p99.9 and maximum); <see cref="Pass"/> is null when the metric has no target.
/// </summary>
internal sealed record Metric(string Name, string Unit, double? Value = null, Distribution? Distribution = null, double? Target = null)
{
    public bool? Pass => Target is not { } target
        ? null
        : Distribution is { } d ? d.P999 <= target && d.Max <= target : Value <= target;

    public string Describe()
    {
        var verdict = Pass switch { true => " [pass]", false => " [MISS]", null => string.Empty };
        var target = Target is { } t ? $" (target ≤ {t:0.###})" : string.Empty;
        return Distribution is { } d
            ? $"{Name}: n={d.Count} p50={d.P50:0.###} p95={d.P95:0.###} p99={d.P99:0.###} p99.9={d.P999:0.###} max={d.Max:0.###} {Unit}{target}{verdict}"
            : $"{Name}: {Value:0.###} {Unit}{target}{verdict}";
    }
}

/// <summary>Order statistics of a sample, in the metric's unit.</summary>
internal sealed record Distribution(long Count, double Min, double P50, double P95, double P99, double P999, double Max, double Mean)
{
    /// <summary>Exact order statistics (nearest rank) of the values.</summary>
    public static Distribution Of(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return new Distribution(0, 0, 0, 0, 0, 0, 0, 0);
        }

        double Rank(double percent) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percent / 100) - 1, 0, sorted.Length - 1)];
        return new Distribution(sorted.Length, sorted[0], Rank(50), Rank(95), Rank(99), Rank(99.9), sorted[^1], sorted.Average());
    }

    /// <summary>
    /// Order statistics of a histogram with buckets of <paramref name="bucketWidth"/>, each value taken as its bucket's
    /// upper bound; the last bucket is open, so a value there is reported as <paramref name="overflow"/>.
    /// </summary>
    public static Distribution OfHistogram(IReadOnlyList<long> histogram, double bucketWidth, double overflow)
    {
        var count = histogram.Sum();
        if (count == 0)
        {
            return new Distribution(0, 0, 0, 0, 0, 0, 0, 0);
        }

        double Bound(int i) => i == histogram.Count - 1 ? overflow : Math.Min((i + 1) * bucketWidth, overflow);
        double Rank(double percent)
        {
            var target = Math.Max(1, (long)Math.Ceiling(count * percent / 100));
            long cumulative = 0;
            for (var i = 0; i < histogram.Count; i++)
            {
                cumulative += histogram[i];
                if (cumulative >= target)
                {
                    return Bound(i);
                }
            }

            return overflow;
        }

        var first = Enumerable.Range(0, histogram.Count).First(i => histogram[i] > 0);
        var last = Enumerable.Range(0, histogram.Count).Last(i => histogram[i] > 0);
        var mean = Enumerable.Range(0, histogram.Count).Sum(i => histogram[i] * (Bound(i) - (bucketWidth / 2))) / count;
        return new Distribution(count, Bound(first) - bucketWidth, Rank(50), Rank(95), Rank(99), Rank(99.9), Bound(last), mean);
    }
}

/// <summary>Where and how a measurement ran, so results from different machines and configurations are not confused.</summary>
internal sealed record MachineInfo(
    string Os,
    string Cpu,
    int LogicalCores,
    long MemoryBytes,
    string Runtime,
    string Configuration,
    string Gc,
    string Commit,
    double LoadAverage1,
    double LoadAverage5,
    bool Quiet)
{
    public static MachineInfo Capture()
    {
        var load = new double[3];
        var haveLoad = !OperatingSystem.IsWindows() && getloadavg(load, 3) == 3;
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        // Every DOTNET_ runtime setting (GC, tiered compilation, ...) changes timing, so all of them are recorded.
        var runtimeSettings = Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(k => k.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Select(k => $"{k}={Environment.GetEnvironmentVariable(k)}");
        var gc = $"server={GCSettings.IsServerGC} latency={GCSettings.LatencyMode} {string.Join(' ', runtimeSettings)}".TrimEnd();

        // A busy machine (other builds, indexing) makes timing numbers meaningless: a 1-minute load average above
        // a quarter of the cores is flagged, and the probes print a warning.
        var quiet = !haveLoad || load[0] <= Environment.ProcessorCount / 4.0;
        return new MachineInfo(
            RuntimeInformation.OSDescription,
            CpuName(),
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            RuntimeInformation.FrameworkDescription,
            configuration,
            gc,
            Git("rev-parse --short HEAD") + (Git("status --porcelain --untracked-files=no").Length > 0 ? "+dirty" : string.Empty),
            haveLoad ? Math.Round(load[0], 2) : -1,
            haveLoad ? Math.Round(load[1], 2) : -1,
            quiet);
    }

    public void WarnIfBusy()
    {
        if (!Quiet)
        {
            Console.WriteLine($"WARNING: load average {LoadAverage1} on {LogicalCores} cores; close other work for trustworthy timing numbers.");
        }

        if (Configuration != "Release")
        {
            Console.WriteLine("WARNING: not a Release build; run with -c Release.");
        }
    }

    private static string CpuName() =>
        OperatingSystem.IsMacOS() ? Run("sysctl", "-n machdep.cpu.brand_string")
        : OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo")
            ? File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim() ?? RuntimeInformation.ProcessArchitecture.ToString()
            : Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString();

    private static string Git(string arguments) => Run("git", arguments);

    private static string Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (process is null)
            {
                return "unknown";
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return output;
        }
#pragma warning disable CA1031 // Identity details are best effort; a missing tool must not stop a measurement.
        catch (Exception)
#pragma warning restore CA1031
        {
            return "unknown";
        }
    }

    [DllImport("libc", EntryPoint = "getloadavg")]
#pragma warning disable CA5392, SYSLIB1054 // A single libc call in a measurement tool.
    private static extern int getloadavg([Out] double[] loadavg, int nelem);
#pragma warning restore CA5392, SYSLIB1054
}
