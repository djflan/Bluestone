using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Bluestone.Audio;
using Bluestone.Desktop.Controls;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// A gallery of the signal indicator family for review: Compact (the junction's own seam), Medium and Large with bar and
/// round LEDs, side by side, in the states a musician sees (silent, moderate, hot, clipping, notes held, and the peak style),
/// and Large with keyboard focus,
/// with the app's theme. Saved at 2x (<c>frames/signal-indicator-gallery.png</c>) and at actual pixels
/// (<c>frames/signal-indicator-gallery-1x.png</c>), and copied to <c>BLUESTONE_GALLERY_DIR</c> when that is set.
/// </summary>
public sealed class SignalIndicatorGalleryTests
{
    // A fixed "now" (Stopwatch ticks): every reading, and so every held peak, is deterministic.
    private const long Now = 1_000_000_000;

    private static readonly MethodInfo UpdateSignal = typeof(JunctionSignal).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("JunctionSignal.Update(bool, MeterReading?, long) was not found.");

    // Each state: what was played half a second ago (so a held peak floats above a quieter reading), what is playing now,
    // whether notes are held, and the level style. Readings are (peak, rms) in dB for the left side; the right is a dB lower.
    private static readonly GalleryState[] States =
    [
        new("Silent", null, null, false, JunctionLevelStyle.RmsAndPeak),
        new("Moderate", (-9, -18), (-14, -21), false, JunctionLevelStyle.RmsAndPeak),
        new("Hot", (-1, -6), (-2.5, -7), false, JunctionLevelStyle.RmsAndPeak),
        new("Clipping", (1.5, -3), (0.6, -4), false, JunctionLevelStyle.RmsAndPeak),
        new("Notes held", (-9, -18), (-14, -21), true, JunctionLevelStyle.RmsAndPeak),
        new("Peak style", (-9, -18), (-14, -21), false, JunctionLevelStyle.Peak),
    ];

    private static readonly (string Name, SignalIndicatorSize? Variant, LedShape Shape)[] Columns =
    [
        ("Compact (seam)", null, LedShape.Bar),
        ("Medium · Bar", SignalIndicatorSize.Medium, LedShape.Bar),
        ("Medium · Round", SignalIndicatorSize.Medium, LedShape.Round),
        ("Large · Bar", SignalIndicatorSize.Large, LedShape.Bar),
        ("Large · Round", SignalIndicatorSize.Large, LedShape.Round),
    ];

    private readonly List<(JunctionSeam Seam, GalleryState State)> _seams = [];

