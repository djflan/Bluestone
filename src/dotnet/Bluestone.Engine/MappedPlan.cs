using System.Buffers;
using System.IO.MemoryMappedFiles;
using Bluestone.Playback;

namespace Bluestone.Engine;

/// <summary>
/// A plan image file mapped read-only, and the plan read from it (ADR 0044). The plan's events and payload bytes are
/// views of the mapping, so the mapping lives as long as the plan is in use; it is released only after the playback
/// thread has moved to a later plan.
/// </summary>
internal sealed unsafe class MappedPlan : MemoryManager<byte>
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _pointer;
    private readonly int _length;
    private int _disposed;

    private MappedPlan(long id, MemoryMappedFile file, MemoryMappedViewAccessor view, int length)
    {
        Id = id;
        _file = file;
        _view = view;
        _length = length;
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _pointer = pointer + view.PointerOffset;
    }

    public long Id { get; }

    public PlaybackPlan Plan { get; private set; } = null!;

    /// <summary>Maps <paramref name="path"/> and reads the plan in it.</summary>
    /// <exception cref="InvalidDataException">The file is not a plan image this build reads.</exception>
    /// <exception cref="IOException">The file cannot be opened.</exception>
    public static MappedPlan Open(long id, string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        MemoryMappedFile? file = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            var length = stream.Length;
            if (length is <= 0 or > int.MaxValue)
            {
                throw new InvalidDataException($"A plan image of {length} bytes cannot be read.");
            }

            file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            view = file.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
            var mapped = new MappedPlan(id, file, view, (int)length);
            try
            {
                mapped.Plan = PlanImage.Read(mapped.Memory);
                return mapped;
            }
            catch
            {
                ((IDisposable)mapped).Dispose();
                throw;
            }
        }
        catch when (view is null)
        {
            file?.Dispose();
            stream.Dispose();
            throw;
        }
    }

    public override Span<byte> GetSpan() => new(_pointer, _length);

    public override MemoryHandle Pin(int elementIndex = 0) => new(_pointer + elementIndex);

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
