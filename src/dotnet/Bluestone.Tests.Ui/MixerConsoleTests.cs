using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bluestone.Audio;
using Bluestone.Desktop.Controls;
using Bluestone.Desktop.Views;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>
/// The mixer console opened from the File menu, in its own window on the headless platform: a strip per channel and the
/// master, a fader dragged with the mouse and undone with the keyboard, sends added from the strip, and meters fed from
/// a meter source.
/// </summary>
public sealed class MixerConsoleTests
{
    private static MixerWindow OpenMixer(AppUnderTest app)
    {
        app.Click(app.Find<DropDownButton>("FileMenuButton"));
        var flyout = Assert.IsType<MenuFlyout>(app.Find<DropDownButton>("FileMenuButton").Flyout);
        app.Click(flyout.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Mixer…")));
        var mixer = app.Window.OwnedWindows.OfType<MixerWindow>().SingleOrDefault() ?? throw new InvalidOperationException("The mixer did not open.");
        Settle(mixer);
        return mixer;
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static Point Centre(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window) ?? throw new InvalidOperationException($"{control} is not in the mixer.");

    [Fact]
    public Task AFaderDraggedWithTheMouse_ChangesTheChannel_AndOneUndoRestoresIt() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var mixer = OpenMixer(app);
        app.Click(AppUnderTest.Within<Button>(mixer, b => Equals(b.Content, "New channel")));
        Settle(mixer);
        var fader = AppUnderTest.Within<Slider>(mixer, s => AutomationProperties.GetName(s) == "Channel fader");
        var thumb = fader.GetVisualDescendants().OfType<Thumb>().First();
        Assert.Equal(0, fader.Value);

        // Press on the thumb and drag it down in a few steps, as a hand would.
        var from = Centre(thumb, mixer);
        mixer.MouseMove(from);
        mixer.MouseDown(from, MouseButton.Left);
        for (var step = 1; step <= 4; step++)
        {
            mixer.MouseMove(from + new Point(0, 15 * step), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
        }

        mixer.MouseUp(from + new Point(0, 60), MouseButton.Left);
        Settle(mixer);

        var dragged = app.Session.Project.Mixer.Channels[0].GainDecibels;
        Assert.True(dragged < -1, $"The fader should have moved the gain down, not to {dragged} dB.");
        Assert.Equal(dragged, fader.Value, 6);
        Assert.Equal("Change Mixer Channel", app.Session.History.UndoLabel);
        app.SaveFrame("mixer-console", mixer);

        mixer.KeyPress(Key.Z, AppUnderTest.Command, PhysicalKey.None, null);
        mixer.KeyRelease(Key.Z, AppUnderTest.Command, PhysicalKey.None, null);
        Settle(mixer);

        Assert.Equal(0, app.Session.Project.Mixer.Channels[0].GainDecibels);
        Assert.Equal(0, fader.Value);
        Assert.Equal("Add Mixer Channel", app.Session.History.UndoLabel);
        mixer.Close();
    });

    [Fact]
    public Task ASendAddedFromTheStrip_ShowsItsLevel_AndTheMetersFollowTheirSource() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        var mixer = OpenMixer(app);
        var newChannel = AppUnderTest.Within<Button>(mixer, b => Equals(b.Content, "New channel"));
        app.Click(newChannel);
        app.Click(newChannel);
        Settle(mixer);

        app.Choose(AppUnderTest.ComboNamed(mixer, "Add send"), o => o is MixerOutputOption { Name: "Channel 2" });
        Settle(mixer);

        var send = Assert.Single(app.Session.Project.Mixer.Channels[0].Sends);
        Assert.Equal((app.Session.Project.Mixer.Channels[1].Id, 0.0, false), (send.Target, send.LevelDecibels, send.PreFader));
        Assert.NotNull(AppUnderTest.Within<Slider>(mixer, s => AutomationProperties.GetName(s) == "Send level"));
        Assert.Equal("Return", app.ViewModel.Mixer.Channels[1].Role);

        app.ViewModel.Mixer.MeterSource = id => id is null ? new MeterReading(0.5f, 0.5f, 0.25f, 0.25f) : new MeterReading(1.5f, 0.1f, 0.05f, 0.05f);
        await Task.Delay(120, TestContext.Current.CancellationToken);
        Settle(mixer);

        Assert.True(app.ViewModel.Mixer.MasterMeter.IsActive);
        Assert.Equal(-6.02, app.ViewModel.Mixer.MasterMeter.PeakLeft, 2);
        Assert.True(app.ViewModel.Mixer.Channels[0].Meter.Clipped);
        // Once the level is back under full scale, a click on the meter clears its clip light.
        app.ViewModel.Mixer.MeterSource = _ => new MeterReading(0.1f, 0.1f, 0.05f, 0.05f);
        var meter = AppUnderTest.Within<LevelMeter>(mixer, m => AutomationProperties.GetName(m) == "Channel meter");
        app.Click(meter);
        Assert.False(app.ViewModel.Mixer.Channels[0].Meter.Clipped);
        app.SaveFrame("mixer-console-sends", mixer);
        mixer.Close();
    });

    [Fact]
    public Task MixerInTheMacOSWindowMenu_OpensOneConsole() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(nativeMenuBar: true);
        var window = NativeMenu.GetMenu(app.Window)!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Window");
        var item = window.Menu!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Mixer");

        item.Command!.Execute(null);
        item.Command!.Execute(null);
        app.Settle();

        var mixer = Assert.Single(app.Window.OwnedWindows.OfType<MixerWindow>());
        Assert.Same(app.ViewModel.Mixer, mixer.DataContext);
        mixer.Close();
    });
}