    [Fact]
    public Task Gallery_RendersEveryVariantInEveryState() => HeadlessApp.RunAsync(() =>
    {
        var app = Avalonia.Application.Current!;
        IBrush Brush(string key) => (IBrush)app.FindResource(key)!;
        double Size(string key) => (double)app.FindResource(key)!;

        var grid = new Grid { Margin = new Thickness(Size("Space.XL")), ColumnSpacing = Size("Space.XXL"), RowSpacing = Size("Space.XL") };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        foreach (var _ in Columns)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        foreach (var _ in States)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        // One more row: Large with keyboard focus, clipping, so its focus ring shows around it.
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        TextBlock Label(string text, IBrush brush) => new()
        {
            Text = text,
            Foreground = brush,
            FontSize = Size("FontSize.Caption"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        for (var c = 0; c < Columns.Length; c++)
        {
            var header = Label(Columns[c].Name, Brush("Text.Primary"));
            Grid.SetColumn(header, c + 1);
            grid.Children.Add(header);
        }

        var indicators = new List<Control>();
        for (var r = 0; r < States.Length; r++)
        {
            var state = States[r];
            var rowLabel = Label(state.Name, Brush("Text.Secondary"));
            rowLabel.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(rowLabel, r + 1);
            grid.Children.Add(rowLabel);
            for (var c = 0; c < Columns.Length; c++)
            {
                var (_, variant, shape) = Columns[c];
                Control cell = variant is { } size ? Indicator(size, shape, state) : Seam(state);
                cell.HorizontalAlignment = HorizontalAlignment.Center;
                cell.VerticalAlignment = VerticalAlignment.Bottom;
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c + 1);
                grid.Children.Add(cell);
                indicators.Add(cell);
            }
        }

        var focusedLabel = Label("Focused (keyboard)", Brush("Text.Secondary"));
        focusedLabel.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(focusedLabel, States.Length + 1);
        grid.Children.Add(focusedLabel);
        var focused = Indicator(SignalIndicatorSize.Large, LedShape.Bar, States.Single(s => s.Name == "Clipping"));
        focused.HorizontalAlignment = HorizontalAlignment.Center;
        focused.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetRow(focused, States.Length + 1);
        Grid.SetColumn(focused, Array.FindIndex(Columns, c => c.Name == "Large · Bar") + 1);
        grid.Children.Add(focused);
        indicators.Add(focused);

        var host = new Border { Background = Brush("Surface.Base"), Child = grid, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var window = new Window { Width = 1200, Height = 2400, Content = host };
        window.Show();
        try
        {
            // Attached, each seam follows its signal (and its lamp lights at once when notes are held).
            foreach (var (seam, state) in _seams)
            {
                if (state.Before is not null)
                {
                    UpdateSignal.Invoke(seam.Signal, [false, Reading(state.Before), Now - (Stopwatch.Frequency / 2)]);
                }

                UpdateSignal.Invoke(seam.Signal, [state.NotesHeld, Reading(state.Now), Now]);
            }

            Assert.True(focused.Focus(NavigationMethod.Tab));
            Assert.Contains(":focus-visible", focused.Classes);
            window.UpdateLayout();
            var frames = Path.Combine(AppContext.BaseDirectory, "frames");
            Directory.CreateDirectory(frames);
            var gallery = Environment.GetEnvironmentVariable("BLUESTONE_GALLERY_DIR");
            foreach (var (dpi, name) in new[] { (192.0, "signal-indicator-gallery.png"), (96.0, "signal-indicator-gallery-1x.png") })
            {
                var pixels = new PixelSize((int)Math.Ceiling(host.Bounds.Width * dpi / 96), (int)Math.Ceiling(host.Bounds.Height * dpi / 96));
                using var bitmap = new RenderTargetBitmap(pixels, new Vector(dpi, dpi));
                bitmap.Render(host);
                var path = Path.Combine(frames, name);
                using (var file = File.Create(path))
                {
                    bitmap.Save(file, new PngBitmapEncoderOptions());
                }

                Assert.True(new FileInfo(path).Length > 0, $"{name} is empty.");
                if (!string.IsNullOrEmpty(gallery))
                {
                    Directory.CreateDirectory(gallery);
                    File.Copy(path, Path.Combine(gallery, name), overwrite: true);
                }
            }

            Assert.All(indicators, cell => Assert.True(cell.Bounds.Width > 0 && cell.Bounds.Height > 0));
        }
        finally
        {
            window.Close();
            _seams.Clear();
        }

        return Task.CompletedTask;
    });

    private static SignalIndicator Indicator(SignalIndicatorSize variant, LedShape shape, GalleryState state)
    {
        MeterLevels? levels = null;
        if (state.Now is not null)
        {
            levels = new MeterLevels();
            levels.Update(Reading(state.Before), Now - (Stopwatch.Frequency / 2));
            levels.Update(Reading(state.Now), Now);
        }

        return new SignalIndicator
        {
            Variant = variant,
            LedShape = shape,
            CarriesNotes = true,
            NotesHeld = state.NotesHeld,
            LevelStyle = state.Style,
            Levels = levels,
        };
    }

    // The junction's own seam, hosted as the Device View hosts it.
    private Border Seam(GalleryState state)
    {
        var signal = new JunctionSignal();
        SetInternal(signal, nameof(JunctionSignal.Label), "After Polysynth");
        SetInternal(signal, nameof(JunctionSignal.CanCarryNotes), true);
        SetInternal(signal, nameof(JunctionSignal.CanCarryAudio), true);
        var seam = new JunctionSeam { Signal = signal, LevelStyle = state.Style, LampPlacement = JunctionLampPlacement.BelowLevel };
        _seams.Add((seam, state));
        return new Border { Classes = { "chainLink" }, Child = seam };
    }

    private static MeterReading? Reading((double Peak, double Rms)? decibels) => decibels is { } d
        ? new MeterReading(Linear(d.Peak), Linear(d.Peak - 1), Linear(d.Rms), Linear(d.Rms - 1))
        : null;

    private static float Linear(double decibels) => (float)Math.Pow(10, decibels / 20);

    private static void SetInternal(JunctionSignal signal, string property, object value) =>
        (typeof(JunctionSignal).GetProperty(property) ?? throw new InvalidOperationException($"JunctionSignal.{property} was not found.")).SetValue(signal, value);

    private sealed record GalleryState(string Name, (double Peak, double Rms)? Before, (double Peak, double Rms)? Now, bool NotesHeld, JunctionLevelStyle Style);
}
