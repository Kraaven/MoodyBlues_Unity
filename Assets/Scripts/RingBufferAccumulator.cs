using System;

/// <summary>
/// A fixed-size circular byte buffer that serialized events are written into directly,
/// with no per-event heap allocation. Designed for a single-writer/single-flusher usage
/// pattern (one worker thread writes; flushing happens on that same thread when enough
/// data has accumulated), which matches BluesStreamer's single serialization thread.
///
/// Usage:
///   Span<byte> dest = accumulator.Reserve(Serializer.TruePositionSize);
///   int written = Serializer.SerializeTruePosition(objectId, position, dest);
///   accumulator.Commit(written);
///   if (accumulator.ReadableBytes >= FlushThreshold)
///   {
///       ReadOnlyMemory<byte> chunk = accumulator.TakeFlushChunk(FlushThreshold);
///       // send chunk over the socket, then:
///       accumulator.ReleaseFlushChunk(chunk.Length);
///   }
///
/// Not thread-safe by design — callers must only Reserve/Commit from the writer thread,
/// and only Take/ReleaseFlushChunk from whichever thread does the flush (fine if that's
/// the same thread, which it is here).
/// </summary>
public sealed class RingBufferAccumulator
{
    // A skip-marker byte value that can never collide with a real EventType (EventType
    // enum values only go up to 23; 0 is unused by any real event ID). Written into the
    // first byte of a wasted tail region so the reader can recognize and jump over it
    // instead of transmitting stale bytes left over from a previous lap of the buffer.
    private const byte SkipMarker = 0;

    private readonly byte[] _buffer;
    private int _writeCursor;   // next byte to write to
    private int _readCursor;    // next byte to flush from
    private int _readableCount; // bytes between _readCursor and _writeCursor, logically

    public RingBufferAccumulator(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new byte[capacity];
        _writeCursor = 0;
        _readCursor = 0;
        _readableCount = 0;
    }

    public int Capacity => _buffer.Length;
    public int ReadableBytes => _readableCount;
    public int FreeBytes => _buffer.Length - _readableCount;

    /// <summary>
    /// Reserves a contiguous span of exactly <paramref name="size"/> bytes to write into.
    /// If the buffer doesn't have <paramref name="size"/> contiguous bytes before wrapping,
    /// the write cursor jumps to the start of the buffer first. The wasted tail is stamped
    /// with a SkipMarker byte (rather than left as stale data from a previous lap), so the
    /// reader side can recognize and skip it instead of transmitting garbage — see
    /// TryTakeFlushChunk / SkipMarker.
    /// Throws if there isn't enough free space even after accounting for the wrap.
    /// </summary>
    public Span<byte> Reserve(int size)
    {
        if (size <= 0 || size > _buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(size));

        int contiguousToEnd = _buffer.Length - _writeCursor;
        if (contiguousToEnd < size)
        {
            // Would cross the seam — check the wasted tail plus a wrap still fits.
            if (FreeBytes < contiguousToEnd + size)
                throw new InvalidOperationException(
                    $"RingBufferAccumulator full: need {size} bytes (plus {contiguousToEnd} wasted tail), only {FreeBytes} free. Flush more often or grow capacity.");

            // Stamp the tail so the reader recognizes it as skippable padding, not a real
            // event, then mark it consumed and wrap the write cursor to the start.
            if (contiguousToEnd > 0)
            {
                _buffer[_writeCursor] = SkipMarker;
            }
            _readableCount += contiguousToEnd;
            _writeCursor = 0;
        }
        else if (FreeBytes < size)
        {
            throw new InvalidOperationException(
                $"RingBufferAccumulator full: need {size} bytes, only {FreeBytes} free. Flush more often or grow capacity.");
        }

        return new Span<byte>(_buffer, _writeCursor, size);
    }

    /// <summary>
    /// Call immediately after writing into the span returned by Reserve, with the
    /// number of bytes actually written (normally == the size you reserved).
    /// </summary>
    public void Commit(int bytesWritten)
    {
        _writeCursor += bytesWritten;
        if (_writeCursor == _buffer.Length) _writeCursor = 0;
        _readableCount += bytesWritten;
    }

    /// <summary>
    /// Returns true and outputs a read-only view over up to <paramref name="maxChunkSize"/>
    /// contiguous readable bytes, if at least <paramref name="minFlushSize"/> contiguous
    /// bytes are available right now. Does NOT copy — this is a view into the shared buffer,
    /// so the caller must finish using/sending it before calling ReleaseFlushChunk.
    /// Contiguity means this may return less than ReadableBytes if the readable region
    /// wraps around the end of the buffer; call again after releasing to get the rest.
    /// </summary>
    public bool TryTakeFlushChunk(int minFlushSize, int maxChunkSize, out ReadOnlyMemory<byte> chunk)
    {
        // If the very next readable byte is a skip marker, it means Reserve() wrapped
        // the write cursor and stamped this spot as wasted padding — jump the read
        // cursor past the whole wasted region without ever handing it to the caller.
        // The wasted region's length was added to _readableCount by Reserve() as
        // (contiguousToEnd at the time), which is exactly "rest of buffer from where the
        // marker sits", so skipping straight to the end of the physical array is correct.
        if (_readableCount > 0 && _buffer[_readCursor] == SkipMarker)
        {
            int wastedLength = _buffer.Length - _readCursor;
            _readCursor = 0;
            _readableCount -= wastedLength;
        }

        int contiguousToEnd = _buffer.Length - _readCursor;
        int available = Math.Min(_readableCount, contiguousToEnd);

        if (available < minFlushSize)
        {
            chunk = ReadOnlyMemory<byte>.Empty;
            return false;
        }

        int take = Math.Min(available, maxChunkSize);
        chunk = new ReadOnlyMemory<byte>(_buffer, _readCursor, take);
        return true;
    }

    /// <summary>
    /// Call after the flush chunk has actually been sent, with the length of the chunk
    /// you were given (i.e. chunk.Length from TryTakeFlushChunk).
    /// </summary>
    public void ReleaseFlushChunk(int length)
    {
        _readCursor += length;
        if (_readCursor == _buffer.Length) _readCursor = 0;
        _readableCount -= length;
    }
}