using System.Diagnostics;
using Bluestone.Audio;

namespace Bluestone.Presentation;

/// <summary>
/// What a level meter shows, in dBFS: the engine's falling peak and its RMS per side, a peak hold that stays at the
/// highest peak for <see cref="HoldTime"/> before following the peak down, and a clip light that stays on once a
/// peak passes 0 dBFS until <see cref="ResetClip"/>. Updated in place on the UI thread, with no allocation, so a meter
/// view can redraw from it about 30 times a second.
/// </summary>
public sealed class MeterLevels
{
    /// <summary>The bottom of the scale; anything quieter shows as this.</summary>
    public const double FloorDecibels = -60;

    /// <summary>The top of the scale.</summary>
    public const double CeilingDecibels = 6;

    public static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(2);

    private long _heldLeftAt;
    private long _heldRightAt;

    /// <summary>Raised after every <see cref="Update"/>.</summary>
    public event EventHandler? Updated;

    /// <summary>A reading arrived (audio is running and the strip is in the engine's graph).</summary>
    public bool IsActive { get; private set; }

    public double PeakLeft { get; private set; } = FloorDecibels;

    public double PeakRight { get; private set; } = FloorDecibels;

    public double RmsLeft { get; private set; } = FloorDecibels;

    public double RmsRight { get; private set; } = FloorDecibels;

    public double HoldLeft { get; private set; } = FloorDecibels;

    public double HoldRight { get; private set; } = FloorDecibels;

    /// <summary>A peak went over 0 dBFS since the last <see cref="ResetClip"/>.</summary>
    public bool Clipped { get; private set; }

    /// <summary>Where <paramref name="decibels"/> falls on the scale: 0 at the floor, 1 at the ceiling.</summary>
    public static double Fraction(double decibels) => Math.Clamp((decibels - FloorDecibels) / (CeilingDecibels - FloorDecibels), 0, 1);

    /// <param name="reading">The engine's meter, or null when there is none (audio off): the meter falls to the floor.</param>
    /// <param name="timestamp">Now, in <see cref="Stopwatch"/> ticks.</param>
    public void Update(MeterReading? reading, long timestamp)
    {
        IsActive = reading is not null;
        var value = reading ?? default;
        PeakLeft = Decibels(value.PeakLeft);
        PeakRight = Decibels(value.PeakRight);
        RmsLeft = Decibels(value.RmsLeft);
        RmsRight = Decibels(value.RmsRight);
        Clipped |= value.PeakLeft > 1f || value.PeakRight > 1f;
        (HoldLeft, _heldLeftAt) = Hold(PeakLeft, HoldLeft, _heldLeftAt, timestamp);
        (HoldRight, _heldRightAt) = Hold(PeakRight, HoldRight, _heldRightAt, timestamp);
        Updated?.Invoke(this, EventArgs.Empty);
    }

    public void ResetClip() => Clipped = false;

    private static (double Hold, long At) Hold(double peak, double hold, long heldAt, long timestamp) =>
        peak >= hold || Stopwatch.GetElapsedTime(heldAt, timestamp) >= HoldTime ? (peak, timestamp) : (hold, heldAt);

    private static double Decibels(float linear) => Math.Max(FloorDecibels, MeterReading.ToDecibels(linear));
}
