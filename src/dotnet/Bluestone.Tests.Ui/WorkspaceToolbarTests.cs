using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using SkiaSharp;

namespace Bluestone.Tests.Ui;

/// <summary>The main workspace tools use the selected outlined treatment without changing what the controls do.</summary>
public sealed class WorkspaceToolbarTests
{
    private static readonly string[] CommandTools = ["Browser", "Mixer", "Editors"];

    [Theory]
    [InlineData(1040, 700, "compact")]
    [InlineData(1440, 900, "normal")]
    public Task WorkspaceTools_HaveGapsAndIndividualOutlinedFaces_AtBothSizes(double width, double height, string size) =>
        HeadlessApp.RunAsync(async () =>
        {
            await using var app = await AppUnderTest.OpenAsync(nativeMenuBar: true);
            app.Window.Width = width;
            app.Window.Height = height;
            ShowReferenceState(app);
            MoveAway(app);

            var tools = new[] { Tool(app, "Inspector"), Tool(app, "Browser"), Tool(app, "Mixer"), Tool(app, "Editors") };
            Assert.All(tools, tool => Assert.True(tool.IsEffectivelyVisible));
            Assert.All(tools, tool =>
            {
                Assert.Equal(28, tool.Bounds.Width);
                Assert.Equal(22, tool.Bounds.Height);
            });
            Assert.False(tools[0].IsChecked is true);
            Assert.All(tools.Skip(1), tool => Assert.True(tool.IsChecked is true));
            var workspaceFrame = app.SaveFrame($"toolbar-A-workspace-{size}");
            using var workspaceImage = SKBitmap.Decode(workspaceFrame);
            Assert.NotNull(workspaceImage);

            var gap = Assert.IsType<double>(app.Window.FindResource("Space.XS"));
            for (var i = 1; i < tools.Length; i++)
            {
                var previousFace = Face(tools[i - 1]);
                var currentFace = Face(tools[i]);
                var previous = app.InWindow(previousFace, default);
                var current = app.InWindow(currentFace, default);
                Assert.InRange(current.X - previous.X - previousFace.Bounds.Width, gap - 0.01, gap + 0.01);
                var middleOfGap = new Point(previous.X + previousFace.Bounds.Width + gap / 2, previous.Y + previousFace.Bounds.Height / 2);
                var gapPixel = PixelAt(app, workspaceImage, middleOfGap);
                Assert.False(Near(gapPixel, ResourceColor(app, "Accent")));
                Assert.False(Near(gapPixel, ResourceColor(app, "State.SelectedSubtle")));
            }

            foreach (var tool in tools.Skip(1))
            {
                AssertCheckedFace(app, tool);
                AssertRenderedFace(app, workspaceImage, tool);
            }

            Assert.NotEqual(ResourceColor(app, "State.SelectedSubtle"), ColorOf(Face(tools[0]).Background));

            var settings = app.Find<ToggleButton>("SettingsButton");
            app.Click(settings);
            MoveAway(app);
            Assert.True(app.Window.Settings.IsOpen);
            Assert.True(settings.IsChecked is true);
            AssertCheckedFace(app, settings);
            var settingsFrame = app.SaveFrame($"toolbar-A-settings-{size}");
            using var settingsImage = SKBitmap.Decode(settingsFrame);
            Assert.NotNull(settingsImage);
            AssertRenderedFace(app, settingsImage, settings);
        });

