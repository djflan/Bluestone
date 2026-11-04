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

    /// <summary>Hides or shows the device's editor window; false when it could not (it then stays as it was).</summary>
    Task<bool> SetEditorVisibleAsync(DeviceId device, bool visible, CancellationToken cancellationToken = default);

    /// <summary>Sends the device editor's keys to Bluestone or the plugin; false when that is not possible.</summary>
    Task<bool> SetEditorKeyboardAsync(DeviceId device, bool toHost, CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows what the header Bluestone draws on the device's editor window offers; false when it could not (no editor
    /// open, or a window the plugin owns).
    /// </summary>
    Task<bool> SetEditorHeaderAsync(DeviceId device, PluginEditorHeader header, CancellationToken cancellationToken = default);

    /// <summary>A key typed in an editor whose keys go to Bluestone. Any thread.</summary>
    event EventHandler<(DeviceId Device, PluginEditorKey Key)>? EditorKeyPressed;

    /// <summary>A button of an editor window's header was clicked: the device, the button, and the state wanted. Any thread.</summary>
    event EventHandler<(DeviceId Device, PluginEditorButton Button, bool On)>? EditorButtonClicked;

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

/// <summary>A button of the header Bluestone draws on a plugin editor window.</summary>
public enum PluginEditorButton
{
    /// <summary>On: the device is bypassed.</summary>
    Bypass = 1,

    /// <summary>On: the editor stays open when another track is selected.</summary>
    Pin = 2,

    /// <summary>On: the window's keys go to Bluestone.</summary>
    Keys = 3,
}

/// <summary>What one header button shows.</summary>
public enum PluginEditorButtonState
{
    Hidden = 0,
    Off = 1,
    On = 2,

    /// <summary>Shown dimmed; clicks do nothing.</summary>
    Unavailable = 3,
}

/// <summary>What the header of a plugin editor window shows.</summary>
public readonly record struct PluginEditorHeader(PluginEditorButtonState Bypass, PluginEditorButtonState Pin, PluginEditorButtonState Keys);
