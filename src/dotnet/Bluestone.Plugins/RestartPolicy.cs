namespace Bluestone.Plugins;

/// <summary>
/// When to restart an instance after its worker fails. Automatic restart is off by default. When on, at most
/// <see cref="MaxRestarts"/> restarts are attempted within any sliding <see cref="Window"/>, each after a back-off
/// that doubles from <see cref="InitialBackoff"/> up to <see cref="MaxBackoff"/>. One more failure after that
/// quarantines the instance until a manual restart, which resets the counters.
/// </summary>
public sealed record RestartPolicy
{
    public static RestartPolicy Default { get; } = new();

    public bool AutoRestart { get; init; }

    public int MaxRestarts { get; init; } = 3;

    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>What to do after a failure.</summary>
public readonly record struct RestartDecision(bool Quarantine, TimeSpan Delay)
{
    public static RestartDecision QuarantineNow { get; } = new(true, TimeSpan.Zero);

    public static RestartDecision RestartAfter(TimeSpan delay) => new(false, delay);
}

/// <summary>
/// The arithmetic of <see cref="RestartPolicy"/>: a sliding window of restart times. Pure and clock-free; callers pass
/// the time, so it is tested without sleeping. Not thread-safe; the owning instance serializes calls.
/// </summary>
public sealed class RestartGuard
{
    private readonly RestartPolicy _policy;
    private readonly Queue<DateTimeOffset> _restarts = new();

    public RestartGuard(RestartPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfNegative(policy.MaxRestarts);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(policy.Window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(policy.InitialBackoff, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(policy.MaxBackoff, policy.InitialBackoff);
        _policy = policy;
    }

    /// <summary>Restarts counted in the window as of the last call.</summary>
    public int RestartsInWindow => _restarts.Count;

    /// <summary>Records a failure at <paramref name="now"/> and decides: restart after a back-off, or quarantine.</summary>
    public RestartDecision RecordFailure(DateTimeOffset now)
    {
        while (_restarts.Count > 0 && now - _restarts.Peek() >= _policy.Window)
        {
            _restarts.Dequeue();
        }

        if (_restarts.Count >= _policy.MaxRestarts)
        {
            return RestartDecision.QuarantineNow;
        }

        var delay = Backoff(_restarts.Count);
        _restarts.Enqueue(now);
        return RestartDecision.RestartAfter(delay);
    }

    /// <summary>Forgets all history; used by a manual restart.</summary>
    public void Reset() => _restarts.Clear();

    private TimeSpan Backoff(int previousRestarts)
    {
        var ticks = (double)_policy.InitialBackoff.Ticks * Math.Pow(2, Math.Min(previousRestarts, 30));
        return ticks >= _policy.MaxBackoff.Ticks ? _policy.MaxBackoff : TimeSpan.FromTicks((long)ticks);
    }
}
