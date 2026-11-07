using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Bluestone.Desktop.Theme;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The seam between two devices of a chain (ADR 0037), as Bitwig's device panel shows it: the hairline the signal flows
/// along, a note lamp at header height that is lit while notes are held there and lets go softly, and a slim stereo level
/// for its audio in the lower body. Only what can be there is drawn; an idle seam is nearly invisible. Hovering shows
/// what is there in words. Drawing allocates nothing.
/// </summary>
internal sealed class JunctionSeam : Control
{
    public static readonly StyledProperty<JunctionSignal?> SignalProperty = AvaloniaProperty.Register<JunctionSeam, JunctionSignal?>(nameof(Signal));

    public static readonly StyledProperty<bool> IsQuietProperty = AvaloniaProperty.Register<JunctionSeam, bool>(nameof(IsQuiet));

    public static readonly StyledProperty<double> HeaderHeightProperty = AvaloniaProperty.Register<JunctionSeam, double>(nameof(HeaderHeight), 30);

    public static readonly StyledProperty<double> LampSizeProperty = AvaloniaProperty.Register<JunctionSeam, double>(nameof(LampSize), 6);

    public static readonly StyledProperty<double> LinkYProperty = AvaloniaProperty.Register<JunctionSeam, double>(nameof(LinkY), 105);

    public static readonly StyledProperty<double> LevelHeightProperty = AvaloniaProperty.Register<JunctionSeam, double>(nameof(LevelHeight), 44);

    public static readonly StyledProperty<double> LevelBottomProperty = AvaloniaProperty.Register<JunctionSeam, double>(nameof(LevelBottom), 34);

    public static readonly StyledProperty<double> LevelBarWidthProperty = AvaloniaProperty.Register<JunctionSeam, double>(nameof(LevelBarWidth), 3);

    public static readonly StyledProperty<TimeSpan> LampReleaseProperty = AvaloniaProperty.Register<JunctionSeam, TimeSpan>(nameof(LampRelease), TimeSpan.FromMilliseconds(220));

    private static readonly IBrush Link = new SolidColorBrush(Palette.Lighten(Palette.Border, 0.08));
    private static readonly IBrush QuietLink = new SolidColorBrush(Palette.Border);
    private static readonly IBrush LampOff = new SolidColorBrush(Palette.Raised);
    private static readonly IPen LampRim = new Pen(new SolidColorBrush(Palette.Border), 1);
    private static readonly IBrush Track = new SolidColorBrush(Palette.MeterTrack);
    private static readonly IBrush PeakTick = new SolidColorBrush(Palette.Lighten(Palette.MeterLow, 0.45));
    private static readonly IBrush ClipLight = new SolidColorBrush(Palette.MeterClip);
    private static readonly double HotFraction = MeterLevels.Fraction(-6);

    private readonly EventHandler _updated;
    private readonly PropertyChangedEventHandler _changed;
    private JunctionSignal? _subscribed;
    private double _glow;
    private long _lastFrame;
    private (double PeakL, double PeakR, double RmsL, double RmsR, bool Clipped, double Glow) _drawn = (-1, -1, -1, -1, false, -1);
    private LinearGradientBrush? _levelBrush;
    private (double Top, double Bottom) _levelBrushSpan;
    private SolidColorBrush? _lampBrush;
    private RadialGradientBrush? _haloBrush;

    static JunctionSeam()
    {
        AffectsRender<JunctionSeam>(SignalProperty, IsQuietProperty, HeaderHeightProperty, LampSizeProperty, LinkYProperty, LevelHeightProperty, LevelBottomProperty, LevelBarWidthProperty);
        FocusableProperty.OverrideDefaultValue<JunctionSeam>(true);
    }

    public JunctionSeam()
    {
        _updated = (_, _) => OnFrame();
        _changed = (_, e) =>
        {
            if (e.PropertyName is nameof(JunctionSignal.Label) or nameof(JunctionSignal.CanCarryNotes) or nameof(JunctionSignal.CanCarryAudio))
            {
                UpdateName();
                InvalidateVisual();
            }
        };
        ClipToBounds = false;
    }

