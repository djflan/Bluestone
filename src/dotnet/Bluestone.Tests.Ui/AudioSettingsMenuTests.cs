using Avalonia.Controls;
using Avalonia.VisualTree;
using Bluestone.Application.Audio;
using Bluestone.Desktop.Views;
using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>Settings ▸ Audio can be opened on macOS, where the window's own File menu is hidden.</summary>
public sealed class AudioSettingsMenuTests
{
    [Fact]
    public Task AudioSettingsInTheMacOSWindowMenu_OpensTheAudioSettings() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(nativeMenuBar: true);
        var directory = Path.Combine(Path.GetTempPath(), "bluestone-audio-menu", Guid.NewGuid().ToString("N"));
        using var audio = new AudioPlayback(app.Playback, app.Session, _ => null);
        app.Window.AttachAudio(new AudioViewModel(audio, new AudioSettingsStore(Path.Combine(directory, "audio-settings.json")), new ImmediateDispatcher(), () => []));
        var window = NativeMenu.GetMenu(app.Window)!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Window");
        var item = window.Menu!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Audio Settings…");

        item.Command!.Execute(null);
        app.Settle();

        Assert.True(app.Window.Settings.IsOpen);
        Assert.Equal(SettingsPage.Audio, app.Window.Settings.SelectedCategory?.Page);
        Assert.Single(app.Window.GetVisualDescendants().OfType<AudioSettingsPage>(), p => p.IsEffectivelyVisible);
        app.Window.Settings.Close();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    });

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();
    }
}
