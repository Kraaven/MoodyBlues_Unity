using System;
using System.Net.WebSockets;
using System.Threading;
using UnityEngine;

public class BluesStreamer
{
    private const int RingBufferCapacity = 8 * 1024;
    private const int FlushThreshold = 2 * 1024;

    private readonly ClientWebSocket _socket;
    private CancellationToken _socketToken;

    private ThreadInstanceManager.ThreadInstance<TransformData, int> SerialisationThread;

    public readonly RingBufferAccumulator _accumulator = new RingBufferAccumulator(RingBufferCapacity);

    public bool SendTrueTransforms;

    // Guards TryFlush against re-entrancy: OnBytesCommitted can fire again (from
    // ThreadInstanceManager.Update draining multiple results in one frame) while a
    // previous flush is still awaiting SendAsync. Without this, two flushes could both
    // read the same unreleased chunk out of the accumulator and send it twice.
    private bool _isFlushing;
    private bool _flushRequestedWhileBusy;

    public BluesStreamer()
    {
        _socket = new ClientWebSocket();
        ConnectAsync();

        SerialisationThread = ThreadInstanceManager.CreateThreadInstance<TransformData, int>(
            SerializeTransformData, OnBytesCommitted, "DataSerializer");
    }

    private async void ConnectAsync()
    {
        try
        {
            //await _socket.ConnectAsync(new System.Uri("127.0.0.1:8765"), _socketToken);
            await _socket.ConnectAsync(new System.Uri("ws://localhost:8765"), _socketToken);
            Debug.Log("BluesStreamer: WebSocket connected.");
        }
        catch (Exception ex)
        {
            // Without this, a failed connect just leaves the socket in a non-Open state
            // forever, and TryFlush silently no-ops every tick with no indication why.
            Debug.LogError($"BluesStreamer: failed to connect - {ex}");
        }
    }

    public void EnqueueTransform(TransformData data)
    {
        SerialisationThread.EnqueueRequest(data);
    }

    public int SerializeTransformData(TransformData inboundData)
    {
        Span<byte> scratch = stackalloc byte[Serializer.MaxEventSize];

        int size = TransformEventDispatcher.SerializeBestFitEvent(
            inboundData.ObjectId, in inboundData, true, scratch);

        if (size <= 0)
        {
            // Dispatcher decided nothing changed enough to warrant an event this tick.
            return 0;
        }

        Span<byte> dest = _accumulator.Reserve(size);
        scratch.Slice(0, size).CopyTo(dest);
        _accumulator.Commit(size);

        return size;
    }

    private void OnBytesCommitted(int bytesWritten)
    {
        TryFlush();
    }

    private async void TryFlush()
    {
        if (_isFlushing)
        {
            // A flush is already in progress (awaiting SendAsync). Don't start a second
            // one on top of it -- just remember that more data showed up, and let the
            // in-progress flush pick it up in its own loop once it's free.
            _flushRequestedWhileBusy = true;
            return;
        }

        _isFlushing = true;
        try
        {
            do
            {
                _flushRequestedWhileBusy = false;

                while (_accumulator.TryTakeFlushChunk(FlushThreshold, FlushThreshold, out ReadOnlyMemory<byte> chunk))
                {
                    if (_socket.State != WebSocketState.Open)
                    {
                        Debug.Log("Socket Closed, will try again");
                        return; // finally still resets _isFlushing
                    }

                    await _socket.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: true, _socketToken);
                    _accumulator.ReleaseFlushChunk(chunk.Length);
                }

                // Covers the narrow window between the inner loop's last (failed)
                // TryTakeFlushChunk and _isFlushing being cleared, during which new
                // bytes could've been committed and had their OnBytesCommitted call
                // swallowed by the _isFlushing guard above.
            } while (_flushRequestedWhileBusy);
        }
        finally
        {
            _isFlushing = false;
        }
    }

    public void Dispose()
    {
        _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Application Quit", _socketToken);
        _socket.Dispose();
    }
}