    public JunctionSignal? Signal
    {
        get => GetValue(SignalProperty);
        set => SetValue(SignalProperty, value);
    }

    /// <summary>The device after it is bypassed: the seam's hairline is quieter, as the chain link was.</summary>
    public bool IsQuiet
    {
        get => GetValue(IsQuietProperty);
        set => SetValue(IsQuietProperty, value);
    }

    public double HeaderHeight
    {
        get => GetValue(HeaderHeightProperty);
        set => SetValue(HeaderHeightProperty, value);
    }

    public double LampSize
    {
        get => GetValue(LampSizeProperty);
        set => SetValue(LampSizeProperty, value);
    }

    public double LinkY
    {
        get => GetValue(LinkYProperty);
        set => SetValue(LinkYProperty, value);
    }

    public double LevelHeight
    {
        get => GetValue(LevelHeightProperty);
        set => SetValue(LevelHeightProperty, value);
    }

    /// <summary>The level's bottom edge, measured up from the seam's bottom.</summary>
    public double LevelBottom
    {
        get => GetValue(LevelBottomProperty);
        set => SetValue(LevelBottomProperty, value);
    }

    public double LevelBarWidth
    {
        get => GetValue(LevelBarWidthProperty);
        set => SetValue(LevelBarWidthProperty, value);
    }

    public TimeSpan LampRelease
    {
        get => GetValue(LampReleaseProperty);
        set => SetValue(LampReleaseProperty, value);
    }

    /// <summary>What the seam carries now, in words, for its tooltip and screen readers.</summary>
    internal string Describe()
    {
        if (Signal is not { } signal)
        {
            return string.Empty;
        }

        var parts = new List<string> { signal.Label };
        if (signal.CanCarryNotes)
        {
            parts.Add(signal.NotesHeld ? "notes held" : "no notes");
        }

        if (signal.CanCarryAudio)
        {
            var levels = signal.Levels;
            var peak = Math.Max(levels.PeakLeft, levels.PeakRight);
            parts.Add(!levels.IsActive || peak <= MeterLevels.FloorDecibels ? "silent"
                : string.Create(CultureInfo.InvariantCulture, $"peak {peak:0.0} dB{(levels.Clipped ? ", clipped" : string.Empty)}"));
        }

        return string.Join(" · ", parts);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SignalProperty)
        {
            Subscribe(VisualRoot is not null ? Signal : null);
            UpdateName();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(Signal);
        UpdateName();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Subscribe(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        ToolTip.SetTip(this, Describe());
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var center = Math.Round(width / 2);
        context.FillRectangle(IsQuiet ? QuietLink : Link, new Rect(0, LinkY, width, 1));
        if (Signal is not { } signal)
        {
            return;
        }

        if (signal.CanCarryNotes)
        {
            DrawLamp(context, center);
        }

        if (signal.CanCarryAudio)
        {
            DrawLevel(context, center, signal.Levels);
        }

        var levels = signal.Levels;
        _drawn = (levels.PeakLeft, levels.PeakRight, levels.RmsLeft, levels.RmsRight, levels.Clipped, _glow);
    }

    private void DrawLamp(DrawingContext context, double center)
    {
        var size = LampSize;
        var y = Math.Round((HeaderHeight - size) / 2);
        var lamp = new Rect(center - (size / 2), y, size, size);
        if (_glow > 0)
        {
            // A soft halo, then the lamp: lit amber, fading back to the unlit lamp as the notes let go.
            _haloBrush ??= new RadialGradientBrush
            {
                GradientStops = [new GradientStop(Color.FromArgb(150, Palette.SignalNote.R, Palette.SignalNote.G, Palette.SignalNote.B), 0), new GradientStop(Color.FromArgb(0, Palette.SignalNote.R, Palette.SignalNote.G, Palette.SignalNote.B), 1)],
            };
            using (context.PushOpacity(_glow))
            {
                var halo = size * 1.6;
                context.DrawEllipse(_haloBrush, null, lamp.Center, halo, halo);
            }

            _lampBrush ??= new SolidColorBrush(Palette.SignalNote);
            _lampBrush.Color = Palette.Mix(Palette.Raised, Palette.SignalNote, _glow);
            context.DrawRectangle(_lampBrush, null, lamp, 1.5, 1.5);
        }
        else
        {
            context.DrawRectangle(LampOff, LampRim, lamp.Deflate(0.5), 1.5, 1.5);
        }
    }

    private void DrawLevel(DrawingContext context, double center, MeterLevels levels)
    {
        var bar = LevelBarWidth;
        var bottom = Bounds.Height - LevelBottom;
        var top = bottom - LevelHeight;
        var height = LevelHeight;
        var left = center - bar - 0.5;
        var silent = !levels.IsActive || Math.Max(levels.PeakLeft, levels.PeakRight) <= MeterLevels.FloorDecibels;

        // An idle level is only a faint track; a sounding one fills with its gradient.
        using (context.PushOpacity(silent ? 0.5 : 1))
        {
            context.DrawRectangle(Track, null, new Rect(left, top, bar, height), 1, 1);
            context.DrawRectangle(Track, null, new Rect(left + bar + 1, top, bar, height), 1, 1);
        }

        if (silent)
        {
            return;
        }

        if (_levelBrush is null || _levelBrushSpan != (top, bottom))
        {
            _levelBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, bottom, RelativeUnit.Absolute),
                EndPoint = new RelativePoint(0, top, RelativeUnit.Absolute),
                GradientStops =
                [
                    new GradientStop(Palette.MeterLow, 0),
                    new GradientStop(Palette.MeterLow, HotFraction * 0.82),
                    new GradientStop(Palette.MeterHot, HotFraction),
                    new GradientStop(Palette.MeterClip, 1),
                ],
            };
            _levelBrushSpan = (top, bottom);
        }

