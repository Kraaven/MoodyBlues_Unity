using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MoodyBlues
{
    /// <summary>Observable connection state, so a host game can tell recording has stopped.</summary>
    public enum RecordingState
    {
        Connecting,
        Recording,
        Reconnecting,
        Stopped,

        /// <summary>Gave up reconnecting. Events are still buffered but nothing is being sent.</summary>
        Failed
    }

    /// <summary>
    /// Transport for the outbound event stream: owns the WebSocket, the ring buffer events are
    /// packed into, and the single pump that moves bytes out. EnqueueXxx serializes inline on the
    /// calling thread (expected to be the main thread, see MoodyBluesSessionManager.CaptureTick)
    /// straight into the ring buffer. See Spec.md Section 6.
    ///
    /// Policy -- when to connect, when to emit session events, when to flush a partial buffer --
    /// belongs to <see cref="MoodyBluesMaintenanceSystem"/>, not here. This class only does what
    /// it is told, and is the single place that touches the socket.
    ///
    /// Every send funnels through <see cref="PumpSendsAsync"/> because ClientWebSocket does not
    /// support concurrent sends: with a capture-driven flush and a timer-driven keepalive both
    /// wanting the socket, one serialized pump is the only safe arrangement.
    /// </summary>
    public sealed class MoodyBluesStreamer : IDisposable
    {
        private const int RingBufferCapacity = 24 * 1024;
        private const int PacketSize = 8 * 1024;

        private ClientWebSocket _socket;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private CancellationToken _socketToken => _cts.Token;

        // Only used to receive control frames (Ping/Close), never application data -- see ReceiveLoopAsync.
        private readonly byte[] _receiveScratch = new byte[256];

        private readonly RingBufferAccumulator _accumulator = new RingBufferAccumulator(RingBufferCapacity);

        // Sizes of events committed into _accumulator, in commit order, not yet released -- lets a
        // flush cut a WebSocket message at an exact event boundary rather than an arbitrary byte offset.
        private readonly Queue<int> _eventSizes = new Queue<int>();

        // Guards the pump. Not a lock: everything here runs on the main thread except the awaited
        // SendAsync itself, so a flag is sufficient and cheaper.
        private bool _sendPumpRunning;
        private bool _sendRequested;

        private readonly Uri _webSocketUri;
        private readonly float _captureHz;
        private bool _disposed;

        private long _droppedEventCount;
        private bool _resyncRequested;
        private bool _warnedDropping;

        public RecordingState State { get; private set; } = RecordingState.Connecting;

        /// <summary>Raised on the main thread whenever <see cref="State"/> changes.</summary>
        public event Action<RecordingState> StateChanged;

        /// <summary>Events discarded because the socket could not keep up. Reported in EndSession.</summary>
        public long DroppedEventCount => _droppedEventCount;

        public bool IsConnected => _socket != null && _socket.State == WebSocketState.Open;

        public MoodyBluesStreamer(Uri webSocketUri, float captureHz)
        {
            _webSocketUri = webSocketUri;
            _captureHz = captureHz;
        }

        private void SetState(RecordingState state)
        {
            if (State == state) return;
            State = state;
            try
            {
                StateChanged?.Invoke(state);
            }
            catch (Exception ex)
            {
                // A subscriber throwing must not take down the recorder.
                Debug.LogError($"MoodyBluesStreamer: StateChanged subscriber threw - {ex}");
            }
        }

        #region Connection

        /// <summary>
        /// Connects, then sends BeginSession directly on the socket before the pump is allowed to
        /// run. BeginSession has to be the first event a receiver decodes on this connection,
        /// because it declares the quantization ranges every later value depends on -- so it
        /// deliberately bypasses the ring buffer, which may already hold capture events written
        /// while the connection was still being established.
        /// </summary>
        public async Task<bool> ConnectAsync()
        {
            if (_disposed) return false;

            _socket?.Dispose();
            _socket = new ClientWebSocket();

            try
            {
                await _socket.ConnectAsync(_webSocketUri, _socketToken);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"MoodyBluesStreamer: connect to {_webSocketUri} failed - {ex.Message}");
                return false;
            }

            // Claim the pump so nothing can interleave with the handshake event.
            _sendPumpRunning = true;
            try
            {
                var scratch = new byte[Serializer.BeginSessionSize];
                int size = Serializer.SerializeBeginSession(_captureHz, scratch);
                await _socket.SendAsync(
                    new ArraySegment<byte>(scratch, 0, size),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    _socketToken);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"MoodyBluesStreamer: failed to send BeginSession - {ex.Message}");
                _sendPumpRunning = false;
                return false;
            }

            _sendPumpRunning = false;

            Debug.Log($"MoodyBluesStreamer: connected to {_webSocketUri}, protocol v{Serializer.ProtocolVersion}.");
            SetState(RecordingState.Recording);
            ReceiveLoopAsync();
            return true;
        }

        public void NotifyReconnecting() => SetState(RecordingState.Reconnecting);

        public void NotifyFailed() => SetState(RecordingState.Failed);

        /// <summary>
        /// Perpetually awaits ReceiveAsync so ClientWebSocket can service the server's keepalive
        /// PING/PONG. This connection is send-only from our side, but ClientWebSocket only replies
        /// to a PING while a ReceiveAsync is actually pending -- without this loop the server's
        /// ping_timeout eventually elapses with no PONG seen and force-closes the connection.
        /// </summary>
        private async void ReceiveLoopAsync()
        {
            // async void: nothing awaits this, so an escaping exception would be unobserved.
            try
            {
                while (_socket != null && _socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = await _socket.ReceiveAsync(_receiveScratch, _socketToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Debug.Log("MoodyBluesStreamer: server closed the connection.");
                        return;
                    }
                    // Any real payload is unexpected on this send-only stream -- discard it.
                }
            }
            catch (OperationCanceledException) { /* expected on Dispose */ }
            catch (ObjectDisposedException) { /* expected if Dispose ran mid-receive */ }
            catch (Exception ex)
            {
                Debug.LogWarning($"MoodyBluesStreamer: receive loop ended - {ex.Message}");
            }
        }

        #endregion

        #region Enqueue (serialize directly into the ring buffer)

        /// <summary>
        /// Serializes the best-fit event for <paramref name="data"/> into the ring buffer and
        /// returns what was encoded. The caller must store the result's Position/Rotation/Scale as
        /// the object's new baseline -- those are the values the receiver will reconstruct, and
        /// using them instead of the true floats is what stops quantization error accumulating.
        /// </summary>
        public TransformEncodeResult EnqueueTransform(TransformData data)
        {
            Span<byte> scratch = stackalloc byte[Serializer.MaxEventSize];
            TransformEncodeResult result = TransformEventDispatcher.SerializeBestFitEvent(data.ObjectId, in data, scratch);
            if (!result.Wrote) return result; // Nothing changed; caller should have already gated on this.
            WriteToAccumulator(scratch.Slice(0, result.BytesWritten));
            return result;
        }

        public void EnqueueTimeStamp(double timeSeconds)
        {
            Span<byte> scratch = stackalloc byte[Serializer.TimeStampSize];
            Serializer.SerializeTimeStamp(timeSeconds, scratch);
            WriteToAccumulator(scratch);
        }

        /// <summary>
        /// Lifecycle events can be triggered from anywhere (not just the capture tick), so unlike
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

        public void EnqueueInstantiateObject(ushort newObjectId, ushort templateObjectId, ushort parentObjectId, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            EnqueueTimeStampForLifecycleEvent();
            Span<byte> scratch = stackalloc byte[Serializer.InstantiateObjectSize];
            Serializer.SerializeInstantiateObject(newObjectId, templateObjectId, parentObjectId, position, rotation, scale, scratch);
            WriteToAccumulator(scratch);
        }

        /// <summary>
        /// Emits a reparent and returns the resulting baseline. Like EnqueueTransform, the caller
        /// must store the result's Position/Rotation/Scale: this event carries an absolute local
        /// transform, so the receiver's state jumps to the quantized values written here.
        /// </summary>
        public TransformEncodeResult EnqueueReparentObject(
            ushort objectId,
            ushort newParentObjectId,
            Vector3 localPosition,
            Quaternion localRotation,
            Vector3 localScale)
        {
            EnqueueTimeStampForLifecycleEvent();

            Span<byte> scratch = stackalloc byte[Serializer.ReparentObjectSize];
            int size = Serializer.SerializeReparentObject(
                objectId, newParentObjectId, localPosition, localRotation, localScale, scratch);
            WriteToAccumulator(scratch.Slice(0, size));

            var result = new TransformEncodeResult
            {
                BytesWritten = size,
                HardClamped = !Serializer.FitsTruePosition(localPosition) || !Serializer.FitsTrueScale(localScale),
            };

            Serializer.TryApplyTransformEvent(
                scratch.Slice(0, size),
                localPosition,
                localRotation,
                localScale,
                out result.Position,
                out result.Rotation,
                out result.Scale);

            return result;
        }

        public void EnqueueKeepAlive(double timeSeconds)
        {
            EnqueueTimeStamp(timeSeconds);
            Span<byte> scratch = stackalloc byte[Serializer.KeepAliveSize];
            Serializer.SerializeKeepAlive(scratch);
            WriteToAccumulator(scratch);
        }

        public void EnqueueEndSession(Serializer.SessionEndReason reason, long deltaPromotions, long rotationWidenings, long hardClamps)
        {
            EnqueueTimeStamp(Time.timeAsDouble);
            Span<byte> scratch = stackalloc byte[Serializer.EndSessionSize];
            Serializer.SerializeEndSession(
                reason,
                Saturate(deltaPromotions),
                Saturate(rotationWidenings),
                Saturate(hardClamps),
                Saturate(_droppedEventCount),
                scratch);
            WriteToAccumulator(scratch);
        }

        private static uint Saturate(long value) =>
            value <= 0 ? 0u : value >= uint.MaxValue ? uint.MaxValue : (uint)value;

        private void WriteToAccumulator(ReadOnlySpan<byte> bytes)
        {
            if (!TryReserve(bytes.Length, out Span<byte> dest)) return; // dropped; already counted
            bytes.CopyTo(dest);
            _accumulator.Commit(bytes.Length);
            _eventSizes.Enqueue(bytes.Length);
        }

        /// <summary>
        /// Reserves space, making room if necessary. Never throws: this runs inside the capture
        /// tick, and the previous behaviour (an exception out of Reserve once the buffer filled
        /// against a dead socket) meant a single dropped connection produced an exception every
        /// tick for the rest of the run.
        /// </summary>
        private bool TryReserve(int size, out Span<byte> destination)
        {
            if (_accumulator.FreeBytes >= size + Serializer.MaxEventSize)
            {
                destination = _accumulator.Reserve(size);
                return true;
            }

            // A synchronous drain is only worth attempting when the socket can actually take it.
            if (!_sendPumpRunning && IsConnected)
            {
                FlushSync();
            }

            while (_accumulator.FreeBytes < size + Serializer.MaxEventSize)
            {
                if (!TryDropOldestEvent())
                {
                    destination = default;
                    return false;
                }
            }

            destination = _accumulator.Reserve(size);
            return true;
        }

        /// <summary>
        /// Discards the oldest buffered event to make room. Drop-oldest keeps the newest state,
        /// so the recording resumes correctly after the gap rather than stopping dead.
        ///
        /// Any drop breaks the Delta* chain: the receiver's baseline for every affected object is
        /// now whatever it had before the gap. A full absolute resync is therefore requested, and
        /// the capture tick performs it once the socket is healthy again (see
        /// ConsumeResyncRequest). Without that, everything after a drop would be silently offset.
        /// </summary>
        private bool TryDropOldestEvent()
        {
            if (_eventSizes.Count == 0) return false;

            int expected = _eventSizes.Peek();

            // Taking and immediately releasing reuses TryTakeFlushChunk's skip-marker handling,
            // so the read cursor can never land mid-padding.
            if (!_accumulator.TryTakeFlushChunk(1, expected, out ReadOnlyMemory<byte> chunk)) return false;
            if (chunk.Length != expected)
            {
                // Would desync _eventSizes against the buffer; bail rather than corrupt the stream.
                Debug.LogError(
                    $"MoodyBluesStreamer: drop-oldest expected {expected} bytes but the buffer offered " +
                    $"{chunk.Length}. Not dropping.");
                return false;
            }

            _accumulator.ReleaseFlushChunk(chunk.Length);
            _eventSizes.Dequeue();
            _droppedEventCount++;
            _resyncRequested = true;

            if (!_warnedDropping)
            {
                _warnedDropping = true;
                Debug.LogWarning(
                    "MoodyBluesStreamer: the socket cannot keep up, so buffered events are being dropped. " +
                    "An absolute resync is queued so playback recovers after the gap. Reported once per " +
                    "session; the total is in EndSession.");
            }

            return true;
        }

        /// <summary>
        /// True once (and then cleared) after events were dropped. The capture tick responds by
        /// sending absolute transforms for everything, which re-anchors every object the gap
        /// desynchronised.
        /// </summary>
        public bool ConsumeResyncRequest()
        {
            if (!_resyncRequested) return false;
            _resyncRequested = false;
            return true;
        }

        #endregion

        #region Flushing

        /// <summary>
        /// Called once per capture tick. Sends only once a full packet has piled up, letting
        /// several ticks' worth of small events coalesce into one right-sized WebSocket message
        /// instead of one tiny message per tick.
        /// </summary>
        public void FlushIfFull()
        {
            if (_accumulator.ReadableBytes < PacketSize) return;
            PumpSendsAsync();
        }

        /// <summary>
        /// Sends whatever is buffered, however little. Called on the maintenance tick so a crash
        /// or force-quit can only lose data since the last keepalive, rather than everything still
        /// sitting below the packet threshold.
        /// </summary>
        public void FlushTail()
        {
            if (_accumulator.ReadableBytes <= 0) return;
            PumpSendsAsync();
        }

        /// <summary>
        /// The only place application data is sent. Re-entrant calls set a flag instead of
        /// starting a second pump, because ClientWebSocket forbids overlapping sends.
        /// </summary>
        private async void PumpSendsAsync()
        {
            // async void: fire-and-forget from a MonoBehaviour callback, so everything is wrapped.
            if (_sendPumpRunning)
            {
                _sendRequested = true;
                return;
            }

            if (!IsConnected) return;

            _sendPumpRunning = true;
            try
            {
                do
                {
                    _sendRequested = false;

                    while (TryPrepareNextChunk(out ReadOnlyMemory<byte> chunk))
                    {
                        if (!IsConnected) return; // finally still clears _sendPumpRunning
                        await _socket.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: true, _socketToken);
                        ReleaseChunk(chunk.Length);
                    }
                } while (_sendRequested);
            }
            catch (OperationCanceledException) { /* expected on Dispose */ }
            catch (ObjectDisposedException) { /* expected if Dispose ran mid-send */ }
            catch (Exception ex)
            {
                Debug.LogWarning($"MoodyBluesStreamer: send failed, buffering until reconnect - {ex.Message}");
            }
            finally
            {
                _sendPumpRunning = false;
            }
        }

        private void FlushSync()
        {
            while (TryPrepareNextChunk(out ReadOnlyMemory<byte> chunk))
            {
                if (!IsConnected) return;

                try
                {
                    // Blocking is acceptable: this only runs on the rare overflow path and on
                    // Dispose, and ClientWebSocket's I/O doesn't depend on Unity's main loop.
                    _socket.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: true, _socketToken)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"MoodyBluesStreamer: synchronous flush failed - {ex.Message}");
                    return;
                }

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
            while (remaining > 0 && _eventSizes.Count > 0)
            {
                remaining -= _eventSizes.Dequeue();
            }
        }

        #endregion

        /// <summary>
        /// Flushes everything still buffered and closes the socket cleanly. Called by
        /// MoodyBluesMaintenanceSystem after it has emitted EndSession, so that event is included
        /// in the final flush rather than lost.
        /// </summary>
        public void Shutdown(TimeSpan closeTimeout)
        {
            try
            {
                FlushSync();
                CloseSocketBlocking("StopStreaming", closeTimeout);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"MoodyBluesStreamer: shutdown error - {ex.Message}");
            }
            finally
            {
                SetState(RecordingState.Stopped);
            }
        }

        /// <summary>
        /// Blocks the calling (main) thread until the WebSocket close handshake completes, or until
        /// timeout elapses. Called from synchronous MonoBehaviour callbacks that can't await --
        /// without blocking here, a fire-and-forget CloseAsync would almost always be aborted by
        /// the immediate socket disposal below before the close frame reached the server, forcing
        /// the backend to fall back on its inactivity timeout instead of finalizing promptly.
        /// </summary>
        private void CloseSocketBlocking(string reason, TimeSpan timeout)
        {
            if (_socket == null) return;
            if (_socket.State != WebSocketState.Open && _socket.State != WebSocketState.CloseReceived) return;

            try
            {
                Task closeTask = _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, _socketToken);
                Task finished = Task.WhenAny(closeTask, Task.Delay(timeout)).GetAwaiter().GetResult();
                if (finished != closeTask)
                {
                    Debug.LogWarning(
                        "MoodyBluesStreamer: WebSocket close handshake timed out; forcing disposal. The " +
                        "backend's inactivity timeout will finalize the session instead.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"MoodyBluesStreamer: CloseAsync failed - {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _cts.Cancel();
            }
            catch (Exception) { /* best effort */ }

            _socket?.Dispose();
            _cts.Dispose();
            SetState(RecordingState.Stopped);
        }
    }
}
