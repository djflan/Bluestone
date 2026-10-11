using System.Collections.Immutable;
using Bluestone.Plugins.Protocol;

namespace Bluestone.PluginWorker.Plugins;

/// <summary>The plugins this worker can host. Built in; nothing is loaded from disk.</summary>
internal static class ReferencePluginCatalog
{
    public static ImmutableArray<PluginIdentity> Identities { get; } =
        [new GainPlugin().Identity, new SinePlugin().Identity, new TransposePlugin().Identity, new DelayPlugin().Identity];

    /// <summary>A new instance, or null when the identity is not a reference plugin (reported to the host as missing).</summary>
    public static IHostedPlugin? Create(PluginIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Format != ReferencePlugin.Format)
        {
            return null;
        }

        return identity.PluginId switch
        {
            GainPlugin.PluginId => new GainPlugin(),
            SinePlugin.PluginId => new SinePlugin(),
            TransposePlugin.PluginId => new TransposePlugin(),
            DelayPlugin.PluginId => new DelayPlugin(),
            _ => null,
        };
    }
}
