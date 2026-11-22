using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Bluestone.Desktop.Controls;
using Bluestone.Desktop.Theme;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Views;

/// <summary>
/// Settings ▸ Display's gradient editor: a recessed well of LED cells drawn in the signal gradient, as a meter draws them,
/// with a draggable, focusable handle under it for each stop. Dragging a handle (or Left/Right on it: 1%, Shift for 5%)
/// changes that stop's position. All state is in <see cref="GradientEditorViewModel"/>, its data context. A drag previews as it
/// goes and saves once, on release; each key press saves. The editor's colours are its own brushes, not the shared caches,
/// so editing never grows them.
/// </summary>
public sealed class SignalGradientEditor : Panel
{
    /// <summary>The bar's inset from the editor's left and right edges: half a handle, so the end handles fit.</summary>
    public const double Inset = 8;

    private const double BarHeight = 22;
    private const double HandleTop = BarHeight + 2;
    private const double HandleWidth = 14;
    private const double HandleHeight = 20;
    private const double CellPitch = 7;
    private const double CellGap = 2;
    private const double WellPad = 4;

    private readonly Bar _bar;
    private GradientEditorViewModel? _display;
    private int _dragIndex = -1;

    public SignalGradientEditor()
    {
        AutomationProperties.SetName(this, "Signal gradient");
        _bar = new Bar(this);
        Children.Add(_bar);
    }

    /// <summary>The handles, in stop order.</summary>
    public IReadOnlyList<StopHandle> Handles => [.. Children.OfType<StopHandle>()];

    private StopHandle HandleAt(int index) => (StopHandle)Children[index + 1];

    private int HandleCount => Children.Count - 1;

    /// <summary>Where <paramref name="position"/> (0 to 1) falls across the bar, in the editor's coordinates.</summary>
    public double XAt(double position) => Inset + (position * Math.Max(0, Bounds.Width - (2 * Inset)));

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // End a drag on the old display first, so its preview is committed or released, not stranded.
        FinishDrag();
        if (_display is not null)
        {
            _display.PropertyChanged -= OnDisplayChanged;
        }

