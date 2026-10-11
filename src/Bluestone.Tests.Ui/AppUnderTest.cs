using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Desktop;
using Bluestone.Desktop.Services;
using Bluestone.Desktop.Views;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Presentation;
using Bluestone.Profiles;

namespace Bluestone.Tests.Ui;

/// <summary>
/// Bluestone's own <see cref="App"/> (theme, styles, fonts) on Avalonia's headless platform, rendered with Skia so frames
/// can be captured. Every test runs on the one Avalonia UI thread through <see cref="RunAsync"/>.
/// </summary>
public static class HeadlessApp
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessApp)));

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();

    /// <summary>Runs <paramref name="test"/> on the UI thread and waits for it, including its awaits.</summary>
    public static Task RunAsync(Func<Task> test) =>
        Session.Value.Dispatch(
            async () =>
            {
                await test();
                return true;
            },
            TestContext.Current.CancellationToken);
}

/// <summary>
/// The real main window, attached to a view model composed as the app composes it, but with a loopback MIDI port
/// instead of the platform's and no playback thread. Dispose to close the window and release everything.
/// </summary>
internal sealed class AppUnderTest : IAsyncDisposable
{
    private AppUnderTest(MainWindow window, MainViewModel viewModel, ProjectSession session, PlaybackController playback, LoopbackPort port)
    {
        Window = window;
        ViewModel = viewModel;
        Session = session;
        Playback = playback;
        Port = port;
    }

    public MainWindow Window { get; }

    public MainViewModel ViewModel { get; }

    public ProjectSession Session { get; }

    public PlaybackController Playback { get; }

    /// <summary>A loopback output named "Synth" that tracks can be routed to.</summary>
    public LoopbackPort Port { get; }

    /// <summary>
    /// Opens the window. It uses the in-window menu and key bindings (as on Windows and Linux) unless
    /// <paramref name="nativeMenuBar"/>: the headless platform has no system menu bar to deliver the macOS menu's shortcuts.
    /// </summary>
    public static async Task<AppUnderTest> OpenAsync(
        ProjectSession? session = null,
        Func<ProjectSession, PluginDeviceHost>? plugins = null,
        ProfileCatalog? profiles = null,
        PluginsViewModel? pluginLibrary = null,
        bool nativeMenuBar = false)
    {
        session ??= new ProjectSession();
        var clock = SystemMonotonicClock.Instance;
        var provider = new LoopbackMidiProvider(clock);
        var port = provider.CreatePort("Synth", "synth");
        var endpoints = new EndpointDirectory([provider]);
        var playback = new PlaybackController(session, endpoints, profiles ?? ProfileCatalog.Empty, clock, startThread: false);
        var window = new MainWindow { Width = 1440, Height = 1600, UseNativeMenuBar = nativeMenuBar };
        var viewModel = new MainViewModel(session, playback, endpoints, new NoDialogs(), new AvaloniaDispatcher(), plugins: plugins?.Invoke(session)) { Plugins = pluginLibrary };
        window.Attach(viewModel);
        window.Show();
        await viewModel.InitializeAsync();
        var app = new AppUnderTest(window, viewModel, session, playback, port);
        app.Settle();
        return app;
    }

