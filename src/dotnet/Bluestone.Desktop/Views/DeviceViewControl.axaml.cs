using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Views;

/// <summary>
/// The Device View's interactions: drops from the Browser insert at the marked gap, a panel header drags to reorder, any
/// click makes the view the active one, and the horizontal scroll position is kept in the view's own state.
/// </summary>
public partial class DeviceViewControl : UserControl
{
    private const double DragThreshold = 4;
    private PointerPressedEventArgs? _press;
    private DevicePanelViewModel? _pressed;
    private Point _pressAt;
    private bool _restoringScroll;

    public DeviceViewControl()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnAnyPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, _) => ClearPress(), RoutingStrategies.Tunnel);
        ChainArea.AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        ChainArea.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        ChainArea.AddHandler(DragDrop.DragLeaveEvent, (_, _) => HideDropFeedback());
        ChainArea.AddHandler(DragDrop.DropEvent, OnDrop);
        ChainScroller.ScrollChanged += (_, _) =>
        {
            if (!_restoringScroll && ViewModel is { } vm)
            {
                vm.ScrollOffset = ChainScroller.Offset.X;
            }
        };
    }

    private DeviceViewViewModel? ViewModel => DataContext as DeviceViewViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        RestoreScroll();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RestoreScroll();
    }

    /// <summary>The gap a drop at <paramref name="x"/> (in <see cref="ChainArea"/> coordinates) would land in, and where to mark it.</summary>
    internal (int Gap, double MarkerX) GapAt(double x)
    {
        var count = ViewModel?.Devices.Count ?? 0;
        var centers = new double[count];
        var links = new double[count + 1];
        for (var i = 0; i < count; i++)
        {
            if (Chain.ContainerFromIndex(i) is not Control container || !IsValidBounds(container.Bounds)
                || container.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("chainLink")) is not { } link
                || container.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("devicePanel")) is not { } panel
                || !IsValidBounds(link.Bounds) || !IsValidBounds(panel.Bounds)
                || link.TranslatePoint(new Point(link.Bounds.Width / 2, link.Bounds.Height / 2), ChainArea) is not { } linkCenter
                || container.TranslatePoint(default, ChainArea) is not { } containerOrigin
                || !IsValidPoint(linkCenter) || !IsValidPoint(containerOrigin))
            {
                return (-1, 0);
            }

            links[i] = linkCenter.X;
            centers[i] = containerOrigin.X + link.Bounds.Width + ((container.Bounds.Width - link.Bounds.Width) / 2);
        }

        if (!IsValidBounds(TrailLink.Bounds)
            || TrailLink.TranslatePoint(new Point(TrailLink.Bounds.Width / 2, TrailLink.Bounds.Height / 2), ChainArea) is not { } trailCenter
            || !IsValidPoint(trailCenter)
            || !double.IsFinite(x))
        {
            return (-1, 0);
        }

        links[count] = trailCenter.X;
        var gap = DeviceViewViewModel.GapAt(centers, x);
        return (gap, links[gap]);
    }

    private void RestoreScroll()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var offset = vm.ScrollOffset;
        Dispatcher.UIThread.Post(() =>
        {
            _restoringScroll = true;
            ChainScroller.Offset = new Vector(offset, 0);
            _restoringScroll = false;
        }, DispatcherPriority.Loaded);
    }

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        vm.Activate();
        if (e.Source is Visual pressed && pressed is not Controls.ChainJunction && !pressed.GetVisualAncestors().Any(a => a is Controls.ChainJunction)
            && TopLevel.GetTopLevel(this)?.FocusManager is { } focus && focus.GetFocusedElement() is Controls.ChainJunction)
        {
            // A press elsewhere in the view takes focus off a junction, so it no longer holds Delete for its clip.
            // A focusable control pressed after this takes focus itself; anything else leaves Delete to the window.
            focus.Focus(null, NavigationMethod.Pointer);
        }

        // A seam belongs to the gap, not the device after it: pressing it (including a hosted indicator) does not select that device.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is not Control source
            || source is Controls.ChainJunction
            || source.GetVisualAncestors().Any(ancestor => ancestor is Controls.ChainJunction))
        {
            return;
        }

        if (source.DataContext is DevicePanelViewModel panel)
        {
            panel.SelectCommand.Execute(null);
            if (source.Classes.Contains("grip"))
            {
                _press = e;
                _pressed = panel;
                _pressAt = e.GetPosition(this);
            }
        }
    }

    private async void OnAnyPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || _pressed is not { } panel || ViewModel is not { } vm)
        {
            return;
        }

        var at = e.GetPosition(this);
        if (Math.Abs(at.X - _pressAt.X) < DragThreshold && Math.Abs(at.Y - _pressAt.Y) < DragThreshold)
        {
            return;
        }

        ClearPress();
        await DragDrop.DoDragDropAsync(press, ChainDrag.Move(panel.Id, vm).ToTransfer(), DragDropEffects.Move);
        HideDropFeedback();
    }

    private void ClearPress()
    {
        _press = null;
        _pressed = null;
    }

    private void OnFitWindowClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is ViewWindow { View.Kind: WorkspaceViewKind.DeviceView, View.IsDetached: true } window)
        {
            window.FitDeviceChain(this);
        }
    }

    internal bool TryGetFitContentSize(out Size size)
    {
        size = default;
        if (Content is not Grid layout || !IsValidBounds(ChainArea.Bounds) || !IsValidBounds(ChainRow.Bounds))
        {
            return false;
        }

        var cardHeight = ChainRow.GetVisualDescendants().OfType<Control>()
            .Where(control => control.Classes.Contains("devicePanel") || control.Classes.Contains("chainNode"))
            .Select(control => control.Bounds.Height)
            .Where(height => height > 0 && double.IsFinite(height))
            .DefaultIfEmpty(0)
            .Max();
        if (cardHeight <= 0)
        {
            return false;
        }

        var headerHeight = layout.Children.FirstOrDefault(child => Grid.GetRow(child) == 0)?.Bounds.Height ?? 0;
        var verticalMargins = ChainRow.Margin.Top + ChainRow.Margin.Bottom + ChainScroller.Margin.Top + ChainScroller.Margin.Bottom;
        // DesiredSize, not Bounds: the row stretches to a wider viewport, which would stop Fit from shrinking the window.
        var width = ChainRow.DesiredSize.Width + ChainScroller.Margin.Left + ChainScroller.Margin.Right;
        var height = headerHeight + cardHeight + verticalMargins;
        if (width <= 0 || height <= 0 || !double.IsFinite(width) || !double.IsFinite(height))
        {
            return false;
        }

        size = new Size(width, height);
        return true;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (ChainDrag.From(e) is not { } drag || ViewModel is not { HasChain: true } vm)
        {
            // Not something a chain takes (files go to the window), or there is no chain here: no marker, no guess.
            HideDropFeedback();
            if (ChainDrag.From(e) is not null)
            {
                e.DragEffects = DragDropEffects.None;
                e.Handled = true;
            }

            return;
        }

        var effect = EffectOf(vm, drag);
        if (effect == DragDropEffects.None)
        {
            // A device from another chain: devices do not move between chains, so there is no gap to offer.
            HideDropFeedback();
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (vm.IsEmpty)
        {
            // The only place it can go: light the empty slot rather than mark a gap.
            InsertionMarker.IsVisible = false;
            EmptySlot.Classes.Set("dropTarget", true);
            e.DragEffects = effect;
            e.Handled = true;
            return;
        }

        var target = TargetAt(e.GetPosition(ChainArea).X, drag);
        if (target.Gap < 0)
        {
            HideDropFeedback();
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (target.Replace is { } replaced)
        {
            // Over the middle of a panel: the whole panel is the target, and says what will happen.
            InsertionMarker.IsVisible = false;
            ReplaceDetail.Text = $"{replaced.Name} with {drag.Definition!.Name}";
            ReplaceOverlay.Width = target.Right - target.Left;
            ReplaceOverlay.Height = target.Height;
            Canvas.SetLeft(ReplaceOverlay, target.Left);
            Canvas.SetTop(ReplaceOverlay, target.Top);
            ReplaceOverlay.IsVisible = true;
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        ReplaceOverlay.IsVisible = false;
        Canvas.SetLeft(InsertionMarker, target.MarkerX - (InsertionMarker.Width / 2));
        InsertionMarker.Height = target.Height;
        Canvas.SetTop(InsertionMarker, target.Top);
        InsertionMarker.IsVisible = true;
        e.DragEffects = effect;
        e.Handled = true;
    }

    /// <summary>
    /// What a drop at <paramref name="x"/> does: replace a device (a Browser item over the middle half of a panel) or go
    /// into a gap (anything else, and every move of a chain device, which never replaces).
    /// </summary>
    internal (DevicePanelViewModel? Replace, double Left, double Right, double Top, double Height, int Gap, double MarkerX) TargetAt(double x, ChainDrag drag)
    {
        var (gap, markerX) = GapAt(x);
        if (gap < 0 || ViewModel is not { } vm)
        {
            return (null, 0, 0, 0, 0, -1, 0);
        }

        if (vm.IsEmpty)
        {
            return (null, 0, 0, 0, 0, gap, markerX);
        }

        var panels = new List<Rect>();
        for (var i = 0; i < vm.Devices.Count; i++)
        {
            if (Chain.ContainerFromIndex(i) is not Control container
                || container.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("devicePanel")) is not { } panel
                || !IsValidBounds(panel.Bounds)
                || panel.TranslatePoint(default, ChainArea) is not { } origin
                || !IsValidPoint(origin))
            {
                return (null, 0, 0, 0, 0, -1, 0);
            }

            panels.Add(new Rect(origin, panel.Bounds.Size));
        }

        if (!double.IsFinite(x) || panels.Count == 0)
        {
            return (null, 0, 0, 0, 0, -1, 0);
        }

        var (replace, index) = DeviceViewViewModel.DropTargetAt(panels.Select(panel => (panel.Left, panel.Right)).ToList(), x, canReplace: drag.Definition is not null);
        var markerPanel = panels[Math.Min(gap, panels.Count - 1)];
        return replace
            ? (vm.Devices[index], panels[index].Left, panels[index].Right, panels[index].Top, panels[index].Height, gap, markerX)
            : (null, 0, 0, markerPanel.Top, markerPanel.Height, gap, markerX);
    }

    private static bool IsValidBounds(Rect bounds) =>
        bounds.Width > 0 && bounds.Height > 0
        && double.IsFinite(bounds.X) && double.IsFinite(bounds.Y)
        && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height);

    private static bool IsValidPoint(Point point) => double.IsFinite(point.X) && double.IsFinite(point.Y);

    private void HideDropFeedback()
    {
        InsertionMarker.IsVisible = false;
        ReplaceOverlay.IsVisible = false;
        EmptySlot.Classes.Set("dropTarget", false);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        HideDropFeedback();
        if (ChainDrag.From(e) is not { } drag || ViewModel is not { HasChain: true } vm)
        {
            return;
        }

        e.DragEffects = EffectOf(vm, drag);
        e.Handled = true;
        if (e.DragEffects == DragDropEffects.None)
        {
            return;
        }

        var target = TargetAt(e.GetPosition(ChainArea).X, drag);
        if (target.Gap < 0)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        // Change the chain after the platform's drop callback returns, not inside the native drag session: the change rebuilds
        // panels (and can start a plugin), which must not happen while the session still holds the drag source and target.
        if (target.Replace is { } replaced && drag.Definition is { } definition)
        {
            Dispatcher.UIThread.Post(async () => await vm.ReplaceAsync(replaced.Id, definition));
            return;
        }

        Dispatcher.UIThread.Post(() => Drop(vm, drag, target.Gap));
    }

    /// <summary>
    /// What a drop of <paramref name="drag"/> on <paramref name="target"/> does: a definition is copied in; a device moves when it
    /// is in the chain <paramref name="target"/> shows (from this view or another view of the same chain); otherwise nothing.
    /// </summary>
    internal static DragDropEffects EffectOf(DeviceViewViewModel target, ChainDrag drag)
    {
        if (drag.Definition is not null)
        {
            return DragDropEffects.Copy;
        }

        return drag.Device is { } device && target.Devices.Any(d => d.Id == device) ? DragDropEffects.Move : DragDropEffects.None;
    }

    /// <summary>Applies a drop at <paramref name="gap"/>: inserts a definition, or moves a device (between views of the same chain too).</summary>
    internal static bool Drop(DeviceViewViewModel target, ChainDrag drag, int gap)
    {
        if (drag.Definition is { } definition)
        {
            return target.InsertAt(gap, definition);
        }

        return drag.Device is { } device && target.MoveToGap(device, gap);
    }
}
