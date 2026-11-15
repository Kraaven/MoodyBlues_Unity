using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoodyBlues
{
    /// <summary>
    /// The streamer's outgoing buffer: a <see cref="RingBufferAccumulator"/> plus the size of every
    /// event in it (so a send can be cut at an event boundary) and the bytes a send currently has
    /// on the socket.
    ///
    /// The in-flight bytes sit at the front of the ring and the socket may still be reading them,
    /// so they can be neither dropped nor overwritten until <see cref="CompleteSend"/>. A ring can
    /// only be trimmed from its front, which means that while a send is in flight there is nothing
    /// that may be dropped to make room: an event that does not fit is refused instead.
    ///
    /// Not thread-safe; used from the main thread only.
    /// </summary>
    public sealed class EventSendBuffer
    {
        private readonly RingBufferAccumulator _accumulator;
        private readonly Queue<int> _eventSizes = new Queue<int>();
        private int _inFlightBytes;

        public EventSendBuffer(int capacity)
        {
            _accumulator = new RingBufferAccumulator(capacity);
        }

        public int FreeBytes => _accumulator.FreeBytes;
        public int ReadableBytes => _accumulator.ReadableBytes;
        public int EventCount => _eventSizes.Count;

        /// <summary>Bytes of the send that has been started but not completed (0 when none).</summary>
        public int InFlightBytes => _inFlightBytes;

        /// <summary>Events lost to overflow: dropped from the buffer, or refused because nothing could be.</summary>
        public long DroppedEvents { get; private set; }

        /// <summary>
        /// Appends one event, dropping the oldest unsent events until at least
        /// <paramref name="headroom"/> bytes remain free after it. Never throws. Returns false, and
        /// counts the event as dropped, when room cannot be made.
        /// </summary>
        public bool TryWrite(ReadOnlySpan<byte> bytes, int headroom)
        {
            if (bytes.Length > _accumulator.Capacity)
            {
                DroppedEvents++;
                return false;
            }

            while (_accumulator.FreeBytes < bytes.Length + headroom)
            {
                if (!TryDropOldest())
                {
                    DroppedEvents++;
                    return false;
                }
            }

            bytes.CopyTo(_accumulator.Reserve(bytes.Length));
            _accumulator.Commit(bytes.Length);
            _eventSizes.Enqueue(bytes.Length);
            return true;
        }

        private bool TryDropOldest()
        {
            if (_inFlightBytes > 0 || _eventSizes.Count == 0) return false;

            int expected = _eventSizes.Peek();

            // Taking and immediately releasing reuses TryTakeFlushChunk's skip-marker handling,
            // so the read cursor can never land mid-padding.
            if (!_accumulator.TryTakeFlushChunk(1, expected, out ReadOnlyMemory<byte> chunk)) return false;
            if (chunk.Length != expected)
            {
                // Would desync _eventSizes against the buffer; bail rather than corrupt the stream.
                Debug.LogError(
                    $"EventSendBuffer: drop-oldest expected {expected} bytes but the buffer offered " +
                    $"{chunk.Length}. Not dropping.");
                return false;
            }

            _accumulator.ReleaseFlushChunk(chunk.Length);
            _eventSizes.Dequeue();
            DroppedEvents++;
            return true;
        }

        /// <summary>
        /// Finds the longest run of whole buffered events that fits within
        /// <paramref name="maxBytes"/> and is physically contiguous, so the chunk never cuts an
        /// event in half. Does not copy; the chunk stays valid until it is released.
        /// </summary>
        public bool TryPrepareChunk(int maxBytes, out ReadOnlyMemory<byte> chunk)
        {
            int safeSize = 0;
            foreach (int eventSize in _eventSizes)
            {
                if (safeSize + eventSize > maxBytes) break;
                safeSize += eventSize;
            }

            if (safeSize <= 0)
            {
                chunk = ReadOnlyMemory<byte>.Empty;
                return false;
            }

            // May still return less than safeSize if the readable region wraps -- fine, the wrap
            // point is always event-aligned (Reserve() never lets an event straddle it).
            return _accumulator.TryTakeFlushChunk(1, safeSize, out chunk);
        }

        /// <summary>Marks a prepared chunk as being on the socket, which protects it from drops.</summary>
        public void BeginSend(int length)
        {
            _inFlightBytes = length;
        }

        /// <summary>The in-flight send was delivered: releases its bytes.</summary>
        public void CompleteSend()
        {
            int length = _inFlightBytes;
            _inFlightBytes = 0;
            if (length > 0) Release(length);
        }

        /// <summary>The in-flight send failed: its bytes stay buffered to be sent again.</summary>
        public void AbandonSend()
        {
            _inFlightBytes = 0;
        }

        /// <summary>Releases <paramref name="length"/> bytes at the front, after a synchronous send.</summary>
        public void Release(int length)
        {
            _accumulator.ReleaseFlushChunk(length);

            int remaining = length;
            while (remaining > 0 && _eventSizes.Count > 0)
            {
                remaining -= _eventSizes.Dequeue();
            }
        }
    }
}
