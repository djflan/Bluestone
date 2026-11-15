namespace Bluestone.Audio;

/// <summary>The DSP shown by a mixer strip or the Master.</summary>
public enum MixerMeterMode : byte
{
    /// <summary>Post-fader peak hold and 300 ms RMS; the existing default.</summary>
    PeakRms = 0,

    /// <summary>Average-rectified level calibrated to the configured sine-wave dBFS reference.</summary>
    Vu = 1,

    /// <summary>RMS level relative to −20 dBFS.</summary>
    K20 = 2,

    /// <summary>RMS level relative to −14 dBFS.</summary>
    K14 = 3,

    /// <summary>RMS level relative to −12 dBFS.</summary>
    K12 = 4,

    /// <summary>Ungated BS.1770 stereo short-term loudness over the trailing three seconds.</summary>
    LufsS = 5,
}

/// <summary>
/// Settings for a mixer meter. The VU reference is the dBFS RMS level of a sine wave that reads 0 VU.
/// </summary>
public readonly record struct MixerMeterSettings(MixerMeterMode Mode, int VuReferenceDbfs)
{
    /// <summary>The default mixer meter settings.</summary>
    public static MixerMeterSettings Default { get; } = new(MixerMeterMode.PeakRms, -18);

    /// <summary>Rejects values that cannot be represented by the mixer meter.</summary>
    public void Validate()
    {
        if (Mode is not (MixerMeterMode.PeakRms or MixerMeterMode.Vu or MixerMeterMode.K20 or MixerMeterMode.K14 or MixerMeterMode.K12 or MixerMeterMode.LufsS))
        {
            throw new ArgumentOutOfRangeException(nameof(Mode), "Unknown mixer meter mode.");
        }

        if (VuReferenceDbfs is < -36 or > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(VuReferenceDbfs), "The VU reference must be from −36 to 0 dBFS.");
        }
    }
}

/// <summary>
/// A lock-free snapshot of a mixer meter. The mode value is in dBFS for Peak + RMS, dBVU for VU, relative dB
/// for a K scale, or LUFS for LUFS-S. LUFS-S values are NaN while <see cref="IsReady"/> is false. Peak and RMS
/// readings remain available in <see cref="PeakRms"/> for every mode.
/// </summary>
public readonly record struct MixerMeterReading(
    MeterReading PeakRms,
    MixerMeterMode Mode,
    double LeftValue,
    double RightValue,
    bool IsReady);

/// <summary>
/// Post-fader mixer strip or Master meter. The existing peak-hold and RMS readings retain
/// <see cref="AudioMeter"/> semantics. Optional display DSP is calculated from the same post-fader samples.
/// The thirty-segment LUFS history is allocated with the meter, not while rendering.
/// </summary>
public sealed class MixerMeter
{
    private const int LufsSegmentCount = 30;
    private const double VuSineAverageToRms = 0.9003163161571061; // 2√2 / π
    private const double LufsOffset = -0.691;
    private const double VuBallisticsSeconds = 0.3;
    private const double K20ReferenceDbfs = -20;
    private const double K14ReferenceDbfs = -14;
    private const double K12ReferenceDbfs = -12;

    private readonly double[] _lufsSegments = new double[LufsSegmentCount];

    // Settings are packed into one word so a mode/reference update is observed atomically by the audio thread.
    private long _settings = Pack(MixerMeterSettings.Default);

    // Published legacy peak/RMS readings.
    private int _peakLeftBits;
    private int _peakRightBits;
    private int _rmsLeftBits;
    private int _rmsRightBits;

    // Audio-thread-only peak/RMS state.
    private float _heldLeft;
    private float _heldRight;
    private double _meanSquareLeft;
    private double _meanSquareRight;

    // VU state is audio-thread-owned, with the current averages published for readers.
    private double _vuMeanAbsLeft;
    private double _vuMeanAbsRight;
    private int _vuLeftBits;
    private int _vuRightBits;

    // Audio-thread-owned LUFS state and its lock-free published result.
    private long _lufsBits = BitConverter.DoubleToInt64Bits(double.NaN);
    private PreparedLufsFilters? _preparedFilters;
    private int _segmentFrameCount;
    private int _segmentIndex;
    private int _segmentCount;
    private double _segmentEnergy;
    private double _lufsEnergySum;
    private BiquadState _leftShelfState;
    private BiquadState _rightShelfState;
    private BiquadState _leftHighPassState;
    private BiquadState _rightHighPassState;
    private MixerMeterMode _appliedMode;

