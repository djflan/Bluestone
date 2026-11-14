using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Bluestone.Audio;
using Bluestone.Desktop.Controls;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// A junction's signal indicators (its level and note lamp, drawn by <see cref="JunctionSeam"/>) rendered standalone, with the
/// app's theme, in a fixed matrix of states and settings at 2x scale. Every frame is saved in the test output
/// (<c>frames/signal-*.png</c>). Set <c>BLUESTONE_BASELINE_DIR</c> to compare each frame pixel for pixel with a stored baseline
/// there, or also <c>BLUESTONE_BASELINE_WRITE=1</c> to write that baseline from the current rendering.
/// </summary>
public sealed class SignalIndicatorTests
{
    private const double Dpi = 192;

    // A fixed "now" (Stopwatch ticks): the level's peak hold depends only on the timestamps it is given.
    private const long Now = 1_000_000_000;

    private static readonly MethodInfo UpdateSignal = typeof(JunctionSignal).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("JunctionSignal.Update(bool, MeterReading?, long) was not found.");

    private static readonly (string Name, bool NotesHeld, MeterReading? Reading)[] States =
    [
        ("silent", false, null),
        ("moderate", false, new MeterReading(0.5f, 0.45f, 0.25f, 0.22f)),
        ("hot", false, new MeterReading(1.05f, 0.97f, 0.62f, 0.58f)),
        ("notes", true, new MeterReading(0.5f, 0.45f, 0.25f, 0.22f)),
    ];

    private static readonly JunctionLevelStyle[] LevelStyles = [JunctionLevelStyle.RmsAndPeak, JunctionLevelStyle.Peak];

    private static readonly JunctionLampPlacement[] Placements = [JunctionLampPlacement.BelowLevel, JunctionLampPlacement.AboveLevel];

    private static readonly JunctionIndicatorPosition[] Positions = [JunctionIndicatorPosition.LowerHalf];

    [Fact]
    public Task Seam_RendersEveryStateAsTheBaselineDoes() => HeadlessApp.RunAsync(() =>
    {
        var baseline = Environment.GetEnvironmentVariable("BLUESTONE_BASELINE_DIR");
        var write = Environment.GetEnvironmentVariable("BLUESTONE_BASELINE_WRITE") == "1";
        Assert.False(write && string.IsNullOrEmpty(baseline), "BLUESTONE_BASELINE_WRITE=1 needs BLUESTONE_BASELINE_DIR.");
        if (write)
        {
            Directory.CreateDirectory(baseline!);
        }

        var frames = Path.Combine(AppContext.BaseDirectory, "frames");
        Directory.CreateDirectory(frames);
        var mismatches = new List<string>();
        var rendered = 0;
        foreach (var position in Positions)
        {
            foreach (var placement in Placements)
            {
                foreach (var style in LevelStyles)
                {
                    foreach (var state in States)
                    {
                        var name = $"signal-{state.Name}-{Slug(style)}-{Slug(placement)}-{Slug(position)}.png";
                        var png = Render(state.NotesHeld, state.Reading, style, placement, position);
                        File.WriteAllBytes(Path.Combine(frames, name), png);
                        var (size, pixels) = Decode(png);
                        Assert.True(pixels.Any(b => b != 0), $"{name}: nothing was rendered.");
                        rendered++;
                        if (string.IsNullOrEmpty(baseline))
                        {
                            continue;
                        }

                        var stored = Path.Combine(baseline, name);
                        if (write)
                        {
                            File.WriteAllBytes(stored, png);
                            continue;
                        }

                        if (!File.Exists(stored))
                        {
                            mismatches.Add($"{name}: no baseline at {stored}");
                            continue;
                        }

                        var (storedSize, storedPixels) = Decode(File.ReadAllBytes(stored));
                        if (storedSize != size)
                        {
                            mismatches.Add($"{name}: size {size.Width}x{size.Height}, baseline {storedSize.Width}x{storedSize.Height}");
                            continue;
                        }

                        var differing = 0;
                        for (var i = 0; i < pixels.Length; i += 4)
                        {
                            if (!pixels.AsSpan(i, 4).SequenceEqual(storedPixels.AsSpan(i, 4)))
                            {
                                differing++;
                            }
                        }

                        if (differing > 0)
                        {
                            mismatches.Add($"{name}: {differing} of {size.Width * size.Height} pixels differ");
                        }
                    }
                }
            }
        }

        Assert.Equal(States.Length * LevelStyles.Length * Placements.Length * Positions.Length, rendered);
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} frame(s) differ from the baseline in {baseline}:{Environment.NewLine}{string.Join(Environment.NewLine, mismatches)}");
        return Task.CompletedTask;
    });

    /// <summary>
    /// One seam as the Device View hosts it (in a <c>Border.chainLink</c>, so the theme sizes both), with room either side for
    /// anything drawn past its bounds, on the Device View's surface. Returns the frame as a PNG.
    /// </summary>
    private static byte[] Render(bool notesHeld, MeterReading? reading, JunctionLevelStyle style, JunctionLampPlacement placement, JunctionIndicatorPosition position)
    {
        var signal = new JunctionSignal();
        SetInternal(signal, nameof(JunctionSignal.Label), "After Polysynth");
        SetInternal(signal, nameof(JunctionSignal.CanCarryNotes), true);
        SetInternal(signal, nameof(JunctionSignal.CanCarryAudio), true);
        var seam = new JunctionSeam { Signal = signal, LevelStyle = style, LampPlacement = placement, IndicatorPosition = position };
        var link = new Border { Classes = { "chainLink" }, Child = seam };
        var host = new Border
        {
            Background = (IBrush)Avalonia.Application.Current!.FindResource("Surface.Base")!,
            Padding = new Thickness(10, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = link,
        };
        var window = new Window { Width = 200, Height = 400, Content = host };
        window.Show();
        try
        {
            // Attached, the seam follows its signal: the reading arrives (and the lamp lights at once if notes are held).
            UpdateSignal.Invoke(signal, [notesHeld, reading, Now]);
            window.UpdateLayout();
            Assert.Equal(new Size(40, 212), host.Bounds.Size);
            Assert.Equal(new Point(0, 0), host.Bounds.Position);
            var pixels = new PixelSize((int)(host.Bounds.Width * Dpi / 96), (int)(host.Bounds.Height * Dpi / 96));
            using var bitmap = new RenderTargetBitmap(pixels, new Vector(Dpi, Dpi));
            bitmap.Render(host);
            using var stream = new MemoryStream();
            bitmap.Save(stream, new PngBitmapEncoderOptions());
            return stream.ToArray();
        }
        finally
        {
            window.Close();
        }
    }

    private static (PixelSize Size, byte[] Pixels) Decode(byte[] png)
    {
        using var bitmap = new Bitmap(new MemoryStream(png));
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        return (size, pixels);
    }

    private static void SetInternal(JunctionSignal signal, string property, object value) =>
        (typeof(JunctionSignal).GetProperty(property) ?? throw new InvalidOperationException($"JunctionSignal.{property} was not found.")).SetValue(signal, value);

    private static string Slug<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();
}
