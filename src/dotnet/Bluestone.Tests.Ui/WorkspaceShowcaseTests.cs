using Avalonia;
using Avalonia.Controls;
using Bluestone.Application.Editing;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;

namespace Bluestone.Tests.Ui;

/// <summary>
/// Representative scenes of the whole workspace, rendered at narrow, laptop, normal, and wide window sizes and saved as frames
/// (<c>frames/showcase-*.png</c> in the test output) so a change to the design can be compared with what came before. Each scene
/// also checks the few things that must hold at every size.
/// </summary>
public sealed class WorkspaceShowcaseTests
{
    private const int Beat = 480;
    private static readonly string[] RailNames = ["LeftRail", "RightRail"];

    private static readonly PluginIdentity Sine = new("bluestone-reference", "bluestone.reference", "reference.sine", "Reference Sine", "Bluestone", PluginKind.Instrument, "1.0.0");

    private static readonly PluginIdentity Gain = new("bluestone-reference", "bluestone.reference", "reference.gain", "Reference Gain", "Bluestone", PluginKind.AudioEffect, "1.0.0");

    private static DeviceDefinition BuiltIn(string name) => Signal.DeviceCatalog.BuiltIn.Definitions.Single(d => d.Name == name);

    private static ProjectSession Song()
    {
        var session = new ProjectSession();
        NoteEvent Note(int beat, int note, int length = Beat) =>
            new(new Tick(beat * Beat), new TickSpan(length), MidiChannel.FromNumber(1), new NoteNumber((byte)note), new Velocity(100));
        session.Execute(ProjectCommands.AddTrack(Track.FromEvents(TrackId.New(), "Lead", [.. Enumerable.Range(0, 16).Select(i => Note(i, 60 + ((i * 5) % 12)))])));
        session.Execute(ProjectCommands.AddTrack(Track.FromEvents(TrackId.New(), "Bass", [.. Enumerable.Range(0, 8).Select(i => Note(i * 2, 36 + (i % 3 * 5), Beat * 2))])));
        session.Execute(TrackSetup.NewTrack(Track.Create("Pad", TrackRole.Hybrid), Signal.DeviceCatalog.BuiltIn.Lookup));
        return session;
    }

    /// <summary>Opens the song with Bass played by external hardware (the loopback port "Synth" on channel 2) and Lead selected.</summary>
    private static async Task<AppUnderTest> OpenSongAsync(Func<ProjectSession, PluginDeviceHost>? plugins = null)
    {
        var app = await AppUnderTest.OpenAsync(Song(), plugins);
        var bass = app.Session.Project.Sequence.Tracks[1].Id;
        app.Session.Execute(ProjectCommands.SetTrackOutput(bass, new TrackOutput
        {
            Endpoint = new EndpointReference(LoopbackMidiProvider.ProviderId, app.Port.OutputId.Value, "Synth"),
            Channel = MidiChannel.FromNumber(2),
        }));
        app.Settle();
        return app;
    }

