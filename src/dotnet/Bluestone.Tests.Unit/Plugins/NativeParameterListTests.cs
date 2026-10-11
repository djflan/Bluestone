using Bluestone.PluginWorker.Native;

namespace Bluestone.Tests.Unit.Plugins;

/// <summary>The parameter list the native plugin host hands the worker as JSON.</summary>
public sealed class NativeParameterListTests
{
    [Fact]
    public void ParseParameters_ADefaultThatIsNotANumber_BecomesZero()
    {
        const string Json = """
            [
              { "id": 1, "name": "Cutoff", "units": "Hz", "default": null, "steps": 0, "flags": 1 },
              { "id": 2, "name": "Mode", "units": "", "default": 0.75, "steps": 3, "flags": 1 }
            ]
            """;

        var parameters = NativePlugin.ParseParameters(Json);

        Assert.Equal(0.0, parameters[0].DefaultValue);
        Assert.Equal(0.75, parameters[1].DefaultValue);
        Assert.Equal("Cutoff", parameters[0].Name);
    }
}
