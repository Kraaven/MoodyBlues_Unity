using System;
using System.Threading.Tasks;
using UnityEngine;

namespace MoodyBlues
{
    /// <summary>
    /// Owns the connection's lifetime, separately from the flush path that moves bulk data.
    ///
    /// The split exists because "is the connection alive" and "is there data worth sending" are
    /// different questions, and conflating them is what made a quiet scene undetectable: with
    /// flushing gated on a full 8KB packet, a scene with one slow-moving object took longer to
    /// fill a packet than the receiver's inactivity timeout, so the receiver closed a perfectly
    /// healthy session. A fixed-interval keepalive decouples the two.
    ///
    /// It has to be an application-level event rather than a WebSocket ping: the receiver re-arms
    /// its inactivity timer only on a fully decoded message, so protocol-level pings would not
    /// reset it.
    ///
    /// Not a MonoBehaviour -- it is driven by <c>Tick</c> from MoodyBluesSessionManager, which
    /// keeps it unit-testable and avoids a second component to wire up in the scene.
    /// </summary>
    public sealed class MoodyBluesMaintenanceSystem
    {
        /// <summary>
        /// Keepalive cadence. Must stay comfortably under the receiver's inactivity timeout, and
        /// it also bounds how much buffered data a crash or force-quit can lose, since each tick
        /// opportunistically flushes the tail.
        /// </summary>
        public const double KeepAliveIntervalSeconds = 5.0;

        private static readonly int[] ReconnectBackoffMillis = { 1000, 2000, 4000, 8000 };

        private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

        private readonly MoodyBluesStreamer _streamer;

        private double _keepAliveAccumulator;
        private bool _ended;
        private bool _reconnecting;
        private int _reconnectAttempt;

        public MoodyBluesStreamer Streamer => _streamer;

        public MoodyBluesMaintenanceSystem(MoodyBluesStreamer streamer)
        {
            _streamer = streamer;
        }

        /// <summary>Opens the connection. BeginSession is emitted by the streamer on success.</summary>
        public async void StartAsync()
        {
            // async void: nothing awaits session startup, so the body is fully wrapped.
            try
            {
                if (await _streamer.ConnectAsync()) return;
                BeginReconnect();
            }
            catch (Exception ex)
            {
                Debug.LogError($"MoodyBluesMaintenanceSystem: failed to start the session - {ex}");
            }
        }

        /// <summary>
        /// Called once per frame by MoodyBluesSessionManager.
        /// </summary>
        /// <param name="unscaledDeltaTime">
        /// Real elapsed time, deliberately NOT scaled: a game paused with timeScale 0 still has to
        /// hold its connection open, and a scaled delta would stop the keepalive exactly when the
        /// capture loop also goes quiet -- the one combination guaranteed to trip the receiver's
        /// inactivity timeout.
        /// </param>
        /// <param name="gameTimeSeconds">
        /// Game clock for the TimeStamp accompanying the keepalive, matching every other event.
        /// During a pause this repeats, which correctly tells the receiver time is not advancing.
        /// </param>
        public void Tick(float unscaledDeltaTime, double gameTimeSeconds)
        {
            if (_ended) return;

            _keepAliveAccumulator += unscaledDeltaTime;
            if (_keepAliveAccumulator < KeepAliveIntervalSeconds) return;
            _keepAliveAccumulator = 0.0;

            if (!_streamer.IsConnected)
            {
                BeginReconnect();
                return;
            }

            _reconnectAttempt = 0;
            _streamer.EnqueueKeepAlive(gameTimeSeconds);

            // Push whatever is pending, not just a full packet. Bounds crash loss to one interval.
            _streamer.FlushTail();
        }

        /// <summary>
        /// Emits EndSession, flushes it, and closes the socket. Idempotent.
        ///
        /// A stream that stops *without* EndSession is meaningful to a receiver -- it means the
        /// session was truncated rather than finished -- so this must only be called on a genuine,
        /// orderly end.
        /// </summary>
        public void End(Serializer.SessionEndReason reason, long deltaPromotions, long rotationWidenings, long hardClamps)
        {
            if (_ended) return;
            _ended = true;

            try
            {
                if (_streamer.IsConnected)
                {
                    _streamer.EnqueueEndSession(reason, deltaPromotions, rotationWidenings, hardClamps);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"MoodyBluesMaintenanceSystem: could not write EndSession - {ex.Message}");
            }

            // Shutdown flushes synchronously, so the EndSession just written goes out with it.
            _streamer.Shutdown(CloseTimeout);
            _streamer.Dispose();
        }

        private async void BeginReconnect()
        {
            // async void: fire-and-forget retry loop, fully wrapped.
            if (_reconnecting || _ended) return;
            _reconnecting = true;

            try
            {
                while (!_ended && _reconnectAttempt < ReconnectBackoffMillis.Length)
                {
                    int delay = ReconnectBackoffMillis[_reconnectAttempt];
                    _reconnectAttempt++;

                    _streamer.NotifyReconnecting();
                    Debug.LogWarning(
                        $"MoodyBluesMaintenanceSystem: connection lost, retrying in {delay}ms " +
                        $"(attempt {_reconnectAttempt}/{ReconnectBackoffMillis.Length}). Events keep buffering meanwhile.");

                    await Task.Delay(delay);
                    if (_ended) return;

                    if (await _streamer.ConnectAsync())
                    {
                        Debug.Log("MoodyBluesMaintenanceSystem: reconnected.");
                        _reconnectAttempt = 0;
                        return;
                    }
                }

                if (!_ended)
                {
                    _streamer.NotifyFailed();

                    // The receiver's handshake token is single-use, so a reconnect currently needs
                    // a matching backend change (a resumable session) before it can succeed. Said
                    // plainly here rather than left as a silent retry failure.
                    Debug.LogError(
                        "MoodyBluesMaintenanceSystem: gave up reconnecting. Recording has stopped for this run. " +
                        "Note that resuming a dropped connection also requires backend support for reclaiming a " +
                        "handshake session token, which is not in place yet.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"MoodyBluesMaintenanceSystem: reconnect loop failed - {ex}");
            }
            finally
            {
                _reconnecting = false;
            }
        }
    }
}
