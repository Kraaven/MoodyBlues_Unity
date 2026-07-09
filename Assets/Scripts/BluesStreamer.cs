using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using UnityEngine;

/// <summary>
/// Owns the outbound WebSocket connection and the ring buffer that events get packed into.
///
/// Serialization now happens inline on whichever thread calls the EnqueueXxx methods (expected
/// to always be Unity's main thread -- see BluesSessionManager.FixedUpdate) rather than being
/// handed off to a background worker thread. For the ~500-object scenes this is sized for,
/// bit-packing a handful of floats per object is cheap enough that the BlockingCollection /
/// ConcurrentQueue hop that used to exist here cost more than it saved. Revisit with real
/// profiling data if scenes grow much larger.
///
/// Flushing (draining the ring buffer to the socket) is caller-driven: BluesSessionManager calls
/// FlushPendingWrites() exactly once per FixedUpdate tick, after all of that tick's events have
/// been written, instead of once per event. The only time a flush happens mid-tick is the rare
/// safety-valve case where the ring buffer has no room left for the next event (see
/// ReserveWithFlushFallback) -- normal sizing (24KB buffer vs. an expected few KB per tick)
/// should make that essentially never trigger in practice.
/// </summary>
public class BluesStreamer
{
    private const int RingBufferCapacity = 24 * 1024;
    private const int PacketSize = 8 * 1024;

    private readonly ClientWebSocket _socket;
    private CancellationToken _socketToken;

    private readonly RingBufferAccumulator _accumulator = new RingBufferAccumulator(RingBufferCapacity);

    // Sizes of events committed into _accumulator, in commit order, not yet released. Used so
    // flushing can cut a WebSocket message at an exact event boundary instead of at an arbitrary
    // PacketSize byte offset that might land in the middle of an event -- TryTakeFlushChunk itself
    // has no concept of "events", only bytes, so this bookkeeping has to live here.
    private readonly Queue<int> _eventSizes = new Queue<int>();

    // Guards FlushPendingWrites against re-entrancy: since flushing awaits SendAsync, a second
    // call landing while one is already in flight must not race it for the same unreleased chunk.
    private bool _isFlushing;
    private bool _flushRequestedWhileBusy;

    public BluesStreamer()
    {
        _socket = new ClientWebSocket();
        ConnectAsync();
    }

    private async void ConnectAsync()
    {
        try
        {
            await _socket.ConnectAsync(new System.Uri("ws://localhost:8765"), _socketToken);
            Debug.Log("BluesStreamer: WebSocket connected.");
        }
        catch (Exception ex)
        {
            // Without this, a failed connect just leaves the socket in a non-Open state
            // forever, and flushing silently no-ops every tick with no indication why.
            Debug.LogError($"BluesStreamer: failed to connect - {ex}");
        }
    }

    #region Enqueue (serialize directly into the ring buffer)

    public void EnqueueTransform(TransformData data)
    {
        Span<byte> scratch = stackalloc byte[Serializer.MaxEventSize];
        int size = TransformEventDispatcher.SerializeBestFitEvent(data.ObjectId, in data, scratch);
        if (size <= 0)
        {
            // Caller is expected to have already gated on "did anything change", but stay
            // safe if it didn't.
            return;
        }
        WriteToAccumulator(scratch.Slice(0, size));
    }

    public void EnqueueTimeStamp(double timeSeconds)
    {
        Span<byte> scratch = stackalloc byte[Serializer.TimeStampSize];
        Serializer.SerializeTimeStamp(timeSeconds, scratch);
        WriteToAccumulator(scratch);
    }

    public void EnqueueShowObject(ushort objectId)
    {
        Span<byte> scratch = stackalloc byte[Serializer.ShowObjectSize];
        Serializer.SerializeShowObject(objectId, scratch);
        WriteToAccumulator(scratch);
    }

    public void EnqueueHideObject(ushort objectId)
    {
        Span<byte> scratch = stackalloc byte[Serializer.HideObjectSize];
        Serializer.SerializeHideObject(objectId, scratch);
        WriteToAccumulator(scratch);
    }

    public void EnqueueDeleteObject(ushort objectId)
    {
        Span<byte> scratch = stackalloc byte[Serializer.DeleteObjectSize];
        Serializer.SerializeDeleteObject(objectId, scratch);
        WriteToAccumulator(scratch);
    }

    public void EnqueueInstantiateObject(ushort newObjectId, ushort templateObjectId, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        Span<byte> scratch = stackalloc byte[Serializer.InstantiateObjectSize];
        Serializer.SerializeInstantiateObject(newObjectId, templateObjectId, position, rotation, scale, scratch);
        WriteToAccumulator(scratch);
    }

    private void WriteToAccumulator(ReadOnlySpan<byte> bytes)
    {
        Span<byte> dest = ReserveWithFlushFallback(bytes.Length);
        bytes.CopyTo(dest);
        _accumulator.Commit(bytes.Length);
        _eventSizes.Enqueue(bytes.Length);
    }

