using Avalonia.Input;
using Bluestone.Domain.Devices;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Views;

/// <summary>
/// What is being dragged onto a Device View: a device definition from the Browser (to insert a new device), or a device
/// already in a chain (to move it). Process-local; it never leaves Bluestone.
/// </summary>
internal sealed record ChainDrag(DeviceDefinition? Definition, DeviceId? Device, DeviceViewViewModel? Source)
{
    public static readonly DataFormat<ChainDrag> Format = DataFormat.CreateInProcessFormat<ChainDrag>("bluestone.chain-item");

    public static ChainDrag Insert(DeviceDefinition definition) => new(definition, null, null);

    public static ChainDrag Move(DeviceId device, DeviceViewViewModel source) => new(null, device, source);

    public DataTransfer ToTransfer()
    {
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(Format, this));
        return transfer;
    }

    public static ChainDrag? From(DragEventArgs e) => e.DataTransfer.TryGetValue(Format);
}