        Side(levels.RmsLeft, levels.PeakLeft, left);
        Side(levels.RmsRight, levels.PeakRight, left + bar + 1);
        if (levels.Clipped)
        {
            context.DrawRectangle(ClipLight, null, new Rect(left, top - 3, (2 * bar) + 1, 2), 1, 1);
        }

        void Side(double rms, double peak, double x)
        {
            var body = Math.Round(MeterLevels.Fraction(rms) * height);
            if (body > 0)
            {
                context.DrawRectangle(_levelBrush, null, new Rect(x, bottom - body, bar, body), 1, 1);
            }

            var tick = Math.Round(MeterLevels.Fraction(peak) * height);
            if (tick > body + 1)
            {
                context.FillRectangle(PeakTick, new Rect(x, bottom - tick, bar, 1));
            }
        }
    }

    // Each frame: the lamp follows the notes (on at once, off over LampRelease), and the seam redraws only if what it
    // shows has changed, so idle seams cost nothing.
    private void OnFrame()
    {
        if (Signal is not { } signal)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsed = _lastFrame == 0 ? 0 : Stopwatch.GetElapsedTime(_lastFrame, now).TotalSeconds;
        _lastFrame = now;
        var release = Math.Max(LampRelease.TotalSeconds, 0.001);
        _glow = signal.NotesHeld ? 1 : Math.Max(0, _glow - (elapsed / release));
        var levels = signal.Levels;
        if (_drawn != (levels.PeakLeft, levels.PeakRight, levels.RmsLeft, levels.RmsRight, levels.Clipped, _glow))
        {
            InvalidateVisual();
        }
    }

    private void Subscribe(JunctionSignal? signal)
    {
        if (ReferenceEquals(signal, _subscribed))
        {
            return;
        }

        if (_subscribed is { } old)
        {
            old.Levels.Updated -= _updated;
            old.PropertyChanged -= _changed;
        }

        _subscribed = signal;
        _glow = 0;
        _lastFrame = 0;
        if (signal is not null)
        {
            signal.Levels.Updated += _updated;
            signal.PropertyChanged += _changed;
        }
    }

    private void UpdateName() => AutomationProperties.SetName(this, Signal?.Label ?? "Signal");
}
