using Bluestone.Domain.Devices;

namespace Bluestone.Application.Plugins;

/// <summary>
/// What the editor-window coordinator needs from plugin hosting: open editors, hide and show their windows, and send their
/// keys to Bluestone. <see cref="PluginDeviceHost"/> implements it with editors in worker processes.
/// </summary>
public interface IPluginEditorHost
{
    /// <summary>Whether unpinned editors follow the selected track.</summary>
    bool EditorsFollowSelection { get; }

    bool IsEditorOpen(DeviceId device);

    /// <summary>Opens (or brings back) the device's editor; null on success, else why not.</summary>
    Task<string?> OpenEditorAsync(DeviceId device, CancellationToken cancellationToken = default);

    Task SetEditorVisibleAsync(DeviceId device, bool visible, CancellationToken cancellationToken = default);

    /// <summary>Sends the device editor's keys to Bluestone or the plugin; false when that is not possible.</summary>
    Task<bool> SetEditorKeyboardAsync(DeviceId device, bool toHost, CancellationToken cancellationToken = default);

    /// <summary>A key typed in an editor whose keys go to Bluestone. Any thread.</summary>
    event EventHandler<(DeviceId Device, PluginEditorKey Key)>? EditorKeyPressed;

    /// <summary>A device's runtime status changed (an editor opened or closed, a worker stopped). Any thread.</summary>
    event EventHandler<DeviceId>? StatusChanged;

    /// <summary>The hosting settings changed (for example, whether editors follow the selection). Any thread.</summary>
    event EventHandler? HostingChanged;
}

/// <summary>A key from a plugin editor whose keys go to Bluestone (see <c>EditorKey</c> in the protocol).</summary>
/// <param name="Usage">The USB HID keyboard usage ID.</param>
/// <param name="Modifiers">1 Shift, 2 Control, 4 Alt, 8 Meta.</param>
/// <param name="Down">Pressed (true) or released.</param>
/// <param name="Repeat">An auto-repeat of a held key.</param>
public readonly record struct PluginEditorKey(int Usage, int Modifiers, bool Down, bool Repeat)
{
    public const int Shift = 1;
    public const int Control = 2;
    public const int Alt = 4;
    public const int Meta = 8;
}
