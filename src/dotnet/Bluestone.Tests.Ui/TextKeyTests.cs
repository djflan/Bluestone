using System.Runtime.InteropServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Bluestone.Desktop.Controls;

namespace Bluestone.Tests.Ui;

/// <summary>A text key reads as unavailable whenever it cannot be used, including when its command says it cannot run.</summary>
public sealed class TextKeyTests
{
    [Fact]
    public Task Key_DimsItsLabel_WhenItsCommandCannotRun_AndBrightensWhenItCan() => HeadlessApp.RunAsync(() =>
    {
        var command = new Toggle();
        var key = new TextKey { Text = "Remove stop", Command = command, Width = 120, Height = 28 };
        var window = new Window { Width = 160, Height = 60, Content = new Border { Padding = new Thickness(16), Child = key, HorizontalAlignment = HorizontalAlignment.Left } };
        window.Show();

        Assert.True(key.IsEnabled);
        Assert.False(key.IsEffectivelyEnabled);
        var dimmed = BrightestLabelPixel(window, key);

        command.Set(true);
        Assert.True(key.IsEffectivelyEnabled);
        var lit = BrightestLabelPixel(window, key);

        Assert.True(lit > dimmed + 60, $"Expected the label to brighten when the command can run: dimmed {dimmed}, lit {lit}.");
        window.Close();
        return Task.CompletedTask;
    });

    private static int BrightestLabelPixel(Window window, TextKey key)
    {
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        var size = frame.PixelSize;
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            frame.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        // Inside the key, clear of its top highlight and edge, where only the label is bright.
        var origin = key.TranslatePoint(default, window) ?? throw new InvalidOperationException("The key is not in the window.");
        var brightest = 0;
        for (var y = (int)origin.Y + 4; y < (int)(origin.Y + key.Bounds.Height) - 4; y++)
        {
            for (var x = (int)origin.X + 4; x < (int)(origin.X + key.Bounds.Width) - 4; x++)
            {
                var offset = (y * stride) + (x * 4);
                brightest = Math.Max(brightest, pixels[offset] + pixels[offset + 1] + pixels[offset + 2]);
            }
        }

        return brightest;
    }

    private sealed class Toggle : ICommand
    {
        private bool _canRun;

        public event EventHandler? CanExecuteChanged;

        public void Set(bool canRun)
        {
            _canRun = canRun;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }

        public bool CanExecute(object? parameter) => _canRun;

        public void Execute(object? parameter)
        {
        }
    }
}