    /// <summary>
    /// Gets or atomically replaces meter settings. Invalid values are rejected here, off the audio thread;
    /// valid updates take no lock and allocate nothing.
    /// </summary>
    public MixerMeterSettings Settings
    {
        get => Unpack(Volatile.Read(ref _settings));
        set
        {
            value.Validate();
            Volatile.Write(ref _settings, Pack(value));
        }
    }

    /// <summary>
    /// Prepares immutable filter coefficients on the graph/state setup thread. Preparation is one-time per meter;
    /// graph snapshots reuse the same sample rate and therefore do not write while an older snapshot can render.
    /// </summary>
    internal void Prepare(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1000);
        var existing = Volatile.Read(ref _preparedFilters);
        if (existing is not null)
        {
            if (existing.SampleRate != sampleRate)
            {
                throw new InvalidOperationException("A mixer meter cannot be reused at another sample rate; create new meter state.");
            }

            return;
        }

        var prepared = new PreparedLufsFilters(
            sampleRate,
            Math.Max(1, sampleRate / 10),
            HighShelf(sampleRate, 1681.974450955533, 3.99984385397, 0.7071752369554196),
            HighPass(sampleRate, 38.13547087602444, 0.5003270373238773));
        existing = Interlocked.CompareExchange(ref _preparedFilters, prepared, null);
        if (existing is not null && existing.SampleRate != sampleRate)
        {
            throw new InvalidOperationException("A mixer meter cannot be reused at another sample rate; create new meter state.");
        }
    }

    /// <summary>Reads the existing peak/RMS values, regardless of the selected display mode.</summary>
    public MeterReading Read() => new(
        BitConverter.Int32BitsToSingle(Volatile.Read(ref _peakLeftBits)),
        BitConverter.Int32BitsToSingle(Volatile.Read(ref _peakRightBits)),
        BitConverter.Int32BitsToSingle(Volatile.Read(ref _rmsLeftBits)),
        BitConverter.Int32BitsToSingle(Volatile.Read(ref _rmsRightBits)));

    /// <summary>Reads the selected mode's stereo values and readiness without locking.</summary>
    public MixerMeterReading ReadLevel()
    {
        var settings = Settings;
        var peakRms = Read();
        return settings.Mode switch
        {
            MixerMeterMode.Vu => new MixerMeterReading(
                peakRms,
                settings.Mode,
                VuDecibels(Volatile.Read(ref _vuLeftBits), settings.VuReferenceDbfs),
                VuDecibels(Volatile.Read(ref _vuRightBits), settings.VuReferenceDbfs),
                true),
            MixerMeterMode.K20 => new MixerMeterReading(
                peakRms, settings.Mode,
                MeterReading.ToDecibels(peakRms.RmsLeft) - K20ReferenceDbfs,
                MeterReading.ToDecibels(peakRms.RmsRight) - K20ReferenceDbfs,
                true),
            MixerMeterMode.K14 => new MixerMeterReading(
                peakRms, settings.Mode,
                MeterReading.ToDecibels(peakRms.RmsLeft) - K14ReferenceDbfs,
                MeterReading.ToDecibels(peakRms.RmsRight) - K14ReferenceDbfs,
                true),
            MixerMeterMode.K12 => new MixerMeterReading(
                peakRms, settings.Mode,
                MeterReading.ToDecibels(peakRms.RmsLeft) - K12ReferenceDbfs,
                MeterReading.ToDecibels(peakRms.RmsRight) - K12ReferenceDbfs,
                true),
            MixerMeterMode.LufsS => ReadLufs(peakRms, settings.Mode),
            _ => new MixerMeterReading(
                peakRms,
                MixerMeterMode.PeakRms,
                MeterReading.ToDecibels(peakRms.PeakLeft),
                MeterReading.ToDecibels(peakRms.PeakRight),
                true),
        };
    }

    /// <summary>Audio thread only. Receives the post-fader mixer signal.</summary>
    internal void Update(StereoBuffer block, in AudioRenderContext context)
    {
        var frames = block.Frames;
        if (frames == 0)
        {
            return;
        }

        var sampleRate = context.SampleRate;
        var fall = (float)Math.Pow(10, -AudioMeter.PeakFallDecibelsPerSecond * frames / sampleRate / 20);
        var rmsAlpha = 1 - Math.Exp(-1 / (AudioMeter.RmsTimeConstantSeconds * sampleRate));
        _heldLeft = Math.Max(Peak(block.Left), _heldLeft * fall);
        _heldRight = Math.Max(Peak(block.Right), _heldRight * fall);
        _meanSquareLeft = MeanSquare(block.Left, _meanSquareLeft, rmsAlpha);
        _meanSquareRight = MeanSquare(block.Right, _meanSquareRight, rmsAlpha);
        Volatile.Write(ref _peakLeftBits, BitConverter.SingleToInt32Bits(_heldLeft));
        Volatile.Write(ref _peakRightBits, BitConverter.SingleToInt32Bits(_heldRight));
        Volatile.Write(ref _rmsLeftBits, BitConverter.SingleToInt32Bits((float)Math.Sqrt(_meanSquareLeft)));
        Volatile.Write(ref _rmsRightBits, BitConverter.SingleToInt32Bits((float)Math.Sqrt(_meanSquareRight)));

        var settings = Settings;
        if (settings.Mode != _appliedMode)
        {
            _appliedMode = settings.Mode;
            _vuMeanAbsLeft = 0;
            _vuMeanAbsRight = 0;
            Volatile.Write(ref _vuLeftBits, 0);
            Volatile.Write(ref _vuRightBits, 0);
            ResetLufs();
        }

        if (settings.Mode == MixerMeterMode.Vu)
        {
            UpdateVu(block, sampleRate);
        }
        else if (settings.Mode == MixerMeterMode.LufsS)
        {
            if (context.Jumped)
            {
                ResetLufs();
            }

            if (context.Wraps)
            {
                // The second part of this block follows a transport loop wrap and starts a new valid history.
                ResetLufs();
                UpdateLufs(block.Left[context.WrapOffset..], block.Right[context.WrapOffset..], sampleRate);
            }
            else
            {
                UpdateLufs(block.Left, block.Right, sampleRate);
            }
        }
    }

    private static long Pack(MixerMeterSettings settings) =>
        ((long)(byte)settings.Mode << 32) | (uint)(settings.VuReferenceDbfs + 36);

    private static MixerMeterSettings Unpack(long packed) =>
        new((MixerMeterMode)(byte)(packed >> 32), (int)(packed & 0xFF) - 36);

    private static double VuDecibels(int averageBits, int referenceDbfs)
    {
        var average = BitConverter.Int32BitsToSingle(averageBits);
        var calibration = VuSineAverageToRms * Math.Pow(10, referenceDbfs / 20.0);
        return average <= 0 ? double.NegativeInfinity : 20 * Math.Log10(average / calibration);
    }

    private static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        return peak;
    }

    private static double MeanSquare(ReadOnlySpan<float> samples, double meanSquare, double alpha)
    {
        foreach (var sample in samples)
        {
            meanSquare += (((double)sample * sample) - meanSquare) * alpha;
        }

        return meanSquare < 1e-20 ? 0 : meanSquare;
    }

    private void UpdateVu(StereoBuffer block, int sampleRate)
    {
        var alpha = 1 - Math.Exp(-1 / (VuBallisticsSeconds * sampleRate));
        var left = block.Left;
        var right = block.Right;
        for (var i = 0; i < block.Frames; i++)
        {
            _vuMeanAbsLeft += (Math.Abs(left[i]) - _vuMeanAbsLeft) * alpha;
            _vuMeanAbsRight += (Math.Abs(right[i]) - _vuMeanAbsRight) * alpha;
        }

        Volatile.Write(ref _vuLeftBits, BitConverter.SingleToInt32Bits((float)_vuMeanAbsLeft));
        Volatile.Write(ref _vuRightBits, BitConverter.SingleToInt32Bits((float)_vuMeanAbsRight));
    }

    private void UpdateLufs(ReadOnlySpan<float> left, ReadOnlySpan<float> right, int sampleRate)
    {
        var filters = Volatile.Read(ref _preparedFilters);
        if (filters is null || filters.SampleRate != sampleRate)
        {
            // A mismatched or unprepared graph is never measured with stale coefficients.
            ResetLufs();
            return;
        }

        for (var i = 0; i < left.Length; i++)
        {
            var inputLeft = left[i];
            var inputRight = right[i];
            if (!float.IsFinite(inputLeft) || !float.IsFinite(inputRight))
            {
                ResetLufs();
                continue;
            }

            var filteredLeft = _leftHighPassState.Process(_leftShelfState.Process(inputLeft, filters.Shelf), filters.HighPass);
            var filteredRight = _rightHighPassState.Process(_rightShelfState.Process(inputRight, filters.Shelf), filters.HighPass);
            _segmentEnergy += (filteredLeft * filteredLeft) + (filteredRight * filteredRight);
            _segmentFrameCount++;
            if (_segmentFrameCount == filters.SegmentFrames)
            {
                CompleteSegment();
            }
        }
    }

    private void CompleteSegment()
    {
        var energy = _segmentEnergy / _segmentFrameCount;
        if (_segmentCount == LufsSegmentCount)
        {
            _lufsEnergySum -= _lufsSegments[_segmentIndex];
        }
        else
        {
            _segmentCount++;
        }

        _lufsSegments[_segmentIndex] = energy;
        _lufsEnergySum += energy;
        _segmentIndex++;
        if (_segmentIndex == LufsSegmentCount)
        {
            _segmentIndex = 0;
        }

        _segmentEnergy = 0;
        _segmentFrameCount = 0;
        if (_segmentCount == LufsSegmentCount)
        {
            var meanEnergy = _lufsEnergySum / LufsSegmentCount;
            var lufs = meanEnergy <= 0 ? double.NegativeInfinity : LufsOffset + (10 * Math.Log10(meanEnergy));
            Volatile.Write(ref _lufsBits, BitConverter.DoubleToInt64Bits(lufs));
        }
    }

    private void ResetLufs()
    {
        Array.Clear(_lufsSegments);
        _segmentIndex = 0;
        _segmentCount = 0;
        _segmentFrameCount = 0;
        _segmentEnergy = 0;
        _lufsEnergySum = 0;
        _leftShelfState.Reset();
        _rightShelfState.Reset();
        _leftHighPassState.Reset();
        _rightHighPassState.Reset();
        Volatile.Write(ref _lufsBits, BitConverter.DoubleToInt64Bits(double.NaN));
    }

    private MixerMeterReading ReadLufs(MeterReading peakRms, MixerMeterMode mode)
    {
        var value = BitConverter.Int64BitsToDouble(Volatile.Read(ref _lufsBits));
        var ready = !double.IsNaN(value);
        return new MixerMeterReading(peakRms, mode, value, value, ready);
    }

    private static BiquadCoefficients HighShelf(int sampleRate, double frequency, double gainDb, double q)
    {
        // BS.1770's high-shelf bilinear-transform form (Vb's exponent is part of the specified shelf shape).
        var k = Math.Tan(Math.PI * frequency / sampleRate);
        var vh = Math.Pow(10, gainDb / 20);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var kOverQ = k / q;
        var kSquared = k * k;
        var a0 = 1 + kOverQ + kSquared;
        return new BiquadCoefficients(
            (vh + (vb * kOverQ) + kSquared) / a0,
            (2 * (kSquared - vh)) / a0,
            (vh - (vb * kOverQ) + kSquared) / a0,
            (2 * (kSquared - 1)) / a0,
            (1 - kOverQ + kSquared) / a0);
    }

    private static BiquadCoefficients HighPass(int sampleRate, double frequency, double q)
    {
        var omega = 2 * Math.PI * frequency / sampleRate;
        var cosine = Math.Cos(omega);
        var alpha = Math.Sin(omega) / (2 * q);
        var a0 = 1 + alpha;
        return new BiquadCoefficients(
            ((1 + cosine) / 2) / a0,
            (-(1 + cosine)) / a0,
            ((1 + cosine) / 2) / a0,
            (-2 * cosine) / a0,
            (1 - alpha) / a0);
    }

    private sealed record PreparedLufsFilters(int SampleRate, int SegmentFrames, BiquadCoefficients Shelf, BiquadCoefficients HighPass);

    private readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2);

    private struct BiquadState
    {
        private double _z1;
        private double _z2;

        public double Process(double input, in BiquadCoefficients coefficients)
        {
            var output = (coefficients.B0 * input) + _z1;
            _z1 = (coefficients.B1 * input) - (coefficients.A1 * output) + _z2;
            _z2 = (coefficients.B2 * input) - (coefficients.A2 * output);
            if (Math.Abs(_z1) < 1e-30)
            {
                _z1 = 0;
            }

            if (Math.Abs(_z2) < 1e-30)
            {
                _z2 = 0;
            }

            return output;
        }

        public void Reset()
        {
            _z1 = 0;
            _z2 = 0;
        }
    }
}
