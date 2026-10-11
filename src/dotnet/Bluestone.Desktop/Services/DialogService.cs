using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Services;

/// <summary>File pickers and confirmation dialogs for the view models.</summary>
internal sealed class DialogService(Window owner) : IUserInteraction
{
    public async Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [.. filters.Select(ToFileType)],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, FileFilter filter)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = filter.Extensions[0],
            FileTypeChoices = [ToFileType(filter)],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive) =>
        await ShowAsync(title, message, [(confirmLabel, "confirm", destructive ? "danger" : "primary"), ("Cancel", "cancel", "ghost")]) == "confirm";

    public async Task<UnsavedChangesChoice> AskToSaveChangesAsync(string projectName) =>
        await ShowAsync(
            "Save changes?",
            $"\"{projectName}\" has unsaved changes.",
            [("Save", "save", "primary"), ("Don't Save", "discard", "danger"), ("Cancel", "cancel", "ghost")]) switch
        {
            "save" => UnsavedChangesChoice.Save,
            "discard" => UnsavedChangesChoice.Discard,
            _ => UnsavedChangesChoice.Cancel,
        };

    private static FilePickerFileType ToFileType(FileFilter filter) => new(filter.Name)
    {
        Patterns = [.. filter.Extensions.Select(e => "*." + e)],
    };

    private async Task<string?> ShowAsync(string title, string message, (string Label, string Result, string Style)[] buttons)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, result, style) in buttons)
        {
            var button = new Button { Content = label, IsDefault = result is "confirm" or "save", IsCancel = result == "cancel" };
            button.Classes.Add(style);
            button.Click += (_, _) => dialog.Close(result);
            row.Children.Add(button);
        }

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = title, Classes = { "title" } },
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "secondary" } },
                row,
            },
        };

        return await dialog.ShowDialog<string?>(owner);
    }
}
