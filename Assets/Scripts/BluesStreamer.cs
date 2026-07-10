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
/// FlushPendingWrites() once per FixedUpdate tick, after all of that tick's events have been
/// written, instead of once per event -- but FlushPendingWrites itself only actually sends once
/// PacketSize bytes have accumulated (see its own doc comment), so most of those per-tick calls
/// are a no-op and several ticks' worth of small events get coalesced into one right-sized
/// WebSocket message. The only time a flush happens outside of that (mid-tick, ignoring the
/// PacketSize threshold) is the rare safety-valve case where the ring buffer has no room left
/// for the next event (see ReserveWithFlushFallback) -- normal sizing (24KB buffer vs. an
/// expected few KB per tick) should make that essentially never trigger in practice -- and on
/// Dispose, to avoid losing whatever's left unsent when the session ends.
/// </summary>
public class BluesStreamer
{
    private const int RingBufferCapacity = 24 * 1024;
    private const int PacketSize = 8 * 1024;

    private readonly ClientWebSocket _socket;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private CancellationToken _socketToken => _cts.Token;

    // Only used to receive control frames (Ping/Close), never application data -- see
    // ReceiveLoopAsync.
    private readonly byte[] _receiveScratch = new byte[256];

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

            // Fire-and-forget: keeps a ReceiveAsync perpetually pending for the lifetime of
            // the socket. See ReceiveLoopAsync's doc comment for why this is required at all.
            ReceiveLoopAsync();
        }
        catch (Exception ex)
        {
            // Without this, a failed connect just leaves the socket in a non-Open state
            // forever, and flushing silently no-ops every tick with no indication why.
            Debug.LogError($"BluesStreamer: failed to connect - {ex}");
        }
    }

    /// <summary>
    /// Perpetually awaits ReceiveAsync so the underlying ClientWebSocket can service incoming
    /// control frames. This connection is send-only from our side (the server never sends us
    /// application data), so it's tempting to think there's nothing to receive -- but
    /// ClientWebSocket only inspects/responds to a server's keepalive PING (replying with a
    /// PONG automatically) while a ReceiveAsync call is actually pending. With nothing ever
    /// calling ReceiveAsync, incoming PINGs just sit unacknowledged: the server's ping_timeout
    /// (20s by default in the `websockets` library) eventually elapses with no PONG seen, and
    /// it force-closes the connection with code 1011 "keepalive ping timeout" -- which is
    /// exactly what was happening before this loop existed, roughly ~40s into every session
    /// regardless of scene activity.
    /// </summary>
    private async void ReceiveLoopAsync()
    {
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await _socket.ReceiveAsync(_receiveScratch, _socketToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Debug.Log("BluesStreamer: server closed the connection.");
                    return;
                }
                // Any real payload from the server is unexpected on this send-only stream --
                // discard it rather than trying to interpret it as anything meaningful.
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Dispose (see _cts.Cancel()).
        }
        catch (ObjectDisposedException)
        {
            // Expected if Dispose() ran while a receive was in flight.
        }
        catch (Exception ex)
        {
            Debug.LogError($"BluesStreamer: receive loop ended unexpectedly - {ex}");
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

    /// <summary>
    /// Lifecycle events (ShowObject/HideObject/DeleteObject/InstantiateObject) aren't tied to
    /// BluesSessionManager's per-tick poll -- BluesRuntimeManager can call them from anywhere
    /// (Update, a gameplay script, etc.), so unlike per-tick transform deltas (see
    /// BluesSessionManager.FixedUpdate, which emits at most one TimeStamp per tick and only if
    /// that tick actually has changed transforms to report) they can't assume a nearby, still-
    /// accurate TimeStamp is already in the stream. Every EnqueueXxx lifecycle method below
    /// therefore stamps its own current time immediately before its event, so the receiver
    /// always knows exactly when it happened regardless of how it was triggered.
    /// </summary>
    private void EnqueueTimeStampForLifecycleEvent()
    {
        EnqueueTimeStamp(Time.timeAsDouble);
    }

    public void EnqueueShowObject(ushort objectId)
    {
        EnqueueTimeStampForLifecycleEvent();
        Span<byte> scratch = stackalloc byte[Serializer.ShowObjectSize];
        Serializer.SerializeShowObject(objectId, scratch);
        WriteToAccumulator(scratch);
    }

    public void EnqueueHideObject(ushort objectId)
    {
        EnqueueTimeStampForLifecycleEvent();
        Span<byte> scratch = stackalloc byte[Serializer.HideObjectSize];
        Serializer.SerializeHideObject(objectId, scratch);
        WriteToAccumulator(scratch);
    }

    public void EnqueueDeleteObject(ushort objectId)
    {
        EnqueueTimeStampForLifecycleEvent();
        Span<byte> scratch = stackalloc byte[Serializer.DeleteObjectSize];
        Serializer.SerializeDeleteObject(objectId, scratch);
        WriteToAccumulator(scratch);
    }

    public void EnqueueInstantiateObject(ushort newObjectId, ushort templateObjectId, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        EnqueueTimeStampForLifecycleEvent();
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
    /// Called once per tick (after that tick's EnqueueXxx calls -- see
    /// BluesSessionManager.FixedUpdate), but only actually sends anything once at least
    /// PacketSize bytes have piled up in the ring buffer. Below that, this is a no-op and the
    /// data just keeps accumulating -- the point of batching into the ring buffer in the first
    /// place is to coalesce many ticks' worth of small events (a handful of bytes each) into
    /// full-size WebSocket messages instead of paying a send + frame-overhead per tick, which is
    /// what happened before this threshold existed (every tick flushed immediately, so most
    /// messages were 9-16 bytes).
    ///
    /// Once there's enough buffered, drains it in as many PacketSize-capped, event-boundary-
    /// aligned chunks as currently fit -- but stops as soon as less than PacketSize remains
    /// rather than draining down to empty, so a small leftover tail stays buffered for the next
    /// tick(s) to top up instead of going out as its own tiny message.
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

        if (_accumulator.ReadableBytes < PacketSize) return;

        _isFlushing = true;
        try
        {
            do
            {
                _flushRequestedWhileBusy = false;

                while (_accumulator.ReadableBytes >= PacketSize && TryPrepareNextChunk(out ReadOnlyMemory<byte> chunk))
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
        try
        {
            // FlushPendingWrites only sends once PacketSize bytes have piled up, so whatever's
            // still short of that at shutdown would otherwise sit in the ring buffer and get
            // silently dropped -- drain it unconditionally now instead of losing the tail end
            // of the session.
            FlushSync();
            _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Application Quit", _socketToken);
        }
        catch (Exception)
        {
            // Best-effort: if the socket's already in a state that rejects CloseAsync (e.g.
            // never finished connecting), there's nothing further to do here.
        }
        finally
        {
            // Cancels the pending ReceiveAsync in ReceiveLoopAsync so it unwinds via
            // OperationCanceledException instead of racing the Dispose() call below.
            _cts.Cancel();
            _socket.Dispose();
            _cts.Dispose();
        }
    }
}
