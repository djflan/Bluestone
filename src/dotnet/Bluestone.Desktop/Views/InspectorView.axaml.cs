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
        viewModel.Connections.ShowRequested += (_, _) =>
        {
            viewModel.IsInspectorVisible = true;
            ConnectionsSection.IsExpanded = true;
            ConnectionsSection.BringIntoView();
        };
    }
}