    [Fact]
    public Task CheckedWorkspaceTool_UsesTheSubduedFaceOutlineAndLightIcon() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(nativeMenuBar: true);
        ShowReferenceState(app);
        MoveAway(app);
        AssertCheckedFace(app, Tool(app, "Browser"));
    });

    [Fact]
    public Task WorkspaceTools_RetainHoverPressedFocusDisabledAndToggleBehavior() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(nativeMenuBar: true);
        app.Window.Width = 1440;
        app.Window.Height = 900;
        ShowReferenceState(app);
        MoveAway(app);

        var inspector = Tool(app, "Inspector");
        var at = app.InWindow(inspector, new Point(inspector.Bounds.Width / 2, inspector.Bounds.Height / 2));
        app.Window.MouseMove(at);
        app.Settle();
        Assert.Equal(ResourceColor(app, "State.Hover"), ColorOf(Face(inspector).Background));
        Assert.True(File.Exists(app.SaveFrame("toolbar-A-hover-unchecked")));

        app.Window.MouseDown(at, MouseButton.Left);
        app.Settle();
        Assert.Equal(ResourceColor(app, "State.Pressed"), ColorOf(Face(inspector).Background));
        Assert.True(File.Exists(app.SaveFrame("toolbar-A-pressed-unchecked")));
        app.Window.MouseUp(at, MouseButton.Left);
        app.Settle();
        Assert.True(app.ViewModel.IsInspectorVisible);
        Assert.True(inspector.IsChecked is true);
        Assert.Equal(ResourceColor(app, "State.Selected"), ColorOf(Face(inspector).Background));
        Assert.True(File.Exists(app.SaveFrame("toolbar-A-hover-checked")));

        app.Window.MouseDown(at, MouseButton.Left);
        app.Settle();
        Assert.Equal(ResourceColor(app, "State.Pressed"), ColorOf(Face(inspector).Background));
        Assert.Equal(ResourceColor(app, "Accent"), ColorOf(Face(inspector).BorderBrush));
        app.Window.MouseUp(at, MouseButton.Left);
        app.Settle();
        Assert.False(inspector.IsChecked is true);
        app.Window.MouseDown(at, MouseButton.Left);
        app.Window.MouseUp(at, MouseButton.Left);
        app.Settle();
        Assert.True(inspector.IsChecked is true);

        MoveAway(app);
        Assert.True(Tool(app, "Browser").Focus(NavigationMethod.Pointer));
        Assert.True(inspector.Focus(NavigationMethod.Tab));
        app.Settle();
        Assert.Contains(":focus-visible", inspector.Classes);
        Assert.Null(inspector.FocusAdorner);
        Assert.Equal(ResourceColor(app, "State.Focus"), ColorOf(Face(inspector).BorderBrush));
        Assert.Equal(new Thickness(2), Face(inspector).BorderThickness);
        Assert.True(File.Exists(app.SaveFrame("toolbar-A-keyboard-focus")));
        app.Press(Key.I);
        Assert.False(app.ViewModel.IsInspectorVisible);
        Assert.False(inspector.IsChecked is true);
        Assert.Contains(":focus-visible", inspector.Classes);
        Assert.Equal(ResourceColor(app, "State.Focus"), ColorOf(Face(inspector).BorderBrush));
        Assert.Equal(new Thickness(2), Face(inspector).BorderThickness);
        app.Press(Key.I);
        Assert.True(app.ViewModel.IsInspectorVisible);
        Assert.True(inspector.IsChecked is true);

        inspector.IsEnabled = false;
        app.Settle();
        Assert.False(inspector.IsEffectivelyEnabled);
        Assert.Equal(ResourceColor(app, "Text.Disabled"), ColorOf(inspector.Foreground));
        Assert.Equal(ResourceColor(app, "Text.Disabled"), ColorOf(Assert.Single(inspector.GetVisualDescendants().OfType<PathIcon>()).Foreground));
        Assert.Equal(ResourceColor(app, "Line.BorderStrong"), ColorOf(Face(inspector).BorderBrush));
        Assert.True(File.Exists(app.SaveFrame("toolbar-A-disabled-checked")));
        app.Click(inspector);
        Assert.True(app.ViewModel.IsInspectorVisible);
        Assert.True(inspector.IsChecked is true);
        inspector.IsEnabled = true;
        app.Settle();

        foreach (var name in CommandTools)
        {
            var tool = Tool(app, name);
            var before = tool.IsChecked is true;
            app.Click(tool);
            Assert.Equal(!before, tool.IsChecked is true);
            app.Click(tool);
            Assert.Equal(before, tool.IsChecked is true);
        }

        var settings = app.Find<ToggleButton>("SettingsButton");
        app.Click(settings);
        Assert.True(app.Window.Settings.IsOpen);
        Assert.True(settings.IsChecked is true);
        app.Click(settings);
        Assert.False(app.Window.Settings.IsOpen);
        Assert.False(settings.IsChecked is true);
    });

    private static void ShowReferenceState(AppUnderTest app)
    {
        app.ViewModel.IsInspectorVisible = false;
        app.ViewModel.IsEditorVisible = true;
        app.ViewModel.Workspace.Browser.ShowCommand.Execute(null);
        app.ViewModel.Workspace.Mixer.ShowCommand.Execute(null);
        app.Settle();
    }

    private static void MoveAway(AppUnderTest app)
    {
        app.Window.MouseMove(new Point(app.Window.Width - 2, app.Window.Height - 2));
        app.Settle();
    }

    private static ToggleButton Tool(AppUnderTest app, string name) =>
        AppUnderTest.Within<ToggleButton>(app.Find<StackPanel>("WorkspaceTools"),
            button => AutomationProperties.GetName(button) == name);

    private static ContentPresenter Face(ToggleButton button) =>
        Assert.Single(button.GetVisualDescendants().OfType<ContentPresenter>());

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static Color ResourceColor(AppUnderTest app, string name) =>
        ColorOf(Assert.IsAssignableFrom<IBrush>(app.Window.FindResource(name)));

    private static void AssertCheckedFace(AppUnderTest app, ToggleButton tool)
    {
        var face = Face(tool);
        Assert.Equal(ResourceColor(app, "State.SelectedSubtle"), ColorOf(face.Background));
        Assert.Equal(ResourceColor(app, "Accent"), ColorOf(face.BorderBrush));
        Assert.Equal(new Thickness(1), face.BorderThickness);
        var icon = Assert.Single(tool.GetVisualDescendants().OfType<PathIcon>());
        Assert.Equal(ResourceColor(app, "Text.Primary"), ColorOf(icon.Foreground));
    }

    private static void AssertRenderedFace(AppUnderTest app, SKBitmap image, ToggleButton tool)
    {
        var face = Face(tool);
        var origin = app.InWindow(face, default);
        var topEdge = new Point(origin.X + (face.Bounds.Width / 2), origin.Y + 0.5);
        var interior = new Point(origin.X + 2.5, origin.Y + 5.5);
        Assert.True(Near(PixelAt(app, image, topEdge), ResourceColor(app, "Accent")), "The rendered face needs an aqua outline.");
        Assert.True(Near(PixelAt(app, image, interior), ResourceColor(app, "State.SelectedSubtle")), "The rendered face needs a subdued fill.");

        var lightIcon = ResourceColor(app, "Text.Primary");
        var lightPixels = 0;
        for (var y = 2; y < face.Bounds.Height - 2; y++)
        {
            for (var x = 2; x < face.Bounds.Width - 2; x++)
            {
                if (Near(PixelAt(app, image, new Point(origin.X + x, origin.Y + y)), lightIcon))
                {
                    lightPixels++;
                }
            }
        }

        Assert.True(lightPixels >= 4, "The rendered icon needs a legible light foreground.");
    }

    private static SKColor PixelAt(AppUnderTest app, SKBitmap image, Point point)
    {
        var x = (int)(point.X * image.Width / app.Window.ClientSize.Width);
        var y = (int)(point.Y * image.Height / app.Window.ClientSize.Height);
        return image.GetPixel(x, y);
    }

    private static bool Near(SKColor actual, Color expected) =>
        Math.Abs(actual.Red - expected.R) <= 8
        && Math.Abs(actual.Green - expected.G) <= 8
        && Math.Abs(actual.Blue - expected.B) <= 8;
}
