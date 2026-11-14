using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Bluestone.Audio;
using Bluestone.Desktop.Controls;
using Bluestone.Presentation;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Tests.Ui;

/// <summary>
/// A junction seam at the size chosen in Settings ▸ Display: Compact drawn by the seam as always (its pixels are held by
/// <see cref="SignalIndicatorTests"/>), Medium and Large a hosted <see cref="SignalIndicator"/> on the junction's own levels
/// and notes, placed in the panel's height clear of the seam's +, and gone again back at Compact.
/// </summary>
public sealed class SignalIndicatorSeamTests
{
    private const long Now = 1_000_000_000;

    private static readonly MethodInfo UpdateSignal = typeof(JunctionSignal).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("JunctionSignal.Update(bool, MeterReading?, long) was not found.");

    public static TheoryData<SignalIndicatorSize, LedShape, JunctionIndicatorPosition> Placements()
    {
        var data = new TheoryData<SignalIndicatorSize, LedShape, JunctionIndicatorPosition>();
        foreach (var size in new[] { SignalIndicatorSize.Medium, SignalIndicatorSize.Large })
        {
            foreach (var shape in Enum.GetValues<LedShape>())
            {
                foreach (var position in Enum.GetValues<JunctionIndicatorPosition>())
                {
                    data.Add(size, shape, position);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Placements))]
    public Task MediumAndLarge_HostAnIndicatorOnTheJunctionsLevels_InsideThePanelAndClearOfThePlus(SignalIndicatorSize size, LedShape shape, JunctionIndicatorPosition position) => HeadlessApp.RunAsync(() =>
    {
        var (window, link, seam, signal) = Show(size, shape, position);
        try
        {
            var indicator = Assert.IsType<SignalIndicator>(seam.Indicator);
            Assert.Same(signal.Levels, indicator.Levels);
            Assert.Equal(size, indicator.Variant);
            Assert.Equal(shape, indicator.LedShape);
            Assert.Equal(size == SignalIndicatorSize.Large, indicator.IsHitTestVisible);
            Assert.Equal("After Polysynth level", Avalonia.Automation.AutomationProperties.GetName(indicator));

            // The seam widens to the indicator and its inset; the panel's height stays as it is.
            Assert.Equal(212, link.Bounds.Height);
            Assert.Equal(indicator.Bounds.Width + 8, link.Bounds.Width);
            Assert.InRange(indicator.Bounds.Top, 1, 211);
            Assert.InRange(indicator.Bounds.Bottom, 1, 211);
            var plus = seam.Targets.Plus;
            Assert.False(plus.Intersects(indicator.Bounds), $"The + at {plus} overlaps the indicator at {indicator.Bounds}.");
            Assert.InRange(plus.Top, 0, 212 - plus.Height);

            // It shows the notes the junction carries as they come and go.
            UpdateSignal.Invoke(signal, [true, new MeterReading(0.5f, 0.45f, 0.25f, 0.22f), Now]);
            Assert.True(indicator.NotesHeld);
            UpdateSignal.Invoke(signal, [false, new MeterReading(0.5f, 0.45f, 0.25f, 0.22f), Now + 1]);
            Assert.False(indicator.NotesHeld);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task BackAtCompact_TheSeamHostsNothing_AndIsTheGapWideAgain() => HeadlessApp.RunAsync(() =>
    {
        var (window, link, seam, _) = Show(SignalIndicatorSize.Compact, LedShape.Bar, JunctionIndicatorPosition.LowerHalf);
        try
        {
            Assert.Null(seam.Indicator);
            Assert.Equal(20, link.Bounds.Width);

            seam.IndicatorSize = SignalIndicatorSize.Large;
            window.UpdateLayout();
            var indicator = Assert.IsType<SignalIndicator>(seam.Indicator);
            Assert.True(link.Bounds.Width > 20);
            var plus = seam.Targets.Plus;
            seam.LedShape = LedShape.Round;
            window.UpdateLayout();
            Assert.Same(indicator, seam.Indicator);
            Assert.Equal(LedShape.Round, indicator.LedShape);
            Assert.False(seam.Targets.Plus.Intersects(indicator.Bounds), "The + should move clear of the taller round level.");
            Assert.NotEqual(plus, seam.Targets.Plus);

            seam.IndicatorSize = SignalIndicatorSize.Compact;
            window.UpdateLayout();
            Assert.Null(seam.Indicator);
            Assert.Null(indicator.Parent);
            Assert.DoesNotContain(indicator, seam.GetVisualChildren());
            Assert.Equal(20, link.Bounds.Width);

            seam.IndicatorSize = SignalIndicatorSize.Medium;
            seam.Signal = null;
            Assert.Null(seam.Indicator);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Large_ClickingTheIndicatorClearsItsClipLight_AndDoesNotInsert() => HeadlessApp.RunAsync(() =>
    {
        var inserted = 0;
        var (window, _, seam, signal) = Show(SignalIndicatorSize.Large, LedShape.Bar, JunctionIndicatorPosition.LowerHalf);
        try
        {
            seam.InsertCommand = new RelayCommand(() => inserted++);
            UpdateSignal.Invoke(signal, [false, new MeterReading(1.2f, 1.1f, 0.6f, 0.5f), Now]);
            UpdateSignal.Invoke(signal, [false, new MeterReading(0.3f, 0.3f, 0.2f, 0.2f), Now + 1]);
            var indicator = Assert.IsType<SignalIndicator>(seam.Indicator);
            window.UpdateLayout();

            var point = indicator.TranslatePoint(new Point(indicator.Bounds.Width / 2, indicator.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);

            Assert.False(indicator.ClearClip(), "The click should already have cleared the clip light.");
            Assert.Equal(0, inserted);

            // Enter on the focused indicator is its own (clear), not the seam's insert.
            indicator.Focus(NavigationMethod.Tab);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.Equal(0, inserted);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task ReattachingTheSeam_ResyncsChangedSignalCapabilitiesAndNotes() => HeadlessApp.RunAsync(() =>
    {
        var (window, link, seam, signal) = Show(SignalIndicatorSize.Large, LedShape.Bar, JunctionIndicatorPosition.LowerHalf);
        try
        {
            var indicator = Assert.IsType<SignalIndicator>(seam.Indicator);
            Set(signal, nameof(JunctionSignal.CanCarryAudio), false);
            Set(signal, nameof(JunctionSignal.CanCarryNotes), false);
            Set(signal, nameof(JunctionSignal.NotesHeld), false);
            Assert.False(indicator.CarriesAudio);
            Assert.False(indicator.CarriesNotes);

            link.Child = null;
            Set(signal, nameof(JunctionSignal.CanCarryAudio), true);
            Set(signal, nameof(JunctionSignal.CanCarryNotes), true);
            Set(signal, nameof(JunctionSignal.NotesHeld), true);
            Assert.False(indicator.CarriesAudio);
            Assert.False(indicator.CarriesNotes);
            link.Child = seam;
            window.UpdateLayout();

            Assert.Same(indicator, seam.Indicator);
            Assert.True(indicator.CarriesAudio);
            Assert.True(indicator.CarriesNotes);
            Assert.True(indicator.NotesHeld);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    private static (Window Window, Border Link, JunctionSeam Seam, JunctionSignal Signal) Show(SignalIndicatorSize size, LedShape shape, JunctionIndicatorPosition position)
    {
        var signal = new JunctionSignal();
        Set(signal, nameof(JunctionSignal.Label), "After Polysynth");
        Set(signal, nameof(JunctionSignal.CanCarryNotes), true);
        Set(signal, nameof(JunctionSignal.CanCarryAudio), true);
        var seam = new JunctionSeam { Signal = signal, IndicatorSize = size, LedShape = shape, IndicatorPosition = position };
        var link = new Border { Classes = { "chainLink" }, Child = seam, HorizontalAlignment = HorizontalAlignment.Left };
        var host = new Border { Background = (IBrush)Avalonia.Application.Current!.FindResource("Surface.Base")!, Child = link, VerticalAlignment = VerticalAlignment.Top };
        var window = new Window { Width = 200, Height = 400, Content = host };
        window.Show();
        window.UpdateLayout();
        return (window, link, seam, signal);
    }

    private static void Set(JunctionSignal signal, string property, object value) =>
        (typeof(JunctionSignal).GetProperty(property) ?? throw new InvalidOperationException($"JunctionSignal.{property} was not found.")).SetValue(signal, value);
}
