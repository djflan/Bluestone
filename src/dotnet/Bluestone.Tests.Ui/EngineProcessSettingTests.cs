using Bluestone.Desktop;

namespace Bluestone.Tests.Ui;

/// <summary>
/// The engine process is the default where it has been measured (macOS, ADR 0044); BLUESTONE_ENGINE overrides that.
/// </summary>
public sealed class EngineProcessSettingTests
{
    [Fact]
    public void Unset_UsesTheEngine_OnlyOnMacOS() =>
        Assert.Equal(OperatingSystem.IsMacOS(), App.UseEngineProcess(null));

    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void TheVariable_Overrides_ThePlatformDefault(string setting, bool expected) =>
        Assert.Equal(expected, App.UseEngineProcess(setting));

    [Fact]
    public void AnyOtherValue_KeepsThePlatformDefault() =>
        Assert.Equal(OperatingSystem.IsMacOS(), App.UseEngineProcess("yes"));
}