    private static PluginHostManager Plugins()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "plugin-data", Guid.NewGuid().ToString("N"));
        return new PluginHostManager(new PluginHostOptions { DataDirectory = directory, RequestTimeout = TimeSpan.FromSeconds(15) });
    }

    [Theory]
    [InlineData(1100, 720)]
    [InlineData(1280, 800)]
    [InlineData(1440, 900)]
    [InlineData(1920, 1080)]
    public Task TheWorkspace_HeadersShareTheSameHeightAndBaseline(double width, double height) => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenSongAsync();
        app.Window.Width = width;
        app.Window.Height = height;
        app.ViewModel.Workspace.Browser.ShowCommand.Execute(null);
        app.Settle();

        var trackHeader = app.Find<Border>("TrackViewHeader");
        var inspectorHeader = AppUnderTest.Within<Border>(app.Find<Panel>("InspectorDock"), b => b.Classes.Contains("dockHeader"));
        var browserHeader = AppUnderTest.Within<Border>(app.Find<Panel>("BrowserDock"), b => b.Classes.Contains("dockHeader"));
        var trackHeaderOrigin = app.InWindow(trackHeader, new Point(0, 0));
        var inspectorHeaderOrigin = app.InWindow(inspectorHeader, new Point(0, 0));
        var browserHeaderOrigin = app.InWindow(browserHeader, new Point(0, 0));

        Assert.Equal(30, trackHeader.Bounds.Height);
        Assert.Equal(trackHeader.Bounds.Height, inspectorHeader.Bounds.Height);
        Assert.Equal(trackHeader.Bounds.Height, browserHeader.Bounds.Height);
        Assert.Equal(trackHeaderOrigin.Y, inspectorHeaderOrigin.Y);
        Assert.Equal(trackHeaderOrigin.Y, browserHeaderOrigin.Y);
        Assert.True(File.Exists(app.SaveFrame($"showcase-headers-{(int)width}")));
    });

    [Fact]
    public Task TheWorkspace_RailIconsAlignWithTheHeaderBand() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenSongAsync();
        app.Window.Width = 1440;
        app.Window.Height = 900;
        app.ViewModel.Workspace.Inspector.CollapseCommand.Execute(null);
        app.ViewModel.Workspace.Browser.CollapseCommand.Execute(null);
        app.Settle();

        var header = app.Find<Border>("TrackViewHeader");
        var headerCenterY = app.InWindow(header, new Point(0, header.Bounds.Height / 2)).Y;
        foreach (var railName in RailNames)
        {
            var rail = app.Find<Border>(railName);
            var button = AppUnderTest.Within<Button>(rail, b => b.Classes.Contains("railItem"));
            var icon = AppUnderTest.Within<PathIcon>(button, _ => true);
            var iconCenterY = app.InWindow(icon, new Point(0, icon.Bounds.Height / 2)).Y;
            Assert.InRange(Math.Abs(headerCenterY - iconCenterY), 0, 1);
        }

        Assert.True(File.Exists(app.SaveFrame("showcase-rails-header-alignment")));
    });

    [Theory]
    [InlineData(1100, 720, "narrow")]
    [InlineData(1280, 800, "laptop")]
    [InlineData(1440, 900, "normal")]
    [InlineData(1920, 1080, "wide")]
    public Task TheWorkspace_WithAPopulatedChain(double width, double height, string size) => HeadlessApp.RunAsync(async () =>
    {
        await using var plugins = Plugins();
        await using var app = await OpenSongAsync(s => new PluginDeviceHost(plugins, s, [Sine, Gain]));
        app.Window.Width = width;
        app.Window.Height = height;
        app.ViewModel.Select(app.ViewModel.Tracks[0]);
        app.ViewModel.Workspace.Browser.ShowCommand.Execute(null);
        app.Settle();
        var view = app.ViewModel.DeviceView!;
        view.InsertAt(0, BuiltIn("Arpeggiator"));
        view.InsertAt(1, PluginDeviceHost.DefinitionOf(Sine));
        view.InsertAt(2, BuiltIn("Transpose"));
        app.Settle();
        await AppUnderTest.WaitUntilAsync(() => view.Devices.Count == 3 && view.Devices[1].Parameters.Count > 0, "the chain and the plugin's parameters");
        await AppUnderTest.WaitUntilAsync(() => app.ViewModel.Tracks[0].StatusLine == Sine.DisplayName, "the track to name its instrument");
        view.SelectedDevice = view.Devices[1];
        await app.SettleTransitionsAsync();

        Assert.True(File.Exists(app.SaveFrame($"showcase-{size}-chain")));
        Assert.True(app.Find<ContentControl>("DockedDeviceView").IsEffectivelyVisible);
    });

    [Theory]
    [InlineData(1280, 800, "laptop")]
    [InlineData(1440, 900, "normal")]
    public Task TheDeviceView_ShowsTheSignalAtEveryJunction(double width, double height, string size) => HeadlessApp.RunAsync(async () =>
    {
        await using var plugins = Plugins();
        await using var app = await OpenSongAsync(s => new PluginDeviceHost(plugins, s, [Sine, Gain]));
        app.Window.Width = width;
        app.Window.Height = height;
        app.ViewModel.Select(app.ViewModel.Tracks[0]);
        app.Settle();
        var view = app.ViewModel.DeviceView!;
        view.InsertAt(0, BuiltIn("Arpeggiator"));
        view.InsertAt(1, PluginDeviceHost.DefinitionOf(Sine));
        view.InsertAt(2, PluginDeviceHost.DefinitionOf(Gain));
        app.Settle();
        await AppUnderTest.WaitUntilAsync(() => view.Devices.Count == 3 && view.Devices[1].Parameters.Count > 0, "the chain and the plugin's parameters");
        await app.SettleTransitionsAsync();

        // Idle: seams are quiet hairlines; only the lamp where notes can be is outlined.
        app.ViewModel.OnFrame();
        await app.SettleTransitionsAsync();
        Assert.True(File.Exists(app.SaveFrame($"showcase-{size}-junctions-idle")));
        Assert.True(File.Exists(SaveDeviceView(app, $"showcase-{size}-junctions-idle-closeup")));

        // Playing: notes held on the track light every lamp they reach; the synth and the effect after it have levels.
        var levels = new Dictionary<Guid, Bluestone.Audio.MeterReading>
        {
            [view.Devices[1].Output.Key] = new(0.62f, 0.55f, 0.32f, 0.28f),
            [view.Devices[2].Output.Key] = new(0.93f, 0.88f, 0.5f, 0.46f),
        };
        app.ViewModel.JunctionLevelSource = key => levels.TryGetValue(key, out var reading) ? reading : null;
        var lead = app.ViewModel.Tracks[0].Id;
        app.Playback.Live.Post(lead, Midi.Wire.ChannelMessage.NoteOn(MidiChannel.FromIndex(0), new NoteNumber(60), new Velocity(100)), TimeSpan.Zero);
        app.ViewModel.OnFrame();
        app.ViewModel.OnFrame();
        await app.SettleTransitionsAsync();

        Assert.True(view.Input.NotesHeld);
        Assert.True(view.Devices[2].Output.Levels.IsActive);
        Assert.True(File.Exists(app.SaveFrame($"showcase-{size}-junctions-playing")));
        Assert.True(File.Exists(SaveDeviceView(app, $"showcase-{size}-junctions-playing-closeup")));
    });

    /// <summary>
    /// Design options for the seams, rendered side by side for choosing (frames/junction-gallery.html): where the note lamp
    /// sits, and colours for the MIDI icon. Not a check of behaviour; it keeps the options reproducible.
    /// </summary>
    [Fact]
    public Task TheJunctionDesignOptions_AreRenderedForComparison() => HeadlessApp.RunAsync(async () =>
    {
        await using var plugins = Plugins();
        await using var app = await OpenSongAsync(s => new PluginDeviceHost(plugins, s, [Sine, Gain]));
        app.Window.Width = 1440;
        app.Window.Height = 900;
        app.ViewModel.Select(app.ViewModel.Tracks[0]);
        app.Settle();
        var view = app.ViewModel.DeviceView!;
        view.InsertAt(0, BuiltIn("Arpeggiator"));
        view.InsertAt(1, PluginDeviceHost.DefinitionOf(Sine));
        view.InsertAt(2, PluginDeviceHost.DefinitionOf(Gain));
        app.Settle();
        await AppUnderTest.WaitUntilAsync(() => view.Devices.Count == 3 && view.Devices[1].Parameters.Count > 0, "the chain and the plugin's parameters");

        // The synth at a healthy level; the effect after it pushed into clipping, so every colour of a scheme shows.
        var levels = new Dictionary<Guid, Bluestone.Audio.MeterReading>
        {
            [view.Devices[1].Output.Key] = new(0.5f, 0.45f, 0.25f, 0.22f),
            [view.Devices[2].Output.Key] = new(1.05f, 0.97f, 0.62f, 0.58f),
        };
        app.ViewModel.JunctionLevelSource = key => levels.TryGetValue(key, out var reading) ? reading : null;
        app.Playback.Live.Post(app.ViewModel.Tracks[0].Id, Midi.Wire.ChannelMessage.NoteOn(MidiChannel.FromIndex(0), new NoteNumber(60), new Velocity(100)), TimeSpan.Zero);

        var options = new (string Name, string Caption, Desktop.Controls.JunctionLampPlacement Lamp, string? Note, bool Playing)[]
        {
            ("segments", "Line-segment level, glowing MIDI icon under it (sky blue)", Desktop.Controls.JunctionLampPlacement.BelowLevel, null, true),
            ("segments-idle", "Idle", Desktop.Controls.JunctionLampPlacement.BelowLevel, null, false),
            ("note-above", "MIDI icon over the level", Desktop.Controls.JunctionLampPlacement.AboveLevel, null, true),
        };

        var html = new System.Text.StringBuilder("<!doctype html><meta charset=utf-8><title>Junction design options</title><style>body{background:#111113;color:#D9D9DC;font:14px -apple-system,sans-serif;margin:24px}h2{font-weight:600;font-size:15px;margin:28px 0 8px}img{width:860px;border:1px solid #2E2E31;border-radius:6px}</style><h1>Junction design options</h1>");
        foreach (var option in options)
        {
            foreach (var seam in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(app.Find<ContentControl>("DockedDeviceView")).OfType<Desktop.Controls.JunctionSeam>())
            {
                seam.LampPlacement = option.Lamp;
                if (option.Note is { } note)
                {
                    seam.LampColor = Avalonia.Media.Color.Parse(note);
                }
                else
                {
                    seam.ClearValue(Desktop.Controls.JunctionSeam.LampColorProperty);
                }
            }

            app.ViewModel.JunctionLevelSource = option.Playing ? key => levels.TryGetValue(key, out var reading) ? reading : null : _ => null;
            if (option.Playing)
            {
                app.Playback.Live.Post(app.ViewModel.Tracks[0].Id, Midi.Wire.ChannelMessage.NoteOn(MidiChannel.FromIndex(0), new NoteNumber(60), new Velocity(100)), TimeSpan.Zero);
            }
            else
            {
                app.Playback.Live.ReleaseAll();
            }

            for (var frame = 0; frame < 20; frame++)
            {
                app.ViewModel.OnFrame();
                await Task.Delay(20);
                app.Settle();
            }

            var file = SaveDeviceView(app, $"junction-{option.Name}");
            html.Append(System.Globalization.CultureInfo.InvariantCulture, $"<h2>{option.Caption}</h2><img src=\"{Path.GetFileName(file)}\">");
        }

        // The chosen design at each indicator position, set the way Settings ▸ Display sets it.
        foreach (var seam in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(app.Find<ContentControl>("DockedDeviceView")).OfType<Desktop.Controls.JunctionSeam>())
        {
            seam.ClearValue(Desktop.Controls.JunctionSeam.LampPlacementProperty);
            seam.ClearValue(Desktop.Controls.JunctionSeam.LampColorProperty);
        }

        app.ViewModel.JunctionLevelSource = key => levels.TryGetValue(key, out var reading) ? reading : null;
        app.Playback.Live.Post(app.ViewModel.Tracks[0].Id, Midi.Wire.ChannelMessage.NoteOn(MidiChannel.FromIndex(0), new NoteNumber(60), new Velocity(100)), TimeSpan.Zero);
        foreach (var position in Presentation.DisplayViewModel.Positions)
        {
            app.ViewModel.Display.IndicatorPosition = position.Position;
            for (var frame = 0; frame < 20; frame++)
            {
                app.ViewModel.OnFrame();
                await Task.Delay(20);
                app.Settle();
            }

            Assert.All(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(app.Find<ContentControl>("DockedDeviceView")).OfType<Desktop.Controls.JunctionSeam>(), s => Assert.Equal(position.Position, s.IndicatorPosition));
            var file = SaveDeviceView(app, $"junction-position-{position.Position}");
            html.Append(System.Globalization.CultureInfo.InvariantCulture, $"<h2>Position: {position.Name}</h2><img src=\"{Path.GetFileName(file)}\">");
        }

        var gallery = Path.Combine(AppContext.BaseDirectory, "frames", "junction-gallery.html");
        File.WriteAllText(gallery, html.ToString());
        Assert.True(File.Exists(gallery));
    });

    // The docked Device View alone, at twice the size, for looking at the seams closely.
    private static string SaveDeviceView(AppUnderTest app, string name)
    {
        var control = app.Find<ContentControl>("DockedDeviceView");
        var size = new PixelSize((int)(control.Bounds.Width * 2), (int)(control.Bounds.Height * 2));
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(192, 192));
        bitmap.Render(control);
        var directory = Path.Combine(AppContext.BaseDirectory, "frames");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}.png");
        bitmap.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        return path;
    }

    [Theory]
    [InlineData(1280, 800, "laptop")]
    [InlineData(1440, 900, "normal")]
    public Task TheWorkspace_AfterAddingANewTrack(double width, double height, string size) => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenSongAsync();
        app.Window.Width = width;
        app.Window.Height = height;
        app.ViewModel.Workspace.Browser.ShowCommand.Execute(null);
        app.ViewModel.AddTrackCommand.Execute(null);
        await app.SettleTransitionsAsync();

        Assert.True(File.Exists(app.SaveFrame($"showcase-{size}-new-track")));
    });

    [Fact]
    public Task TheWorkspace_WithSeveralTracksSelected() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await OpenSongAsync();
        app.Window.Width = 1440;
        app.Window.Height = 900;
        app.ViewModel.Workspace.Browser.ShowCommand.Execute(null);
        app.ViewModel.Select(app.ViewModel.Tracks[0]);
        app.ViewModel.SelectAdjacent(1, extend: true);
        await app.SettleTransitionsAsync();

        Assert.True(File.Exists(app.SaveFrame("showcase-normal-multiple")));
    });
}
