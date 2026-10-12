using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Views;

/// <summary>
/// A detached workspace view (ADR 0036): the same view, hosted in its own window. Closing the window docks the view again;
/// the window's bounds are remembered per display setup, so returning to a known monitor layout puts it back where it was.
/// </summary>
public sealed class ViewWindow : Window
{
    private bool _closingFromLayout;

    public ViewWindow(WorkspaceView view, Control content)
    {
        ArgumentNullException.ThrowIfNull(view);
        View = view;
        DataContext = view;
        Title = $"{view.Title} — Bluestone";
        MinWidth = 320;
        MinHeight = 200;
        Width = view.Kind == WorkspaceViewKind.Browser ? 300 : 900;
        Height = view.Kind == WorkspaceViewKind.Browser ? 600 : 300;
        Background = Brush("Surface.Base");
        ShowInTaskbar = false;
        Content = content;
        view.PropertyChanged += OnViewPropertyChanged;
    }

    public WorkspaceView View { get; }

    /// <summary>Closes the window because the layout no longer has the view detached (it was docked or collapsed elsewhere).</summary>
    public void CloseFromLayout()
    {
        _closingFromLayout = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // Only closing this window itself docks the view. When the main window (its owner) closes, or the app quits (⌘Q, the
        // app menu's Quit, logging out), the window is asked too, first; the view must stay detached so the layout saved
        // when the main window has closed keeps it, and so a quit cancelled at the unsaved-changes prompt changes nothing.
        if (!_closingFromLayout && !e.Cancel && View.IsDetached && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            // Closing a detached window puts the view back where it lives in the main window.
            View.DockCommand.Execute(null);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // The view outlives its window: a view detached and docked again and again must not keep every closed window alive.
        View.PropertyChanged -= OnViewPropertyChanged;
    }

    private void OnViewPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceView.Title))
        {
            Title = $"{View.Title} — Bluestone";
        }
    }

    private static IBrush? Brush(string key) => Bluestone.Desktop.Theme.ThemeResources.Find(key) as IBrush;

    /// <summary>A detached view's content with a slim header that can dock it again.</summary>
    public static Control Frame(WorkspaceView view, Control body)
    {
        ArgumentNullException.ThrowIfNull(view);
        var dock = new Button
        {
            Classes = { "tool", "small" },
            Content = new PathIcon { Classes = { "stroke" }, Data = Bluestone.Desktop.Theme.ThemeResources.Find("Icon.Collapse") as Geometry },
            Command = view.DockCommand,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(dock, "Dock in the main window");
        Avalonia.Automation.AutomationProperties.SetName(dock, "Dock");
        if (view.Kind == WorkspaceViewKind.DeviceView)
        {
            // The Device View has its own header with these options.
            return body;
        }

        var header = new Border { Classes = { "dockHeader" }, Child = new Grid { Children = { new TextBlock { Classes = { "dockTitle" }, Text = view.Title.ToUpperInvariant() }, dock } } };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(header);
        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        return grid;
    }
}
