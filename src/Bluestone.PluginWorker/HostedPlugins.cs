using Bluestone.Plugins.Protocol;
using Bluestone.PluginWorker.Native;
using Bluestone.PluginWorker.Plugins;

namespace Bluestone.PluginWorker;

/// <summary>Creates the plugin an identity names: a reference plugin, or a VST3 or CLAP plugin from its module. Main thread.</summary>
internal static class HostedPlugins
{
    /// <summary>A new instance, or null with <paramref name="error"/> saying why (reported to the host as not found).</summary>
    public static IHostedPlugin? Create(PluginIdentity identity, string? modulePath, out string? error)
    {
        ArgumentNullException.ThrowIfNull(identity);
        error = null;
        if (!NativeModules.IsNativeFormat(identity.Format))
        {
            var reference = ReferencePluginCatalog.Create(identity);
            error = reference is null ? $"Plugin {identity} is not available in this worker." : null;
            return reference;
        }

        if (string.IsNullOrEmpty(modulePath))
        {
            error = $"No module file was given for {identity}.";
            return null;
        }

        if (BinaryArchitecture.Check(modulePath) is { } unsupported)
        {
            error = unsupported;
            return null;
        }

        try
        {
            return NativePlugin.Create(identity, modulePath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            error = $"{identity.DisplayName} could not be loaded from {modulePath}: {ex.Message}";
            return null;
        }
    }
}
