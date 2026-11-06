using System;

namespace MoodyBlues
{
    /// <summary>
    /// Fixed-size circular byte buffer that serialized events are written into directly, with no
    /// per-event heap allocation:
    ///
    ///   Span<byte> dest = accumulator.Reserve(Serializer.TruePositionSize);
    ///   int written = Serializer.SerializeTruePosition(objectId, position, dest);
    ///   accumulator.Commit(written);
    ///   if (accumulator.TryTakeFlushChunk(minSize, maxSize, out var chunk)) {
    ///       // send chunk, then:
    ///       accumulator.ReleaseFlushChunk(chunk.Length);
    ///   }
    ///
    /// Not thread-safe -- caller must own both writing (Reserve/Commit) and flushing
    /// (Take/ReleaseFlushChunk), though they may be different call sites on the same thread.
    /// </summary>
    public sealed class RingBufferAccumulator
    {
        // Stamped at the start of a wasted tail region (see Reserve). The reader finds padding by
        // position (_wrapAt), not by this value; the marker is kept so a raw dump of the buffer is
        // readable, and because no real EventType is 0 it can never be mistaken for an event.
        private const byte SkipMarker = 0;

        private readonly byte[] _buffer;
        private int _writeCursor;   // next byte to write to
        private int _readCursor;    // next byte to flush from
        private int _readableCount; // bytes between _readCursor and _writeCursor, logically

        // Where the current lap's real data ends, when Reserve has wrapped and stamped the tail
        // [_wrapAt, Capacity) as padding; -1 when no padding is pending. The reader must stop at
        // this index: the padding is counted in _readableCount but is not part of any event, so a
        // chunk that ran past it would put padding on the wire and cut later events in half.
        private int _wrapAt = -1;

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
        /// Reserves a contiguous span of exactly <paramref name="size"/> bytes to write into. If
        /// there isn't room before wrapping, stamps the wasted tail with SkipMarker and wraps the
        /// write cursor to the start. Throws if there isn't enough free space even after the wrap.
        /// </summary>
        public Span<byte> Reserve(int size)
        {
            if (size <= 0 || size > _buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(size));

            int contiguousToEnd = _buffer.Length - _writeCursor;
            if (contiguousToEnd < size)
            {
                // Would cross the seam -- check the wasted tail plus a wrap still fits.
                if (FreeBytes < contiguousToEnd + size)
                    throw new InvalidOperationException(
                        $"RingBufferAccumulator full: need {size} bytes (plus {contiguousToEnd} wasted tail), only {FreeBytes} free. Flush more often or grow capacity.");

                if (contiguousToEnd > 0)
                {
                    _buffer[_writeCursor] = SkipMarker;
                }
                _wrapAt = _writeCursor;
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
        /// Outputs a read-only view over up to <paramref name="maxChunkSize"/> contiguous readable
        /// bytes, if at least <paramref name="minFlushSize"/> are available. Does NOT copy -- caller
        /// must finish using it before calling ReleaseFlushChunk. May return less than ReadableBytes
        /// if the readable region wraps; call again after releasing to get the rest.
        /// </summary>
        public bool TryTakeFlushChunk(int minFlushSize, int maxChunkSize, out ReadOnlyMemory<byte> chunk)
        {
            // Reached the padding Reserve() stamped when it wrapped: jump the read cursor past it
            // without ever handing it to the caller.
            if (_readableCount > 0 && _wrapAt >= 0 && _readCursor == _wrapAt)
            {
                int wastedLength = _buffer.Length - _readCursor;
                _readCursor = 0;
                _readableCount -= wastedLength;
                _wrapAt = -1;
            }

            // Real data on this lap ends at the padding, if there is any, not at the buffer end.
            int dataEnd = _wrapAt >= 0 && _readCursor < _wrapAt ? _wrapAt : _buffer.Length;
            int contiguous = dataEnd - _readCursor;
            int available = Math.Min(_readableCount, contiguous);

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
}
