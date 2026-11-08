using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MoodyBlues
{
    /// <summary>Outcome of <see cref="MoodyBluesSessionManager.SendScene"/>.</summary>
    public enum SceneSendResult
    {
        /// <summary>The backend had no matching export, and this scene was uploaded.</summary>
        Uploaded,

        /// <summary>The backend already holds an export matching this scene. Nothing was sent.</summary>
        AlreadyOnServer,

        /// <summary>The check, the export or the upload failed. The console has the reason.</summary>
        Failed,

        /// <summary>
        /// The backend needs this scene version, but this run may not export it (runtime export is
        /// off for this context, or the scene changed too much since Start). Recording carries on;
        /// the sessions wait on the backend until the version is exported from Tools > MoodyBlues.
        /// </summary>
        NotExported,
    }

    /// <summary>Outcome of <see cref="MoodyBluesSessionManager.StopRecording"/>.</summary>
    public enum StopResult
    {
        /// <summary>Every recorded event, EndSession included, reached the backend and was stored.</summary>
        Completed,

        /// <summary>
        /// The recording ended but its tail may be missing (flush failed, close timed out, or the
        /// connection was already down). The backend marks the session as truncated.
        /// </summary>
        Incomplete,

        /// <summary>Nothing was recording, or the recording had not started yet and was cancelled.</summary>
        NotRecording,
    }

    /// <summary>
    /// Recording lifecycle: the public recording API, the start-of-recording snapshot, and scene
    /// upload. See Spec.md Section 9.
    ///
    /// The scene's ObjectIDs, capture set and scene export live for the whole run of the scene.
    /// Each StartRecording opens a new backend session on top of them: a fresh handshake, a fresh
    /// connection, and a snapshot that brings the receiver from the scene's load-time state (what
    /// the uploaded export describes) to the scene as it is right now.
    /// </summary>
    public partial class MoodyBluesSessionManager
    {
        /// <summary>
        /// How long a starting recording has to reach the backend (handshake and WebSocket
        /// connect together). Past this the start fails and nothing is recorded.
        /// </summary>
        public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

        /// <summary>Budget for SendScene's check request.</summary>
        private static readonly TimeSpan SceneCheckTimeout = TimeSpan.FromSeconds(60);

        /// <summary>Logs held while a recording is starting. Further logs are dropped.</summary>
        private const int MaxLogsWhileStarting = 256;

        private enum RecordingPhase
        {
            Idle,

            /// <summary>StartRecording was called and the handshake is in flight.</summary>
            Handshaking,

            /// <summary>A streamer exists. It may still be connecting (see _startConfirmed).</summary>
            Streaming,
        }

        private RecordingPhase _phase = RecordingPhase.Idle;

        // Bumped whenever a recording ends or a start is abandoned, so async work belonging to an
        // older recording (a handshake still in flight, say) can tell it is stale and do nothing.
        private int _recordingGeneration;

        private DateTime _startDeadlineUtc;
        private bool _startConfirmed;
        private Action<bool> _onStarted;
        private readonly List<(LogLevel Level, LogColor Color, string Message)> _logsWhileStarting = new();

        // ---- Captured once at Start --------------------------------------------------------------
        private bool _initialized;
        private readonly List<Action> _callsBeforeStart = new();
        private string _sceneId;
        private string _sceneHash;
        private byte[] _sceneGlb;
        private int _sceneSkippedMeshes;
        private bool _sceneKnownOnServer;
        private bool _warnedNotExported;
        private Task<SceneSendResult> _sceneSendTask;

        /// <summary>
        /// Every object tracked when Start ran, as the scene export describes it. The snapshot
        /// compares the live scene against this, because it is what the backend starts each
        /// session from.
        /// </summary>
        private readonly Dictionary<ushort, LoadState> _loadStates = new();
        private ushort[] _loadOrder = Array.Empty<ushort>();

        private struct LoadState
        {
            public TrackedState Pose;
            public Transform Parent;
            public ushort TrackedAncestorId;
            public bool ActiveSelf;
        }

        /// <summary>True from StartRecording until the recording stops or fails to start.</summary>
        public bool IsRecording => _phase != RecordingPhase.Idle;

        /// <summary>This scene's config asset.</summary>
        public MoodyBluesClientConfig Config => config;

        // ---- Public API -------------------------------------------------------------------------

        /// <summary>
        /// Makes sure the backend holds this scene's export, uploading it only if the backend's
        /// copy is missing or stale. <paramref name="onDone"/> is called on the main thread.
        /// StartRecording does the same check on its own, so calling this first is optional; it
        /// is useful for uploading during a loading screen rather than mid-game.
        /// </summary>
        public void SendScene(Action<SceneSendResult> onDone = null)
        {
            if (_inert)
            {
                Debug.LogError("MoodyBluesSessionManager.SendScene: recording is disabled for this scene (see earlier errors).");
                InvokeCallback(onDone, SceneSendResult.Failed);
                return;
            }

            if (!_initialized)
            {
                _callsBeforeStart.Add(() => SendScene(onDone));
                return;
            }

            AwaitSceneSend(EnsureSceneOnServerAsync(knownUploadUrl: null), onDone);
        }

        /// <summary>
        /// Starts a new recording (a new backend session). Returns immediately; the handshake,
        /// any needed scene upload and the connection happen in the background.
        /// <paramref name="onStarted"/> is called on the main thread with true once the connection
        /// is open, or false if the backend could not be reached within <see cref="StartTimeout"/>,
        /// the start was cancelled by StopRecording, or a recording is already running.
        ///
        /// Recording time 0 is the moment the handshake completes. The first events of the
        /// session are a snapshot of every change since the scene loaded, so spawns, deletes and
        /// moves made while nothing was recording still appear in the replay.
        /// </summary>
        public void StartRecording(Action<bool> onStarted = null)
        {
            if (_inert)
            {
                Debug.LogError("MoodyBluesSessionManager.StartRecording: recording is disabled for this scene (see earlier errors).");
                InvokeCallback(onStarted, false);
                return;
            }

            if (!_initialized)
            {
                // Called before this component's Start (it runs late, see DefaultExecutionOrder).
                _callsBeforeStart.Add(() => StartRecording(onStarted));
                return;
            }

            if (!isActiveAndEnabled)
            {
                Debug.LogError("MoodyBluesSessionManager.StartRecording: the component is disabled, so it cannot capture.");
                InvokeCallback(onStarted, false);
                return;
            }

            if (_phase != RecordingPhase.Idle)
            {
                Debug.LogWarning("MoodyBluesSessionManager.StartRecording: a recording is already running. Call StopRecording first.");
                InvokeCallback(onStarted, false);
                return;
            }

            int generation = ++_recordingGeneration;
            _phase = RecordingPhase.Handshaking;
            _startConfirmed = false;
            _onStarted = onStarted;
            _startDeadlineUtc = DateTime.UtcNow + StartTimeout;
            _logsWhileStarting.Clear();

            RunStartAsync(generation);
        }

        /// <summary>
        /// Ends the current recording without blocking. Writes EndSession, sends everything still
        /// buffered and closes the connection in the background. <paramref name="onDone"/> is
        /// called on the main thread once the backend has acknowledged the close (Completed), or
        /// when the tail could not be confirmed (Incomplete). A new recording may be started
        /// straight away; it does not wait for this one to finish closing.
        ///
        /// On Quest and mobile, call this from OnApplicationPause(true): the OS may kill a
        /// backgrounded app without OnApplicationQuit ever running.
        /// </summary>
        public void StopRecording(Action<StopResult> onDone = null)
        {
            if (_phase == RecordingPhase.Idle)
            {
                InvokeCallback(onDone, StopResult.NotRecording);
                return;
            }

            if (_phase == RecordingPhase.Handshaking || !_startConfirmed)
            {
                AbandonStart("StopRecording was called before the recording finished starting.", logAsError: false);
                InvokeCallback(onDone, StopResult.NotRecording);
                return;
            }

            MoodyBluesMaintenanceSystem maintenance = _maintenance;
            DetachRecording();

            // Once detached nothing else writes into this streamer, so EndAsync can write
            // EndSession now or, during a reconnect, once the connection is back.
            Task<bool> ending = maintenance.EndAsync(
                Serializer.SessionEndReason.StoppedByHost, _deltaPromotionCount, _rotationWidenCount, _hardClampCount);
            AwaitStop(ending, onDone);
        }

        // ---- Internal API -----------------------------------------------------------------------

        /// <summary>Entry point for <see cref="MoodyBluesLog"/>. Main thread only.</summary>
        internal void EnqueueLog(LogLevel level, LogColor color, string message)
        {
            if (_inert) return;

            switch (_phase)
            {
                case RecordingPhase.Handshaking:
                    if (_logsWhileStarting.Count < MaxLogsWhileStarting) _logsWhileStarting.Add((level, color, message));
                    break;

                case RecordingPhase.Streaming:
                    DataStreamer.EnqueueLog(level, color, message);
                    break;
            }
        }

        // ---- Lifecycle --------------------------------------------------------------------------

        /// <summary>
        /// Runs once from Start. Captures everything that describes the scene "as loaded": each
        /// object's load-time state and the scene hash, taken in the same frame so they agree;
        /// Start's late execution order is what pins that frame down (see the class remarks in
        /// MoodyBluesSessionManager.cs).
        ///
        /// No export happens here unless the config asks for one on every run (Always Export at
        /// Runtime). Otherwise, when runtime export is allowed, the scene check starts now and the
        /// scene is exported only if the backend turns out not to have it.
        /// </summary>
        private void InitializeRecorder()
        {
            _sceneId = SceneIdentity.GetCurrentSceneId();
            if (string.IsNullOrEmpty(_sceneId))
            {
                // Already logged by SceneIdentity. Without a scene ID the backend cannot file
                // anything, so behave as if recording were switched off.
                _inert = true;
                FailCallsBeforeStart();
                return;
            }

            CaptureLoadStates();

            Scene scene = SceneManager.GetActiveScene();
            string structuralHash = SceneHasher.ComputeSceneHash(this);
            string contentHash = SceneContentHash.For(scene);
            string exportHash = null;

            if (config.AlwaysExportAtRuntime && config.RuntimeExportAllowed && TryExportScene())
            {
                // Generated content can differ between runs with identical assets and layout; the
                // export itself is the only thing that tells them apart.
                exportHash = SceneHasher.HashBytes(_sceneGlb);
            }

            _sceneHash = SceneHasher.Combine(contentHash, structuralHash, exportHash);
            _initialized = true;

            if (config.RuntimeExportAllowed)
            {
                // Concurrent callers (the handshake's own upload path, SendScene) share this check.
                AwaitSceneSend(EnsureSceneOnServerAsync(knownUploadUrl: null), null);
            }

            List<Action> pending = new(_callsBeforeStart);
            _callsBeforeStart.Clear();
            foreach (Action call in pending) call();

            if (config.RecordingMode == RecordingMode.StartOnLoad && _phase == RecordingPhase.Idle)
            {
                StartRecording();
            }
        }

        private void FailCallsBeforeStart()
        {
            // With _inert set, each queued call reports failure through its callback.
            List<Action> pending = new(_callsBeforeStart);
            _callsBeforeStart.Clear();
            foreach (Action call in pending) call();
        }

        private void CaptureLoadStates()
        {
            _loadStates.Clear();
            var order = new List<ushort>(_transformById.Count);

            // Registration order, which is ascending ObjectID order.
            foreach (ushort id in _registrationOrder)
            {
                if (!_transformById.TryGetValue(id, out Transform t) || t == null) continue;

                _loadStates[id] = new LoadState
                {
                    Pose = TrackedState.FromTransform(t),
                    Parent = t.parent,
                    TrackedAncestorId = FindTrackedAncestorId(t),
                    ActiveSelf = t.gameObject.activeSelf,
                };
                order.Add(id);
            }

            _loadOrder = order.ToArray();
        }

        private ushort FindTrackedAncestorId(Transform t)
        {
            for (Transform p = t.parent; p != null; p = p.parent)
            {
                if (_idByTransform.TryGetValue(p, out ushort id)) return id;
            }
            return 0;
        }

        private async void RunStartAsync(int generation)
        {
            // async void: nothing awaits this, so everything is wrapped.
            try
            {
                var request = new HandshakeRequest
                {
                    developerId = config.DeveloperId,
                    sceneId = _sceneId,
                    sceneHash = _sceneHash,
                    sessionId = Guid.NewGuid().ToString(),
                };

                HandshakeResponse response = await MoodyBluesHandshakeClient.HandshakeAsync(
                    config, request, _startDeadlineUtc, () => generation != _recordingGeneration);

                if (generation != _recordingGeneration || this == null) return; // stopped meanwhile

                if (response == null)
                {
                    AbandonStart("MoodyBluesSessionManager: the handshake failed, so this recording was not started.", logAsError: true);
                    return;
                }

                Uri webSocketUri;
                try
                {
                    webSocketUri = new Uri(response.webSocketUrl);
                }
                catch (Exception ex)
                {
                    AbandonStart($"MoodyBluesSessionManager: backend returned an invalid webSocketUrl '{response.webSocketUrl}' - {ex.Message}", logAsError: true);
                    return;
                }

                BeginStreaming(webSocketUri);

                if (response.sceneUploadRequired)
                {
                    AwaitSceneSend(EnsureSceneOnServerAsync(response.sceneUploadUrl), null);
                }
            }
            catch (Exception ex)
            {
                if (generation == _recordingGeneration && this != null)
                {
                    AbandonStart($"MoodyBluesSessionManager: starting the recording failed unexpectedly - {ex}", logAsError: true);
                }
            }
        }

        /// <summary>
        /// Handshake done: create the transport, write the snapshot into its buffer, and start
        /// connecting. The buffer is sent once the socket opens, right after BeginSession.
        /// </summary>
        private void BeginStreaming(Uri webSocketUri)
        {
            // Everything registered or destroyed since the last tick must be in the capture set
            // before the snapshot walks it.
            DrainPendingMutations();
            if (_inert) return;

            var streamer = new MoodyBluesStreamer(webSocketUri, captureHz, config.MessageSizeBytes);
            var maintenance = new MoodyBluesMaintenanceSystem(streamer);

            ResetSessionDiagnostics();
            WriteStartSnapshot(streamer);

            DataStreamer = streamer;
            _maintenance = maintenance;
            _phase = RecordingPhase.Streaming;
            _captureAccumulator = 0.0;
            _forceAbsoluteThisTick = false;

            foreach ((LogLevel level, LogColor color, string message) in _logsWhileStarting)
            {
                streamer.EnqueueLog(level, color, message);
            }
            _logsWhileStarting.Clear();

            maintenance.StartAsync(_startDeadlineUtc);
        }

        /// <summary>Called every frame from LateUpdate.</summary>
        private void UpdateRecordingLifecycle()
        {
            switch (_phase)
            {
                case RecordingPhase.Handshaking:
                    // The handshake client has its own deadline; this is the backstop.
                    if (DateTime.UtcNow > _startDeadlineUtc + TimeSpan.FromSeconds(5))
                    {
                        AbandonStart("MoodyBluesSessionManager: could not reach the backend within the start timeout.", logAsError: true);
                    }
                    break;

                case RecordingPhase.Streaming when !_startConfirmed:
                    if (DataStreamer.State == RecordingState.Recording)
                    {
                        _startConfirmed = true;
                        Action<bool> callback = _onStarted;
                        _onStarted = null;
                        InvokeCallback(callback, true);
                    }
                    else if (DataStreamer.State == RecordingState.Failed || DateTime.UtcNow > _startDeadlineUtc)
                    {
                        AbandonStart("MoodyBluesSessionManager: could not connect to the backend within the start timeout.", logAsError: true);
                    }
                    break;

                case RecordingPhase.Streaming:
                    if (DataStreamer.State == RecordingState.Failed)
                    {
                        // The maintenance system has already logged why it gave up.
                        Debug.LogError("MoodyBluesSessionManager: the connection was lost and could not be re-established. This recording has ended.");
                        MoodyBluesMaintenanceSystem maintenance = _maintenance;
                        DetachRecording();
                        maintenance.Abort();
                    }
                    break;
            }
        }

        /// <summary>Gives up on a recording that has not finished starting.</summary>
        private void AbandonStart(string message, bool logAsError)
        {
            if (logAsError) Debug.LogError(message);
            else Debug.Log($"MoodyBluesSessionManager: {message}");

            MoodyBluesMaintenanceSystem maintenance = _maintenance;
            Action<bool> callback = _onStarted;
            _onStarted = null;

            DetachRecording();
            maintenance?.Abort();

            InvokeCallback(callback, false);
        }

        /// <summary>
        /// Synchronous stop for OnDisable/OnDestroy/OnApplicationQuit, where Unity will not wait for
        /// anything asynchronous. Blocks for at most the close timeout.
        /// </summary>
        private void StopRecordingImmediate(Serializer.SessionEndReason reason)
        {
            if (_phase == RecordingPhase.Idle) return;

            if (_phase == RecordingPhase.Handshaking || !_startConfirmed)
            {
                AbandonStart("the recorder was disabled or destroyed before the recording finished starting.", logAsError: false);
                return;
            }

            MoodyBluesMaintenanceSystem maintenance = _maintenance;
            DetachRecording();
            maintenance.End(reason, _deltaPromotionCount, _rotationWidenCount, _hardClampCount);
        }

        /// <summary>
        /// Unrecoverable client-side error: end any recording and stop recording for this run.
        /// </summary>
        private void FailFatally(Serializer.SessionEndReason reason)
        {
            StopRecordingImmediate(reason);
            _inert = true;
        }

        /// <summary>Returns to Idle. The caller owns the detached maintenance system.</summary>
        private void DetachRecording()
        {
            _recordingGeneration++;
            _phase = RecordingPhase.Idle;
            _startConfirmed = false;
            DataStreamer = null;
            _maintenance = null;
            _logsWhileStarting.Clear();
        }

        private void ResetSessionDiagnostics()
        {
            _deltaPromotionCount = 0;
            _rotationWidenCount = 0;
            _hardClampCount = 0;
            _warnedPromotion = false;
            _warnedHardClamp = false;
        }

        // ---- Start-of-recording snapshot ------------------------------------------------------

        /// <summary>
        /// Writes the events that take the receiver from the scene as exported (its state at
        /// Start) to the scene as it is now. Every event shares one TimeStamp, which becomes the
        /// session's time 0. In order:
        ///
        /// 1. DeleteObject for load-time objects that no longer exist (topmost only; the receiver
        ///    cascades to descendants).
        /// 2. InstantiateObject for every live spawn, ascending ObjectID. A spawn whose parent is
        ///    a later spawn is created at the root in world space and reparented in step 3.
        /// 3. ReparentObject for those spawns, and for load-time objects whose parent changed.
        /// 4. TrueTransform for load-time objects that moved.
        /// 5. Show/Hide for load-time objects whose active state changed; Hide for inactive spawns.
        ///
        /// Afterwards every object's baseline is what the receiver now holds: the encoded values
        /// for objects that were sent, the load-time pose for objects that were not.
        /// </summary>
        private void WriteStartSnapshot(MoodyBluesStreamer streamer)
        {
            streamer.EnqueueTimeStamp(Time.timeAsDouble);

            var baselines = new Dictionary<ushort, TrackedState>(_transformById.Count);

            // 1. Deletes.
            foreach (ushort id in _loadOrder)
            {
                if (IsAlive(id)) continue;

                ushort ancestor = _loadStates[id].TrackedAncestorId;
                if (ancestor != 0 && _loadStates.ContainsKey(ancestor) && !IsAlive(ancestor)) continue;

                streamer.EnqueueDeleteObject(id, stampTime: false);
            }

            // 2. Spawns. A spawned subtree is one InstantiateObject for its root; the receiver creates
            // the children from the template (ObjectIDs root + 1 ...), so they are skipped here and
            // described right after their root.
            MoodyBluesRuntimeManager runtime = MoodyBluesRuntimeManager.Instance;
            var spawnIds = new List<ushort>();
            foreach (KeyValuePair<ushort, Transform> entry in _transformById)
            {
                if (entry.Value == null || _loadStates.ContainsKey(entry.Key)) continue;
                if (runtime != null && runtime.IsSpawnPart(entry.Key)) continue;
                spawnIds.Add(entry.Key);
            }
            spawnIds.Sort();

            var emittedSpawns = new HashSet<ushort>();
            var emittedRoots = new List<ushort>();
            var deferredReparents = new List<(ushort Id, ushort ParentId)>();
            var deadParts = new List<ushort>();
            bool warnedUnknownSpawn = false;

            foreach (ushort id in spawnIds)
            {
                Transform t = _transformById[id];

                if (runtime == null || !runtime.TryGetSpawnTemplateId(id, out ushort templateId))
                {
                    if (!warnedUnknownSpawn)
                    {
                        warnedUnknownSpawn = true;
                        Debug.LogWarning(
                            $"MoodyBluesSessionManager: ObjectID {id} ('{t.name}') was registered at runtime without " +
                            "MoodyBluesRuntimeManager.InstantiateObject, so the snapshot cannot say what to spawn. " +
                            "It will be missing from this replay. Reported once per recording.");
                    }
                    continue;
                }

                ushort parentId = 0;
                bool deferParent = false;
                if (t.parent != null && _idByTransform.TryGetValue(t.parent, out ushort trackedParent))
                {
                    if (_loadStates.ContainsKey(trackedParent) || emittedSpawns.Contains(trackedParent)) parentId = trackedParent;
                    else deferParent = true;
                }

                if (parentId == 0)
                {
                    // At the root on the receiver, so the pose has to be in world space.
                    streamer.EnqueueInstantiateObject(id, templateId, 0, t.position, t.rotation, t.lossyScale, stampTime: false);
                }
                else
                {
                    streamer.EnqueueInstantiateObject(id, templateId, parentId, t.localPosition, t.localRotation, t.localScale, stampTime: false);
                }

                emittedSpawns.Add(id);
                emittedRoots.Add(id);
                baselines[id] = TrackedState.FromTransform(t);
                if (deferParent) deferredReparents.Add((id, _idByTransform[t.parent]));

                DescribeSpawnPartsForSnapshot(streamer, runtime, id, emittedSpawns, deferredReparents, deadParts);
            }

            // 3. Reparents.
            foreach ((ushort id, ushort parentId) in deferredReparents)
            {
                Transform t = _transformById[id];
                TransformEncodeResult encoded = streamer.EnqueueReparentObject(
                    id, parentId, t.localPosition, t.localRotation, t.localScale, stampTime: false);
                AccumulateEncodeDiagnostics(encoded, id);
                baselines[id] = ToTrackedState(encoded);
            }

            // Spawn children destroyed while idle. After the reparents, so a child that was moved out
            // from under a destroyed sibling is not taken down with it. Deleting an already-deleted
            // child is a no-op on the receiver.
            foreach (ushort id in deadParts)
            {
                streamer.EnqueueDeleteObject(id, stampTime: false);
            }

            foreach (ushort id in _loadOrder)
            {
                if (!TryGetTransform(id, out Transform t)) continue;

                LoadState load = _loadStates[id];
                if (t.parent == load.Parent) continue;

                ushort parentId = 0;
                if (t.parent != null) _idByTransform.TryGetValue(t.parent, out parentId);

                TransformEncodeResult encoded = streamer.EnqueueReparentObject(
                    id, parentId, t.localPosition, t.localRotation, t.localScale, stampTime: false);
                AccumulateEncodeDiagnostics(encoded, id);
                baselines[id] = ToTrackedState(encoded);
            }

            // 4. Moves.
            foreach (ushort id in _loadOrder)
            {
                if (baselines.ContainsKey(id)) continue; // reparented: that event carried the pose
                if (!TryGetTransform(id, out Transform t)) continue;

                TrackedState load = _loadStates[id].Pose;
                Vector3 position = t.localPosition;
                Quaternion rotation = t.localRotation;
                Vector3 scale = t.localScale;

                bool moved =
                    TransformEventDispatcher.HasPositionChanged(position, load.LastSentPosition) ||
                    TransformEventDispatcher.HasRotationChanged(rotation, load.LastSentRotation) ||
                    TransformEventDispatcher.HasScaleChanged(scale, load.LastSentScale);

                if (!moved)
                {
                    baselines[id] = load;
                    continue;
                }

                var data = new TransformData
                {
                    ObjectId = id,
                    SendTrue = true,
                    PositionChanged = true,
                    RotationChanged = true,
                    ScaleChanged = true,
                    currentPosition = position,
                    currentRotation = rotation,
                    currentScale = scale,
                    previousPosition = load.LastSentPosition,
                    previousRotation = load.LastSentRotation,
                    previousScale = load.LastSentScale,
                };

                TransformEncodeResult encoded = streamer.EnqueueTransform(data);
                if (encoded.Wrote)
                {
                    AccumulateEncodeDiagnostics(encoded, id);
                    baselines[id] = ToTrackedState(encoded);
                }
                else
                {
                    baselines[id] = load;
                }
            }

            // 5. Visibility.
            foreach (ushort id in _loadOrder)
            {
                if (!TryGetTransform(id, out Transform t)) continue;

                bool active = t.gameObject.activeSelf;
                if (active == _loadStates[id].ActiveSelf) continue;

                if (active) streamer.EnqueueShowObject(id, stampTime: false);
                else streamer.EnqueueHideObject(id, stampTime: false);
            }

            foreach (ushort id in emittedRoots)
            {
                if (!_transformById[id].gameObject.activeSelf) streamer.EnqueueHideObject(id, stampTime: false);
            }

            // Baselines.
            foreach (KeyValuePair<ushort, TrackedState> entry in baselines)
            {
                if (_indexById.TryGetValue(entry.Key, out int index)) _states[index] = entry.Value;
            }

#if UNITY_EDITOR
            // Parents recorded during an earlier recording are stale now; sanctioned reparents made
            // while idle would otherwise be reported as bypassed.
            _lastKnownParent.Clear();
#endif
        }

        private bool IsAlive(ushort id) => _transformById.TryGetValue(id, out Transform t) && t != null;

        /// <summary>
        /// Snapshot step for a just-written spawn root: the children's pose and visibility (where
        /// they differ from the template), and, for children reparented or destroyed while nothing
        /// was recording, the reparent (deferred to step 3) or delete (after step 3) that brings
        /// the receiver's template-built copy in line.
        /// </summary>
        private void DescribeSpawnPartsForSnapshot(
            MoodyBluesStreamer streamer,
            MoodyBluesRuntimeManager runtime,
            ushort rootId,
            HashSet<ushort> emittedSpawns,
            List<(ushort Id, ushort ParentId)> deferredReparents,
            List<ushort> deadParts)
        {
            if (!runtime.TryGetSpawnSubtree(rootId, out MoodyBluesRuntimeManager.TemplateNode[] nodes)) return;

            runtime.DescribeSpawnParts(streamer, rootId);

            for (int i = 1; i < nodes.Length; i++)
            {
                var id = (ushort)(rootId + i);
                if (!TryGetTransform(id, out Transform t))
                {
                    deadParts.Add(id);
                    continue;
                }

                emittedSpawns.Add(id);

                var templateParentId = (ushort)(rootId + nodes[i].ParentIndex);
                ushort parentId = 0;
                if (t.parent != null) _idByTransform.TryGetValue(t.parent, out parentId);
                if (parentId != templateParentId) deferredReparents.Add((id, parentId));
            }
        }

        private static TrackedState ToTrackedState(in TransformEncodeResult encoded) => new TrackedState
        {
            LastSentPosition = encoded.Position,
            LastSentRotation = encoded.Rotation,
            LastSentScale = encoded.Scale,
        };

        // ---- Scene upload -----------------------------------------------------------------------

        /// <summary>
        /// Uploads the scene export if the backend lacks it. With <paramref name="knownUploadUrl"/>
        /// (from a handshake that already said an upload is needed) the check is skipped. Only one
        /// check/upload runs at a time; concurrent callers share it.
        /// </summary>
        private async Task<SceneSendResult> EnsureSceneOnServerAsync(string knownUploadUrl)
        {
            if (_sceneKnownOnServer) return SceneSendResult.AlreadyOnServer;
            if (_sceneSendTask != null) return await _sceneSendTask;

            _sceneSendTask = CheckAndUploadSceneAsync(knownUploadUrl);
            try
            {
                return await _sceneSendTask;
            }
            finally
            {
                _sceneSendTask = null;
            }
        }

        private async Task<SceneSendResult> CheckAndUploadSceneAsync(string uploadUrl)
        {
            if (string.IsNullOrEmpty(uploadUrl))
            {
                var request = new SceneCheckRequest
                {
                    developerId = config.DeveloperId,
                    sceneId = _sceneId,
                    sceneHash = _sceneHash,
                };

                SceneCheckResponse response = await MoodyBluesHandshakeClient.CheckSceneAsync(
                    config, request, DateTime.UtcNow + SceneCheckTimeout);
                if (response == null) return SceneSendResult.Failed;

                if (!response.sceneUploadRequired)
                {
                    MarkSceneOnServer();
                    return SceneSendResult.AlreadyOnServer;
                }

                uploadUrl = response.sceneUploadUrl;
            }

            if (_sceneGlb == null && !TryExportForUpload()) return SceneSendResult.NotExported;
            if (_sceneGlb == null) return SceneSendResult.Failed; // the export threw; already logged

            if (!await MoodyBluesHandshakeClient.UploadSceneAsync(uploadUrl, _sceneGlb, _sceneSkippedMeshes)) return SceneSendResult.Failed;

            MarkSceneOnServer();
            return SceneSendResult.Uploaded;
        }

        /// <summary>
        /// Called when the backend turns out to need this scene version and nothing has been
        /// exported yet. Exports the scene as it was at Start, if this run is allowed to.
        /// Returns false (after a warning) if it may not export; true otherwise, even if the export
        /// itself failed.
        /// </summary>
        private bool TryExportForUpload()
        {
            if (!config.RuntimeExportAllowed)
            {
                WarnNotExported(Application.isEditor && config.ExportSceneOnRuntimeStart
                    ? "runtime export is off in Play mode (Export Scene from Editor is unticked on the config)"
                    : "Export Scene on Runtime Start is off on the config");
                return false;
            }

            if (HasStructureChangedSinceStart(out string change))
            {
                WarnNotExported($"the scene has changed since Start ({change}), so it can no longer be exported as it loaded");
                return false;
            }

            TryExportScene();
            return true;
        }

        private void WarnNotExported(string reason)
        {
            if (_warnedNotExported) return;
            _warnedNotExported = true;
            Debug.LogWarning(
                $"MoodyBluesSessionManager: the backend does not have this version of scene '{SceneManager.GetActiveScene().name}', " +
                $"and it was not exported because {reason}. Recording continues; its sessions wait on the backend " +
                "until the version is exported with Tools > MoodyBlues > Export This Scene (or a build).");
        }

        /// <summary>
        /// Exports the scene's load-time objects with their load-time pose and visibility, into
        /// _sceneGlb. Objects spawned since Start are left out. Poses and visibility can be
        /// rewritten in the export; structure cannot, which is why TryExportForUpload checks it.
        /// </summary>
        private bool TryExportScene()
        {
            var ids = new Dictionary<Transform, ushort>(_loadOrder.Length);
            var overrides = new Dictionary<ushort, ExportNodeState>(_loadOrder.Length);
            foreach (ushort id in _loadOrder)
            {
                if (!TryGetTransform(id, out Transform t)) continue;

                ids[t] = id;
                LoadState load = _loadStates[id];
                overrides[id] = new ExportNodeState(
                    load.Pose.LastSentPosition, load.Pose.LastSentRotation, load.Pose.LastSentScale, load.ActiveSelf);
            }

            try
            {
                _sceneGlb = SceneGltfExporter.Export(SceneManager.GetActiveScene(), ids, overrides, out _sceneSkippedMeshes);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"MoodyBluesSessionManager: scene export failed, so the scene cannot be uploaded - {ex}");
                _sceneGlb = null;
                return false;
            }
        }

        /// <summary>True if a load-time object has been destroyed or reparented since Start.</summary>
        private bool HasStructureChangedSinceStart(out string change)
        {
            foreach (ushort id in _loadOrder)
            {
                if (!TryGetTransform(id, out Transform t))
                {
                    change = $"ObjectID {id} was destroyed";
                    return true;
                }

                if (t.parent != _loadStates[id].Parent)
                {
                    change = $"'{t.name}' was reparented";
                    return true;
                }
            }

            change = null;
            return false;
        }

        private void MarkSceneOnServer()
        {
            _sceneKnownOnServer = true;

            // The export can be tens of megabytes, which matters on a headset. Once the backend
            // has it, it is no longer needed.
            _sceneGlb = null;
        }

        private static async void AwaitSceneSend(Task<SceneSendResult> sending, Action<SceneSendResult> onDone)
        {
            SceneSendResult result;
            try
            {
                result = await sending;
            }
            catch (Exception ex)
            {
                Debug.LogError($"MoodyBluesSessionManager: scene send failed unexpectedly - {ex}");
                result = SceneSendResult.Failed;
            }

            InvokeCallback(onDone, result);
        }

        private static async void AwaitStop(Task<bool> ending, Action<StopResult> onDone)
        {
            StopResult result;
            try
            {
                result = await ending ? StopResult.Completed : StopResult.Incomplete;
            }
            catch (Exception ex)
            {
                Debug.LogError($"MoodyBluesSessionManager: stopping the recording failed unexpectedly - {ex}");
                result = StopResult.Incomplete;
            }

            InvokeCallback(onDone, result);
        }

        /// <summary>Calls a user callback without letting its exception break the recorder.</summary>
        private static void InvokeCallback<T>(Action<T> callback, T value)
        {
            if (callback == null) return;
            try
            {
                callback(value);
            }
            catch (Exception ex)
            {
                Debug.LogError($"MoodyBluesSessionManager: a callback threw - {ex}");
            }
        }
    }
}
