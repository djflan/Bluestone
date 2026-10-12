using Avalonia.Input;
using Bluestone.Application.Sessions;
using Bluestone.Desktop.Views;
using Bluestone.Application.Plugins;

namespace Bluestone.Tests.Ui;

/// <summary>Keys from a plugin editor that sends its keys to Bluestone: mapped from HID usages and handled like typed keys.</summary>
public sealed class EditorKeyForwardingTests
{
    [Fact]
    public void HidUsages_MapToTheKeysAndPhysicalKeysBluestoneReads()
    {
        Assert.Equal((Key.A, PhysicalKey.A), MainWindow.ToAvalonia(4));
        Assert.Equal((Key.Z, PhysicalKey.Z), MainWindow.ToAvalonia(29));
        Assert.Equal((Key.D1, PhysicalKey.Digit1), MainWindow.ToAvalonia(30));
        Assert.Equal((Key.D9, PhysicalKey.Digit9), MainWindow.ToAvalonia(38));
        Assert.Equal((Key.D0, PhysicalKey.Digit0), MainWindow.ToAvalonia(39));
        Assert.Equal((Key.Space, PhysicalKey.Space), MainWindow.ToAvalonia(44));
        Assert.Equal((Key.OemTilde, PhysicalKey.Backquote), MainWindow.ToAvalonia(53));
        Assert.Equal((Key.F12, PhysicalKey.F12), MainWindow.ToAvalonia(69));
        Assert.Equal((Key.Up, PhysicalKey.ArrowUp), MainWindow.ToAvalonia(82));
        Assert.Null(MainWindow.ToAvalonia(57));
        for (var usage = 4; usage <= 29; usage++)
        {
            var letter = (char)('A' + usage - 4);
            Assert.Equal(Enum.Parse<Key>(letter.ToString()), MainWindow.ToAvalonia(usage)!.Value.Key);
        }
    }

    [Fact]
    public Task AForwardedKey_ReachesBluestonesShortcuts() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync(new ProjectSession());
        Assert.False(app.Session.Project.Loop.IsEnabled);

        // L (HID usage 15) pressed and released in an editor whose keys go to Bluestone.
        app.Window.RaiseEditorKey(new PluginEditorKey(15, 0, Down: true, Repeat: false));
        app.Window.RaiseEditorKey(new PluginEditorKey(15, 0, Down: false, Repeat: false));
        app.Settle();

        Assert.True(app.Session.Project.Loop.IsEnabled);
    });
}