        _display = DataContext as GradientEditorViewModel;
        if (_display is not null)
        {
            _display.PropertyChanged += OnDisplayChanged;
        }

        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        FinishDrag();
    }

    private void OnDisplayChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GradientEditorViewModel.Gradient) or nameof(GradientEditorViewModel.SelectedGradientStopIndex))
        {
            Sync();
        }
    }

    // One handle per stop, reused while the count holds so that focus and a drag's capture survive a move.
    private void Sync()
    {
        var stops = _display?.Gradient.Stops ?? [];
        var focusedIndex = Children.OfType<StopHandle>().FirstOrDefault(h => h.IsFocused)?.Index ?? -1;
        while (HandleCount > stops.Length)
        {
            Children.RemoveAt(Children.Count - 1);
        }

        while (HandleCount < stops.Length)
        {
            Children.Add(new StopHandle(this));
        }

        var selected = _display is null ? -1 : Math.Clamp(_display.SelectedGradientStopIndex, 0, stops.Length - 1);
        for (var i = 0; i < stops.Length; i++)
        {
            HandleAt(i).Update(i, stops[i], i == selected);
        }

        // A keyboard move that reorders the stops keeps the focus on the stop that moved.
        if (focusedIndex >= 0 && selected >= 0 && focusedIndex != selected && selected < HandleCount)
        {
            HandleAt(selected).Focus(NavigationMethod.Directional);
        }

        InvalidateArrange();
        _bar.InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var handle in Children.OfType<StopHandle>())
        {
            handle.Measure(new Size(HandleWidth, HandleHeight));
        }

        _bar.Measure(new Size(availableSize.Width, BarHeight));

        return new Size(double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width, HandleTop + HandleHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _bar.Arrange(new Rect(0, 0, finalSize.Width, BarHeight));
        var width = Math.Max(0, finalSize.Width - (2 * Inset));
        foreach (var handle in Children.OfType<StopHandle>())
        {
            var x = Inset + (handle.Position * width);
            handle.Arrange(new Rect(Math.Round(x - (HandleWidth / 2)), HandleTop, HandleWidth, HandleHeight));
        }

        return finalSize;
    }

    internal void BeginDrag(StopHandle handle, PointerPressedEventArgs e)
    {
        if (_display is null)
        {
            return;
        }

        _dragIndex = handle.Index;
        _display.BeginGradientPreview();
        _display.SelectedGradientStopIndex = handle.Index;
        handle.Focus(NavigationMethod.Pointer);
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    internal void Drag(PointerEventArgs e)
    {
        if (_display is null || _dragIndex < 0)
        {
            return;
        }

        var width = Math.Max(1, Bounds.Width - (2 * Inset));
        var position = Math.Clamp((e.GetPosition(this).X - Inset) / width, 0, 1);
        _display.MoveAndSelectGradientStop(_dragIndex, Math.Round(position * 1000) / 1000);
        _dragIndex = _display.SelectedGradientStopIndex;
        e.Handled = true;
    }

    internal void EndDrag(PointerReleasedEventArgs e)
    {
        if (_dragIndex >= 0)
        {
            e.Handled = true;
            e.Pointer.Capture(null);
            FinishDrag();
        }
    }

    /// <summary>Ends a drag however it ends (release, lost capture, leaving the window), saving its result once.</summary>
    internal void FinishDrag()
    {
        if (_dragIndex >= 0)
        {
            _dragIndex = -1;
            _display?.EndGradientPreview();
        }
    }

    internal void Nudge(StopHandle handle, double delta)
    {
        if (_display is not null)
        {
            _display.NudgeGradientStop(handle.Index, delta);
        }
    }

    internal void MoveTo(StopHandle handle, double position)
    {
        if (_display is not null)
        {
            _display.MoveAndSelectGradientStop(handle.Index, Math.Round(Math.Clamp(position, 0, 1) * 1000) / 1000);
        }
    }

    internal void Select(StopHandle handle)
    {
        if (_display is not null)
        {
            _display.SelectedGradientStopIndex = handle.Index;
        }
    }

    /// <summary>The well of LED cells, lit in the gradient as a meter draws it, with a hairline at each stop.</summary>
    private sealed class Bar(SignalGradientEditor editor) : Control
    {
        private IBrush[] _cells = [];
        private SignalGradient? _cellsGradient;

        public override void Render(DrawingContext context)
        {
            if (editor._display is not { } display)
            {
                return;
            }

            ConsolePaint.Well(context, new Rect(0, 0, Bounds.Width, BarHeight), 3);
            var width = Math.Max(0, Bounds.Width - (2 * Inset));
            var count = Math.Max(1, (int)Math.Floor((width + CellGap) / CellPitch));
            var cell = ((width + CellGap) / count) - CellGap;
            var gradient = display.Gradient;
            var cells = CellBrushes(gradient, count);
            for (var i = 0; i < count; i++)
            {
                var rect = new Rect(Inset + (i * (cell + CellGap)), WellPad, cell, BarHeight - (2 * WellPad));
                context.DrawRectangle(cells[i], null, new RoundedRect(rect, 1.5));
                ConsolePaint.HLine(context, ConsolePaint.Alpha(ConsolePaint.White, 0.18), rect.X + 1, rect.Right - 1, rect.Y + 0.5);
            }

            // A hairline at each stop, so its handle reads as pointing at its place on the bar.
            var selected = Math.Clamp(display.SelectedGradientStopIndex, 0, gradient.Stops.Length - 1);
            for (var i = 0; i < gradient.Stops.Length; i++)
            {
                var x = Math.Round(Inset + (gradient.Stops[i].Position * width)) + 0.5;
                var on = i == selected;
                ConsolePaint.VLine(context, ConsolePaint.Alpha(on ? Palette.Accent : ConsolePaint.White, on ? 0.9 : 0.35), x, WellPad - 2, BarHeight - WellPad + 2);
            }
        }

        // The cells' brushes, rebuilt only when the gradient or the cell count changes.
        private IBrush[] CellBrushes(SignalGradient gradient, int count)
        {
            if (_cells.Length == count && ReferenceEquals(_cellsGradient, gradient))
            {
                return _cells;
            }

            _cellsGradient = gradient;
            _cells = new IBrush[count];
            for (var i = 0; i < count; i++)
            {
                var (r, g, b) = gradient.Evaluate((i + 0.5) / count);
                var colour = Color.FromRgb(r, g, b);
                _cells[i] = new ImmutableLinearGradientBrush(
                    [new ImmutableGradientStop(0, ConsolePaint.Lighter(colour, 0.12)), new ImmutableGradientStop(1, colour)],
                    startPoint: new RelativePoint(0, 0, RelativeUnit.Relative),
                    endPoint: new RelativePoint(0, 1, RelativeUnit.Relative));
            }

            return _cells;
        }
    }

    /// <summary>One stop's handle: a small key in the stop's colour with a notch pointing up at the bar. Focusable.</summary>
    public sealed class StopHandle : Control
    {
        private readonly SignalGradientEditor _editor;
        private string _colour = string.Empty;
        private IBrush _swatch = Brushes.Transparent;

        internal StopHandle(SignalGradientEditor editor)
        {
            _editor = editor;
            Focusable = true;
            IsTabStop = true;
            Cursor = new Cursor(StandardCursorType.SizeWestEast);
            FocusAdorner = null;
        }

        private StopPeer? _peer;

        public int Index { get; private set; }

        public double Position { get; private set; }

        public bool IsSelected { get; private set; }

        internal void Update(int index, SignalGradientStop stop, bool selected)
        {
            Index = index;
            var oldValue = Math.Round(Position * 100, 1);
            Position = stop.Position;
            var newValue = Math.Round(Position * 100, 1);
            if (oldValue != newValue)
            {
                // A screen reader hears the move, as on the mixer's faders.
                _peer?.RaiseValueChanged(oldValue, newValue);
            }

            IsSelected = selected;
            if (stop.Colour != _colour)
            {
                _colour = stop.Colour;
                _swatch = new ImmutableSolidColorBrush(Color.Parse(stop.Colour));
            }

            AutomationProperties.SetName(this, $"Gradient stop {index + 1} at {GradientEditorViewModel.FormatPercent(stop.Position)}");
            AutomationProperties.SetHelpText(this, $"{stop.Colour}. Left or Right to move; Shift for 5%.");
            InvalidateVisual();
        }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            base.OnGotFocus(e);
            _editor.Select(this);
            InvalidateVisual();
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            base.OnLostFocus(e);
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _editor.BeginDrag(this, e);
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (ReferenceEquals(e.Pointer.Captured, this))
            {
                _editor.Drag(e);
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _editor.EndDrag(e);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            _editor.FinishDrag();
        }

        protected override AutomationPeer OnCreateAutomationPeer() => _peer = new StopPeer(this);

        internal void MoveTo(double position) => _editor.MoveTo(this, position);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.05 : 0.01;
            switch (e.Key)
            {
                case Key.Left:
                    _editor.Nudge(this, -step);
                    e.Handled = true;
                    break;
                case Key.Right:
                    _editor.Nudge(this, step);
                    e.Handled = true;
                    break;
            }
        }

        public override void Render(DrawingContext context)
        {
            var w = Bounds.Width;
            var h = Bounds.Height;
            var mid = w / 2;

            // A notch up to the bar, then a rounded key face holding a swatch of the stop's colour.
            var notch = new StreamGeometry();
            using (var g = notch.Open())
            {
                g.BeginFigure(new Point(mid, 0), true);
                g.LineTo(new Point(mid + 4, 5));
                g.LineTo(new Point(mid - 4, 5));
                g.EndFigure(true);
            }

            var rim = IsSelected ? Palette.Accent : Palette.Key;
            context.DrawGeometry(ConsolePaint.Brush(rim), null, notch);
            var face = new Rect(0.5, 4.5, w - 1, h - 5);
            if (IsSelected)
            {
                Glow.Rect(context, face, 3, Palette.Accent, 0.35);
            }

            context.DrawRectangle(ConsolePaint.Vertical(ConsolePaint.Lighter(Palette.Key, 0.06), Palette.Key), ConsolePaint.Pen(rim), new RoundedRect(face, 3));
            context.DrawRectangle(_swatch, ConsolePaint.Pen(ConsolePaint.Alpha(ConsolePaint.Black, 0.5)), new RoundedRect(face.Deflate(3), 1.5));
            if (IsFocused)
            {
                context.DrawRectangle(null, ConsolePaint.Pen(Palette.Focus), new RoundedRect(face.Inflate(2), 5));
            }
        }

        /// <summary>A stop to a screen reader: a slider over 0 to 100 (%), named for its place and described by its colour.</summary>
        private sealed class StopPeer(StopHandle owner) : ControlAutomationPeer(owner), IRangeValueProvider
        {
            public bool IsReadOnly => false;

            public double Minimum => 0;

            public double Maximum => 100;

            public double Value => Math.Round(owner.Position * 100, 1);

            public double SmallChange => 1;

            public double LargeChange => 5;

            public void SetValue(double value) => owner.MoveTo(value / 100);

            public void RaiseValueChanged(double oldValue, double newValue) =>
                RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, oldValue, newValue);

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        }
    }
}
