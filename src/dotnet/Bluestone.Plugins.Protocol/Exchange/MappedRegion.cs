using System.IO.MemoryMappedFiles;

namespace Bluestone.Plugins.Protocol.Exchange;

/// <summary>
/// The only pointer code in Bluestone's plugin hosting. Why it exists: <see cref="MemoryMappedViewAccessor"/> offers only
/// per-call marshalled reads and writes, which cannot hand out spans for audio or refs for <see cref="Interlocked"/>
/// and <see cref="Volatile"/>. The view's pointer is acquired once here and exposed only as bounds-checked spans and
/// refs; callers must not use them after <see cref="Dispose"/>, which the exchanges guarantee with an in-use counter.
/// The mapping is backed by a file because named shared memory is Windows-only in .NET.
/// </summary>
internal sealed unsafe class MappedRegion : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _pointer;

    private MappedRegion(FileStream stream, long length)
    {
        _file = MemoryMappedFile.CreateFromFile(stream, mapName: null, length, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
        try
        {
            _view = _file.CreateViewAccessor(0, length, MemoryMappedFileAccess.ReadWrite);
            byte* pointer = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            _pointer = pointer + _view.PointerOffset;
        }
        catch
        {
            _view?.Dispose();
            _file.Dispose();
            throw;
        }

        Length = length;
    }

    public long Length { get; }

    /// <summary>
    /// Creates the file and maps it. An existing file is reused only when <paramref name="allowExisting"/> is set and it
    /// already has exactly <paramref name="length"/> bytes, so a mapping held by another process is never truncated.
    /// </summary>
    public static MappedRegion Create(string path, long length, bool allowExisting)
    {
        var stream = new FileStream(path, allowExisting ? FileMode.OpenOrCreate : FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            if (stream.Length == 0)
            {
                stream.SetLength(length);
            }
            else if (stream.Length != length)
            {
                throw new IOException($"Exchange file '{path}' exists with a different size.");
            }

            return new MappedRegion(stream, length);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Maps an existing file in full. Fails if it is shorter than <paramref name="minimumLength"/>.</summary>
    public static MappedRegion Open(string path, long minimumLength)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            if (stream.Length < minimumLength)
            {
                throw new ProtocolException($"Exchange file '{path}' is too short ({stream.Length} bytes).");
            }

            return new MappedRegion(stream, stream.Length);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public Span<byte> Bytes(long offset, int length)
    {
        Check(offset, length, 1);
        return new Span<byte>(_pointer + offset, length);
    }

    public ref int Int32At(long offset)
    {
        Check(offset, sizeof(int), sizeof(int));
        return ref *(int*)(_pointer + offset);
    }

    public ref long Int64At(long offset)
    {
        Check(offset, sizeof(long), sizeof(long));
        return ref *(long*)(_pointer + offset);
    }

    public void Dispose()
    {
        if (_pointer != null)
        {
            _pointer = null;
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
        }

        _view.Dispose();
        _file.Dispose();
    }

    private void Check(long offset, int length, int alignment)
    {
        ObjectDisposedException.ThrowIf(_pointer == null, this);
        if (offset < 0 || length < 0 || offset > Length - length || offset % alignment != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Outside the mapped region or misaligned.");
        }
    }
}
