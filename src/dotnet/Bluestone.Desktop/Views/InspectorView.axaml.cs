using Avalonia.Controls;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Views;

/// <summary>The inspector's content. All behaviour is in <see cref="SelectionViewModel"/> and the section view models.</summary>
public partial class InspectorView : UserControl
{
    public InspectorView() => InitializeComponent();

    /// <summary>Connects the controls that are not plain bindings: the take and count-in choices, and requests to show connections.</summary>
    internal void Attach(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        TakeModeBox.SelectedIndex = viewModel.IsReplaceRecording ? 1 : 0;
        TakeModeBox.SelectionChanged += (_, _) => viewModel.IsReplaceRecording = TakeModeBox.SelectedIndex == 1;
        CountInBox.SelectedIndex = viewModel.CountInBars - 1;
        CountInBox.SelectionChanged += (_, _) => viewModel.CountInBars = Math.Max(1, CountInBox.SelectedIndex + 1);
        ClipLoopStartBox.LostFocus += (_, _) => Commit(ClipLoopStartBox, viewModel.ClipLoop.StartText, viewModel.ClipLoop.CommitStart);
        ClipLoopLengthBox.LostFocus += (_, _) => Commit(ClipLoopLengthBox, viewModel.ClipLoop.LengthText, viewModel.ClipLoop.CommitLength);
        viewModel.Connections.ShowRequested += (_, _) =>
        {
            viewModel.IsInspectorVisible = true;
            ConnectionsSection.IsExpanded = true;
            ConnectionsSection.BringIntoView();
        };
    }

    // A field left as it was, or emptied, changes nothing; text that is not a value is put back.
    private static void Commit(TextBox box, string shown, Func<string, bool> commit)
    {
        var text = box.Text?.Trim() ?? string.Empty;
        if (text.Length == 0 || text == shown || !commit(text))
        {
            box.Text = shown;
        }
    }
}
