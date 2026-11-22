using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Bluestone.Audio;
using Bluestone.Desktop.Controls;
using Bluestone.Presentation;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Tests.Ui;

/// <summary>
/// A standalone junction seam (no longer hosted by the Device View, which draws <see cref="ChainJunction"/>): its pixels are held by
/// <see cref="SignalIndicatorTests"/>; a hosted <see cref="SignalIndicator"/> clears its clip light on a click and resyncs
/// when reattached.
/// </summary>
public sealed class SignalIndicatorSeamTests
{
    private const long Now = 1_000_000_000;

    private static readonly MethodInfo UpdateSignal = typeof(JunctionSignal).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("JunctionSignal.Update(bool, MeterReading?, long) was not found.");

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