    private Span<byte> ReserveWithFlushFallback(int size)
    {
        try
        {
            return _accumulator.Reserve(size);
        }
        catch (InvalidOperationException)
        {
            // The buffer has no room even after accounting for wrap padding. Given
            // RingBufferCapacity (24KB) is sized well above one tick's expected payload for
            // ~500 objects, this should be rare -- when it does happen, drain synchronously
            // to make room rather than losing the event or throwing mid-tick.
            //
            // Not safe to do if an async FlushPendingWrites is already mid-flight (awaiting a
            // SendAsync): ClientWebSocket doesn't support concurrent sends, and the chunk that
            // flush already took is still "unreleased" (counted as used, see
            // RingBufferAccumulator.FreeBytes) so it'd be sitting there unable to help anyway.
            // Surface loudly instead of risking a corrupted/racing send -- this means the buffer
            // is backlogged for multiple ticks' worth of data despite actively flushing, which
            // is a sizing/connectivity problem worth knowing about, not silently papering over.
            if (_isFlushing)
            {
                throw new InvalidOperationException(
                    "RingBufferAccumulator is full and a flush is already in flight -- the " +
                    "socket can't keep up with the data rate. Increase RingBufferCapacity or " +
                    "investigate why sends are backing up.");
            }

            FlushSync();
            return _accumulator.Reserve(size);
        }
    }

    #endregion

    #region Flushing

    /// <summary>
    /// Drains everything currently buffered to the socket, in as many PacketSize-capped,
    /// event-boundary-aligned chunks as needed. Call once per tick after all of that tick's
    /// EnqueueXxx calls, not per event.
    /// </summary>
    public async void FlushPendingWrites()
    {
        if (_isFlushing)
        {
            // A flush is already in progress (awaiting SendAsync). Don't start a second one
            // on top of it -- just remember more data showed up, and let the in-progress
            // flush's own loop pick it up once it's free.
            _flushRequestedWhileBusy = true;
            return;
        }

        _isFlushing = true;
        try
        {
            do
            {
                _flushRequestedWhileBusy = false;

                while (TryPrepareNextChunk(out ReadOnlyMemory<byte> chunk))
                {
                    if (_socket.State != WebSocketState.Open)
                    {
                        Debug.Log("Socket Closed, will try again");
                        return; // finally still resets _isFlushing
                    }

                    await _socket.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: true, _socketToken);
                    ReleaseChunk(chunk.Length);
                }

                // Covers the narrow window between the inner loop's last (failed) attempt and
                // _isFlushing being cleared, during which new bytes could've been committed and
                // had their flush request swallowed by the _isFlushing guard above.
            } while (_flushRequestedWhileBusy);
        }
        finally
        {
            _isFlushing = false;
        }
    }

    private void FlushSync()
    {
        while (TryPrepareNextChunk(out ReadOnlyMemory<byte> chunk))
        {
            if (_socket.State != WebSocketState.Open) return;

            // Blocking is acceptable here: this only runs on the rare overflow path (see
            // ReserveWithFlushFallback), and ClientWebSocket's I/O completes independently of
            // Unity's main-thread loop, so there's no deadlock risk from waiting on it directly.
            _socket.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: true, _socketToken)
                .GetAwaiter().GetResult();
            ReleaseChunk(chunk.Length);
        }
    }

    /// <summary>
    /// Looks at the front of _eventSizes to find the longest run of whole, buffered events that
    /// (a) fits within PacketSize and (b) is physically contiguous in the ring buffer, then asks
    /// the accumulator for exactly that many bytes. Never returns a chunk that cuts an event in
    /// half, unlike asking the accumulator for a flat PacketSize-capped chunk directly would.
    /// </summary>
    private bool TryPrepareNextChunk(out ReadOnlyMemory<byte> chunk)
    {
        int safeSize = 0;
        foreach (int eventSize in _eventSizes)
        {
            if (safeSize + eventSize > PacketSize) break;
            safeSize += eventSize;
        }

        if (safeSize <= 0)
        {
            chunk = ReadOnlyMemory<byte>.Empty;
            return false;
        }

        // TryTakeFlushChunk may still hand back less than safeSize if the readable region wraps
        // around the end of the physical array before reaching safeSize -- that's fine, the wrap
        // point is itself always event-aligned (Reserve() never lets an event straddle it).
        return _accumulator.TryTakeFlushChunk(1, safeSize, out chunk);
    }

    private void ReleaseChunk(int length)
    {
        _accumulator.ReleaseFlushChunk(length);

        int remaining = length;
        while (remaining > 0)
        {
            remaining -= _eventSizes.Dequeue();
        }
    }

    #endregion

    public void Dispose()
    {
        _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Application Quit", _socketToken);
        _socket.Dispose();
    }
}
