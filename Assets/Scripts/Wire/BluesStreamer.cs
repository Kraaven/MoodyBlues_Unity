using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Owns the outbound WebSocket connection and the ring buffer that events get packed into.
/// EnqueueXxx methods serialize inline on the calling thread (expected to be the main thread,
/// see BluesSessionManager.FixedUpdate) straight into the ring buffer; FlushPendingWrites drains
/// it to the socket once per tick, batching several ticks' worth of small events into right-sized
/// WebSocket messages instead of sending one tiny message per tick. See Spec.md Section 6.
/// </summary>
public class BluesStreamer
{
    private const int RingBufferCapacity = 24 * 1024;
    private const int PacketSize = 8 * 1024;

    private readonly ClientWebSocket _socket;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private CancellationToken _socketToken => _cts.Token;

    // Only used to receive control frames (Ping/Close), never application data -- see ReceiveLoopAsync.
    private readonly byte[] _receiveScratch = new byte[256];

    private readonly RingBufferAccumulator _accumulator = new RingBufferAccumulator(RingBufferCapacity);

    // Sizes of events committed into _accumulator, in commit order, not yet released -- lets a
    // flush cut a WebSocket message at an exact event boundary rather than an arbitrary byte offset.
    private readonly Queue<int> _eventSizes = new Queue<int>();

    // Guards FlushPendingWrites against re-entrancy while a SendAsync is already in flight.
    private bool _isFlushing;
    private bool _flushRequestedWhileBusy;

    private readonly Uri _webSocketUri;

    public BluesStreamer(Uri webSocketUri)
    {
        _webSocketUri = webSocketUri;
        _socket = new ClientWebSocket();
        ConnectAsync();
    }

    private async void ConnectAsync()
    {
        try
        {
            await _socket.ConnectAsync(_webSocketUri, _socketToken);
            Debug.Log("BluesStreamer: WebSocket connected.");
            ReceiveLoopAsync();
        }
        catch (Exception ex)
        {
            Debug.LogError($"BluesStreamer: failed to connect - {ex}");
        }
    }

    /// <summary>
    /// Perpetually awaits ReceiveAsync so ClientWebSocket can service the server's keepalive
    /// PING/PONG. This connection is send-only from our side, but ClientWebSocket only replies
    /// to a PING while a ReceiveAsync is actually pending -- without this loop the server's
    /// ping_timeout eventually elapses with no PONG seen and force-closes the connection.
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
                // Any real payload is unexpected on this send-only stream -- discard it.
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Dispose.
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
        if (size <= 0) return; // Nothing changed; caller should have already gated on this.
        WriteToAccumulator(scratch.Slice(0, size));
    }

    public void EnqueueTimeStamp(double timeSeconds)
    {
        Span<byte> scratch = stackalloc byte[Serializer.TimeStampSize];
        Serializer.SerializeTimeStamp(timeSeconds, scratch);
        WriteToAccumulator(scratch);
    }

    /// <summary>
    /// Lifecycle events can be triggered from anywhere (not just the FixedUpdate poll), so unlike
    /// per-tick transform deltas they can't assume a nearby TimeStamp is still accurate -- each
    /// EnqueueXxx lifecycle method stamps its own current time immediately before its event.
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
            // Buffer's full (should be rare given RingBufferCapacity vs. expected per-tick
            // payload) -- drain synchronously to make room rather than losing the event.
            //
            // Not safe if a flush is already mid-flight: ClientWebSocket doesn't support
            // concurrent sends, and that in-flight chunk can't help anyway. Surface loudly
            // instead of risking a corrupted/racing send.
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
    /// Called once per tick after that tick's EnqueueXxx calls, but only actually sends once at
    /// least PacketSize bytes have piled up -- below that this is a no-op, letting several ticks'
    /// worth of small events coalesce into one right-sized WebSocket message. Drains as many
    /// PacketSize-capped, event-boundary-aligned chunks as currently fit, leaving any leftover
    /// tail buffered for the next tick(s) rather than sending it as its own tiny message.
    /// </summary>
    public async void FlushPendingWrites()
    {
        if (_isFlushing)
        {
            // Already flushing -- let its own loop pick up the new data once it's free.
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

                // Covers new bytes committed in the narrow window before _isFlushing is cleared.
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

            // Blocking is fine: this only runs on the rare overflow path (ReserveWithFlushFallback)
            // and Dispose, and ClientWebSocket's I/O doesn't depend on Unity's main-thread loop.
            _socket.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: true, _socketToken)
                .GetAwaiter().GetResult();
            ReleaseChunk(chunk.Length);
        }
    }

    /// <summary>
    /// Finds the longest run of whole, buffered events that fits within PacketSize and is
    /// physically contiguous, so the resulting chunk never cuts an event in half.
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

        // May still return less than safeSize if the readable region wraps -- fine, the wrap
        // point is always event-aligned (Reserve() never lets an event straddle it).
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
            // Drain unconditionally so the sub-PacketSize tail isn't silently dropped at shutdown.
            FlushSync();
            CloseSocketBlocking(reason: "StopStreaming", timeout: TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Best-effort: nothing further to do if the socket already rejects CloseAsync.
        }
        finally
        {
            // Cancels the pending ReceiveAsync so it unwinds via OperationCanceledException.
            _cts.Cancel();
            _socket.Dispose();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Blocks the calling (main) thread until the WebSocket close handshake completes, or until
    /// timeout elapses. Called from Dispose (BluesSessionManager.StopStreaming/OnApplicationQuit),
    /// which are synchronous MonoBehaviour callbacks that can't await -- without blocking here, a
    /// fire-and-forget CloseAsync would almost always be aborted by the immediate _socket.Dispose()
    /// in Dispose()'s finally block before the close frame actually reaches the server, forcing the
    /// backend to fall back on its 10s inactivity timeout instead of finalizing the session promptly.
    /// </summary>
    private void CloseSocketBlocking(string reason, TimeSpan timeout)
    {
        if (_socket.State != WebSocketState.Open && _socket.State != WebSocketState.CloseReceived)
        {
            return;
        }

        try
        {
            Task closeTask = _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, _socketToken);
            Task finished = Task.WhenAny(closeTask, Task.Delay(timeout)).GetAwaiter().GetResult();
            if (finished != closeTask)
            {
                Debug.LogWarning("BluesStreamer: WebSocket close handshake timed out; forcing disposal. " +
                                  "The backend's 10s inactivity timeout will finalize the session instead.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"BluesStreamer: CloseAsync failed - {ex.Message}");
        }
    }
}