    /// <summary>Runs pending UI work and lays the window out.</summary>
    public void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Window.UpdateLayout();
    }

    /// <summary>The named control in the window.</summary>
    public T Find<T>(string name)
        where T : Control =>
        Window.FindControl<T>(name) ?? throw new InvalidOperationException($"No control named {name}.");

    /// <summary>The first control of type <typeparamref name="T"/> inside <paramref name="scope"/> that matches.</summary>
    public static T Within<T>(Control scope, Func<T, bool> match)
        where T : Control =>
        scope.GetLogicalDescendants().OfType<T>().FirstOrDefault(match)
        ?? throw new InvalidOperationException($"No matching {typeof(T).Name} in {scope.Name ?? scope.GetType().Name}.");

    /// <summary>
    /// Clicks <paramref name="control"/> with the mouse, as a musician would: scrolled into view, then pressed and released.
    /// The click goes to the control's own top level: the main window, another window, or a popup.
    /// </summary>
    public void Click(Control control)
    {
        var root = TopLevel.GetTopLevel(control) ?? throw new InvalidOperationException($"{control} is not in a window.");
        control.BringIntoView();
        root.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)
            ?? throw new InvalidOperationException($"{control} is not in its window.");
        Assert.True(control.IsEffectivelyVisible, $"{control} should be visible to be clicked.");
        root.MouseDown(centre, MouseButton.Left);
        root.MouseUp(centre, MouseButton.Left);
        Settle();
    }

    /// <summary>The platform's command key (⌘ on macOS, Ctrl elsewhere), as the window reads it.</summary>
    public static RawInputModifiers Command =>
        (Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control) switch
        {
            KeyModifiers.Meta => RawInputModifiers.Meta,
            _ => RawInputModifiers.Control,
        };

    /// <summary><paramref name="local"/> in <paramref name="control"/>'s coordinates, in the window's.</summary>
    public Point InWindow(Control control, Point local) =>
        control.TranslatePoint(local, Window) ?? throw new InvalidOperationException($"{control} is not in the window.");

    /// <summary>Presses and releases the left button at <paramref name="local"/> in <paramref name="control"/>.</summary>
    public void ClickAt(Control control, Point local, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var at = InWindow(control, local);
        Window.MouseDown(at, MouseButton.Left, modifiers);
        Window.MouseUp(at, MouseButton.Left, modifiers);
        Settle();
    }

    /// <summary>Two clicks in quick succession at <paramref name="local"/> in <paramref name="control"/>: a double-click.</summary>
    public void DoubleClickAt(Control control, Point local)
    {
        var at = InWindow(control, local);
        Window.MouseDown(at, MouseButton.Left);
        Window.MouseUp(at, MouseButton.Left);
        Window.MouseDown(at, MouseButton.Left);
        Window.MouseUp(at, MouseButton.Left);
        Settle();
    }

    /// <summary>Presses at <paramref name="from"/>, moves to <paramref name="to"/> in a few steps, and releases there.</summary>
    public void Drag(Control control, Point from, Point to, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var start = InWindow(control, from);
        var end = InWindow(control, to);
        Window.MouseMove(start, modifiers);
        Window.MouseDown(start, MouseButton.Left, modifiers);
        const int Steps = 4;
        for (var step = 1; step <= Steps; step++)
        {
            var at = start + ((end - start) * step / Steps);
            Window.MouseMove(at, modifiers | RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
        }

        Window.MouseUp(end, MouseButton.Left, modifiers);
        Settle();
    }

    /// <summary>Presses and releases a key, as typed on a keyboard.</summary>
    public void Press(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Window.KeyPress(key, modifiers, PhysicalKey.None, null);
        Window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Settle();
    }

    /// <summary>Types text into the focused control.</summary>
    public void Type(string text)
    {
        Window.KeyTextInput(text);
        Settle();
    }

    /// <summary>
    /// Opens <paramref name="box"/> with a click and clicks the first item matching <paramref name="item"/> in its drop-down,
    /// which is a popup with its own top level.
    /// </summary>
    public void Choose(ComboBox box, Func<object?, bool> item)
    {
        Click(box);
        Assert.True(box.IsDropDownOpen, $"{AutomationProperties.GetName(box)} should open when clicked.");
        var index = box.Items.IndexOf(box.Items.First(item));
        box.ScrollIntoView(index);
        Settle();
        Click(box.ContainerFromIndex(index) ?? throw new InvalidOperationException($"Item {index} of {AutomationProperties.GetName(box)} is not realized."));
        Assert.False(box.IsDropDownOpen, $"{AutomationProperties.GetName(box)} should close after a choice.");
    }

    /// <summary>The combo box with the accessible name <paramref name="name"/> inside <paramref name="scope"/>.</summary>
    public static ComboBox ComboNamed(Control scope, string name) =>
        Within<ComboBox>(scope, c => AutomationProperties.GetName(c) == name);

    /// <summary>The section (expander) with the header <paramref name="header"/>.</summary>
    public Expander Section(string header) =>
        Within<Expander>(Window, e => Equals(e.Header, header));

    /// <summary>Waits, letting the UI thread run, until <paramref name="condition"/> holds.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for {what}.");
            }

            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Renders the main window, or <paramref name="window"/>, and saves the frame as a PNG in the test output, for looking at.</summary>
    public string SaveFrame(string name, TopLevel? window = null)
    {
        window ??= Window;
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        var directory = Path.Combine(AppContext.BaseDirectory, "frames");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}.png");
        frame.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        Window.Close();
        await ViewModel.DisposeAsync();
    }

    private sealed class NoDialogs : IUserInteraction
    {
        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, FileFilter filter) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive) => Task.FromResult(true);

        public Task<UnsavedChangesChoice> AskToSaveChangesAsync(string projectName) => Task.FromResult(UnsavedChangesChoice.Discard);
    }
}
