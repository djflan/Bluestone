using Bluestone.Plugins;

namespace Bluestone.Tests.Unit.Plugins;

public sealed class RestartGuardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly RestartPolicy Policy = new()
    {
        AutoRestart = true,
        MaxRestarts = 3,
        Window = TimeSpan.FromMinutes(1),
        InitialBackoff = TimeSpan.FromSeconds(1),
        MaxBackoff = TimeSpan.FromSeconds(3),
    };

    [Fact]
    public void RestartPolicy_Default_DoesNotRestartAutomatically() => Assert.False(RestartPolicy.Default.AutoRestart);

    [Fact]
    public void Failures_BackOffExponentially_UpToTheCap_ThenQuarantine()
    {
        var guard = new RestartGuard(Policy);

        Assert.Equal(RestartDecision.RestartAfter(TimeSpan.FromSeconds(1)), guard.RecordFailure(T0));
        Assert.Equal(RestartDecision.RestartAfter(TimeSpan.FromSeconds(2)), guard.RecordFailure(T0.AddSeconds(2)));
        Assert.Equal(RestartDecision.RestartAfter(TimeSpan.FromSeconds(3)), guard.RecordFailure(T0.AddSeconds(5)));
        Assert.Equal(RestartDecision.QuarantineNow, guard.RecordFailure(T0.AddSeconds(9)));
        Assert.Equal(RestartDecision.QuarantineNow, guard.RecordFailure(T0.AddSeconds(10)));
    }

    [Fact]
    public void RestartsOlderThanTheWindow_AreForgotten()
    {
        var guard = new RestartGuard(Policy);
        guard.RecordFailure(T0);
        guard.RecordFailure(T0.AddSeconds(10));
        guard.RecordFailure(T0.AddSeconds(20));

        var decision = guard.RecordFailure(T0.AddSeconds(60));

        Assert.False(decision.Quarantine);
        Assert.Equal(TimeSpan.FromSeconds(3), decision.Delay);
        Assert.Equal(3, guard.RestartsInWindow);
    }

    [Fact]
    public void Reset_ForgetsEverything()
    {
        var guard = new RestartGuard(Policy);
        for (var i = 0; i < 4; i++)
        {
            guard.RecordFailure(T0);
        }

        guard.Reset();

        Assert.Equal(RestartDecision.RestartAfter(TimeSpan.FromSeconds(1)), guard.RecordFailure(T0));
    }

    [Fact]
    public void ZeroRestartsAllowed_QuarantinesOnTheFirstFailure() =>
        Assert.True(new RestartGuard(Policy with { MaxRestarts = 0 }).RecordFailure(T0).Quarantine);

    [Fact]
    public void AnInvalidPolicy_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RestartGuard(Policy with { Window = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RestartGuard(Policy with { MaxBackoff = TimeSpan.FromMilliseconds(1) }));
    }
}

public sealed class PluginInstanceStatusTests
{
    [Theory]
    [InlineData(PluginInstanceState.Starting, PluginInstanceState.Running, true)]
    [InlineData(PluginInstanceState.Starting, PluginInstanceState.Unavailable, true)]
    [InlineData(PluginInstanceState.Running, PluginInstanceState.Unavailable, true)]
    [InlineData(PluginInstanceState.Running, PluginInstanceState.Restarting, true)]
    [InlineData(PluginInstanceState.Unavailable, PluginInstanceState.Restarting, true)]
    [InlineData(PluginInstanceState.Unavailable, PluginInstanceState.Quarantined, true)]
    [InlineData(PluginInstanceState.Unavailable, PluginInstanceState.Running, false)]
    [InlineData(PluginInstanceState.Restarting, PluginInstanceState.Running, true)]
    [InlineData(PluginInstanceState.Restarting, PluginInstanceState.Unavailable, true)]
    [InlineData(PluginInstanceState.Quarantined, PluginInstanceState.Restarting, true)]
    [InlineData(PluginInstanceState.Quarantined, PluginInstanceState.Running, false)]
    [InlineData(PluginInstanceState.Running, PluginInstanceState.Unloaded, true)]
    [InlineData(PluginInstanceState.Unloaded, PluginInstanceState.Restarting, false)]
    [InlineData(PluginInstanceState.Unloaded, PluginInstanceState.Running, false)]
    public void IsAllowed_FollowsTheDocumentedTable(PluginInstanceState from, PluginInstanceState to, bool allowed) =>
        Assert.Equal(allowed, PluginInstanceStatus.IsAllowed(from, to));

    [Fact]
    public void Unavailable_CarriesItsReason()
    {
        var status = PluginInstanceStatus.Unavailable(UnavailableReason.Crashed, "boom");

        Assert.Equal("Unavailable(Crashed)", status.ToString());
        Assert.Throws<ArgumentException>(() => new PluginInstanceStatus(PluginInstanceState.Running, UnavailableReason.Hung));
        Assert.Throws<ArgumentException>(() => new PluginInstanceStatus(PluginInstanceState.Unavailable));
    }
}
