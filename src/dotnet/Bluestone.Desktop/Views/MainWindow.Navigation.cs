using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Bluestone.Presentation;
using Bluestone.Presentation.Timeline;

namespace Bluestone.Desktop.Views;

/// <summary>
/// Horizontal navigation of the arrangement: the beat ruler's drag (zoom and scroll), ⌘/Ctrl + scroll, plain scroll over
/// the ruler, pinch, and the zoom commands (in, out, fit, selection, loop). All of them go through
/// <see cref="TimelineViewport"/>, so the position under the pointer (or the playhead) stays put and no value can leave
/// its range. The piano roll navigates the same way with its own zoom and scroll.
/// </summary>
public partial class MainWindow
{
    private void AttachNavigation(MainViewModel viewModel)
    {
        TimelineScroller.AddHandler(PointerWheelChangedEvent, OnTimelineWheel, RoutingStrategies.Tunnel);
        TimelineScroller.AddHandler(InputElement.PointerTouchPadGestureMagnifyEvent, OnTimelinePinch, RoutingStrategies.Tunnel);
        Ruler.AddHandler(PointerWheelChangedEvent, OnRulerWheel, RoutingStrategies.Tunnel);
        Ruler.ZoomLimits = MainViewModel.ArrangerZoomLimits;
        Ruler.NavigateRequested += (_, view) => ShowArrangement(view);
        viewModel.ArrangerZoomRequested += (_, request) => OnArrangerZoomRequested(request);
    }

    /// <summary>The arrangement's zoom and scroll now.</summary>
    internal TimelineViewport ArrangementViewport => new(_viewModel?.Zoom ?? 40, TimelineScroller.Offset.X);

    /// <summary>Puts the arrangement at <paramref name="view"/>'s zoom and scroll; there is always room to scroll past the end.</summary>
    internal void ShowArrangement(TimelineViewport view)
    {
        if (_viewModel is null)
        {
            return;
        }

        Timeline.MinWidth = view.ScrollX + Math.Max(0, TimelineScroller.Viewport.Width);
        _viewModel.Zoom = view.PixelsPerQuarter;
        TimelineScroller.UpdateLayout();
        TimelineScroller.Offset = new Vector(view.ScrollX, TimelineScroller.Offset.Y);
    }

    private void OnArrangerZoomRequested(TimelineZoomRequest request)
    {
        // The zoom commands act on the piano roll while it has the keyboard (fit: its clip; selection: its notes).
        if (PianoRoll.IsKeyboardFocusWithin && PianoRoll.IsEffectivelyVisible)
        {
            if (request.Factor is { } by)
            {
                PianoRoll.ZoomAround(PianoRoll.GridWidth / 2, by);
            }
            else
            {
                PianoRoll.ZoomToFit(selection: request.IsSelection);
            }

            return;
        }

        var view = ArrangementViewport;
        var width = TimelineScroller.Viewport.Width;
        if (request.Factor is { } factor)
        {
            // Around the playhead while it is in view, else around the middle.
            var playhead = Timeline.TickToX(_viewModel?.PlayheadTick ?? 0) - view.ScrollX;
            var anchor = playhead >= 0 && playhead <= width ? playhead : width / 2;
            ShowArrangement(view.ZoomedAround(anchor, factor, MainViewModel.ArrangerZoomLimits));
        }
        else if (request.To > request.From)
        {
            ShowArrangement(view.Fitted(request.From, request.To, width, MainViewModel.ArrangerZoomLimits));
        }
    }

    /// <summary>⌘/Ctrl + scroll zooms the arrangement around the pointer.</summary>
    private void OnTimelineWheel(object? sender, PointerWheelEventArgs e)
    {
        var command = CommandModifier;
        if (!e.KeyModifiers.HasFlag(command) || e.Delta.Y == 0)
        {
            return;
        }

        ZoomAround(e.GetPosition(TimelineScroller).X, Math.Pow(1.12, e.Delta.Y));
        e.Handled = true;
    }

    /// <summary>Plain scrolling over the bar ruler zooms, as in most sequencers.</summary>
    private void OnRulerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y != 0)
        {
            ZoomAround(e.GetPosition(Ruler).X, Math.Pow(1.12, e.Delta.Y));
            e.Handled = true;
        }
    }

    private void OnTimelinePinch(object? sender, PointerDeltaEventArgs e)
    {
        ZoomAround(e.GetPosition(TimelineScroller).X, 1 + e.Delta.X);
        e.Handled = true;
    }

    /// <summary>Zooms while keeping the musical position under <paramref name="viewportX"/> in place.</summary>
    private void ZoomAround(double viewportX, double factor) =>
        ShowArrangement(ArrangementViewport.ZoomedAround(viewportX, factor, MainViewModel.ArrangerZoomLimits));
}
