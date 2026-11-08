using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Views;

/// <summary>
/// The source picker's popup content. Opening it lists the project as it is now and puts the cursor in the search box;
/// choosing a source (a click, or Enter for the first match) closes it.
/// </summary>
public partial class SourcePickerView : UserControl
{
    private SourcePickerViewModel? _subscribed;

    public SourcePickerView()
    {
        InitializeComponent();
        QueryBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is SourcePickerViewModel picker)
            {
                picker.ChooseFirstCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Start();
        Dispatcher.UIThread.Post(() => QueryBox.Focus(), DispatcherPriority.Loaded);
    }

    // A flyout's content can get its data context after it is shown: the list is built whichever comes last.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (VisualRoot is not null)
        {
            Start();
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        Watch(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void Start()
    {
        var picker = DataContext as SourcePickerViewModel;
        if (picker is not null && !ReferenceEquals(picker, _subscribed))
        {
            picker.Open();
        }

        Watch(picker);
    }

    private void Watch(SourcePickerViewModel? picker)
    {
        if (_subscribed is not null)
        {
            _subscribed.Chosen -= OnChosen;
        }

        _subscribed = picker;
        if (picker is not null)
        {
            picker.Chosen += OnChosen;
        }
    }

    private void OnChosen(object? sender, EventArgs e)
    {
        if (this.FindLogicalAncestorOfType<Popup>() is { } popup)
        {
            popup.Close();
        }
    }
}
