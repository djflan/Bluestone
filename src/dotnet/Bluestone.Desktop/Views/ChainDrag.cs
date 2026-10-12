using Avalonia.Input;
using Bluestone.Domain.Devices;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Views;

/// <summary>
/// What is being dragged onto a Device View: a device definition from the Browser (to insert a new device), or a device
/// already in a chain (to move it). The drag itself is process-local: drop targets read only the typed in-process
/// <see cref="Format"/> through <see cref="From"/>.
/// </summary>
/// <remarks>
/// The transfer also carries <see cref="NativeFormat"/>, a string application format holding only <c>"insert"</c> or
/// <c>"move"</c>. A native drag session needs at least one real pasteboard type: Avalonia.Native drops in-process formats
/// before handing the item to AppKit, and an item with no type at all makes <c>NSDraggingSession</c> throw on macOS as the
/// drag starts. The companion is a fixed token, never a serialized device, definition, or view, and nothing reads it to
/// insert or move anything.
/// </remarks>
internal sealed record ChainDrag(DeviceDefinition? Definition, DeviceId? Device, DeviceViewViewModel? Source)
{
    public static readonly DataFormat<ChainDrag> Format = DataFormat.CreateInProcessFormat<ChainDrag>("bluestone.chain-item");

    /// <summary>The native-representable companion of <see cref="Format"/>: an opaque token, never a payload.</summary>
    public static readonly DataFormat<string> NativeFormat = DataFormat.CreateStringApplicationFormat("bluestone.chain-item");

    public static ChainDrag Insert(DeviceDefinition definition) => new(definition, null, null);

    public static ChainDrag Move(DeviceId device, DeviceViewViewModel source) => new(null, device, source);

    public DataTransfer ToTransfer()
    {
        var item = DataTransferItem.Create(Format, this);
        item.Set(NativeFormat, Definition is not null ? "insert" : "move");
        var transfer = new DataTransfer();
        transfer.Add(item);
        return transfer;
    }

    public static ChainDrag? From(DragEventArgs e) => e.DataTransfer.TryGetValue(Format);
}
