using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Jobs;
using UnityEngine.SceneManagement;

namespace MoodyBlues
{
    /// <summary>
    /// Walks the scene once at startup to assign every relevant Transform a permanent, never-reused
    /// ObjectID, then samples each tracked Transform at a fixed capture rate, diffs it against the
    /// last state sent, and streams the changes via MoodyBluesStreamer. Also the registry
    /// MoodyBluesRuntimeManager uses to register/unregister runtime-spawned objects into the same
    /// ID space. See Spec.md Sections 4-5.
    ///
    /// Governing model (Spec.md Section 1): transform state is *polled* once per capture tick;
    /// every other event is *injected* the instant it happens, carrying its own TimeStamp. That is
    /// why there is no runtime watching for spawns, deletes, or reparents -- the sanctioned
    /// MoodyBluesRuntimeManager wrappers emit those directly.
    /// </summary>
    /// <remarks>
    /// Execution order 1000 puts this after every default-order script, which pins down two
    /// otherwise ambiguous moments:
    ///
    /// - Awake (the ID-assigning walk) sees the scene after any other Awake has spawned into or
    ///   destroyed out of it, so the tracked set is whatever the game actually starts with.
    /// - Start (which computes the scene hash and hands it to the handshake) samples transforms
    ///   after every other Start has run. Without this, a script whose Start nudged an object
    ///   could land either side of the hash depending on Unity's ordering, making the hash vary
    ///   between runs of an unmodified scene and triggering pointless scene re-uploads.
    ///
    /// MoodyBluesRuntimeManager stays at -1000 so its prefab templates exist before the walk.
    /// </remarks>
    [DefaultExecutionOrder(1000)]
    public partial class MoodyBluesSessionManager : MonoBehaviour
    {
        /// <summary>
        /// Capture rate in Hz, deliberately decoupled from the physics timestep. Sampling used to
        /// run in FixedUpdate, which tied data volume to a host-game setting that has nothing to
        /// do with replay fidelity -- a project on a 100Hz fixed timestep paid double for no
        /// visible gain.
        /// </summary>
        public const float DefaultCaptureHz = 40f;

        /// <summary>Transforms handed to one worker per batch by ScheduleReadOnly.</summary>
        private const int DiffJobBatchSize = 32;

        private const int InitialCapacity = 256;

        [Header("Config")]
        [SerializeField]
        [Tooltip("This scene's MoodyBlues config asset (Assets > Create > MoodyBlues > Scene Config). " +
                 "Without one, nothing is recorded.")]
        private MoodyBluesClientConfig config;

        [Header("Capture")]
        [SerializeField]
        [Tooltip("Transform sampling rate in Hz. Independent of the physics timestep. Sampling " +
                 "happens at most once per frame, so below this rate the effective capture rate " +
                 "degrades to the frame rate.")]
        [Range(1f, 120f)]
        private float captureHz = DefaultCaptureHz;

        [SerializeField]
        [Tooltip("Run the per-tick transform diff as a Burst job across worker threads. Turn off " +
                 "to compare against the single-threaded path; see the benchmark notes in Spec.md. " +
                 "The two produce identical output.")]
        private bool useJobDiff = true;

        public static MoodyBluesSessionManager Instance { get; private set; }

        // ---- Identity -------------------------------------------------------------------------
        // Maintained synchronously, so every lookup works the instant RegisterTransform returns --
        // before the next capture tick drains the pending queue below. SceneHasher depends on this
        // during the Awake walk, which happens before any tick has run.
        private readonly Dictionary<Transform, ushort> _idByTransform = new();
        private readonly Dictionary<ushort, Transform> _transformById = new();

        // int rather than ushort so RegisterTransform can detect running past ushort.MaxValue
        // instead of wrapping to 0, which is the reserved skip marker. Starts at 1.
        private int _nextObjectId = 1;
        private bool _warnedIdExhaustion;

        // ---- Capture set (structure of arrays, all index-aligned) -----------------------------
        private TransformAccessArray _transforms;
        private NativeList<TrackedState> _states;
        private NativeList<ushort> _objectIds;
        private NativeList<SampledTransform> _sampled;
        private readonly Dictionary<ushort, int> _indexById = new();

        // ---- Deferred mutation ----------------------------------------------------------------
        // The capture arrays only change at tick start. That keeps the set stable for the whole
        // tick, and batches TransformAccessArray.Add/RemoveAtSwapBack (which touch Unity's
        // transform hierarchy bookkeeping) instead of paying for them one spawn at a time.
        private readonly List<PendingRegistration> _pendingRegistrations = new();
        private readonly List<ushort> _pendingUnregistrations = new();

        // IDs in the exact order RegisterTransform assigned them during the initial scene walk --
        // SceneHasher relies on this order matching the backend's own walk. Snapshotted once
        // Awake's walk finishes, before any runtime spawn can append to _registrationOrder.
        private readonly List<ushort> _registrationOrder = new();
        private ushort[] _initialWalkObjectIds = System.Array.Empty<ushort>();
        public IReadOnlyList<ushort> InitialWalkObjectIds => _initialWalkObjectIds;

        private MoodyBluesStreamer DataStreamer;
        public MoodyBluesStreamer Streamer => DataStreamer;

        private MoodyBluesMaintenanceSystem _maintenance;

        /// <summary>Set for one tick after dropped events broke the delta chain (see ConsumeResyncRequest).</summary>
        private bool _forceAbsoluteThisTick;

        private double _captureAccumulator;

        // Set when there is no config, recording is switched off, or an unrecoverable error
        // happened. Nothing records and nothing contacts the backend for the rest of this run.
        private bool _inert;

        // ---- Encode diagnostics ---------------------------------------------------------------
        // Reported in EndSession so a too-tight quantization range surfaces as a number instead
        // of as motion that silently tops out. The _warned* flags keep the console usable: these
        // conditions repeat every tick once they start.
        private long _deltaPromotionCount;
        private long _rotationWidenCount;
        private long _hardClampCount;
        private bool _warnedPromotion;
        private bool _warnedHardClamp;

        /// <summary>Per-session encode diagnostics. Surfaced in EndSession.</summary>
        public (long DeltaPromotions, long RotationWidenings, long HardClamps) EncodeDiagnostics =>
            (_deltaPromotionCount, _rotationWidenCount, _hardClampCount);

        private double CaptureInterval => 1.0 / Mathf.Max(1f, captureHz);

        private readonly struct PendingRegistration
        {
            public readonly ushort ObjectId;
            public readonly Transform Target;
            public readonly TrackedState Seed;

            public PendingRegistration(ushort objectId, Transform target, TrackedState seed)
            {
                ObjectId = objectId;
                Target = target;
                Seed = seed;
            }
        }

        private void Awake()
        {
            Instance = this;

            if (config == null)
            {
                Debug.LogError(
                    $"MoodyBluesSessionManager on '{name}': no config assigned, so this scene will not be " +
                    "recorded. Create one via Assets > Create > MoodyBlues > Scene Config and assign it.");
            }

            if (config == null || !config.RecordingEnabled)
            {
                // No config, or recording switched off for this build: stay completely inert. No
                // scene walk, no native allocations, no backend calls. Teardown is still safe to
                // reach afterwards -- every step of it is guarded.
                _inert = true;
                enabled = false;
                return;
            }

            // Ranges have to be in place before anything is encoded, and before the scene walk
            // below seeds the baselines the dispatcher will quantize against.
            config.ApplyQuantizationRanges();

            _transforms = new TransformAccessArray(InitialCapacity);
            _states = new NativeList<TrackedState>(InitialCapacity, Allocator.Persistent);
            _objectIds = new NativeList<ushort>(InitialCapacity, Allocator.Persistent);
            _sampled = new NativeList<SampledTransform>(InitialCapacity, Allocator.Persistent);

            // Runs after MoodyBluesRuntimeManager's Awake (DefaultExecutionOrder -1000), so its
            // PrefabInstanceLibrary templates are already in the hierarchy and get picked up here
            // like any other scene object.
            var walk = new List<Transform>(InitialCapacity);
            SceneWalk.Collect(SceneManager.GetActiveScene(), walk);
            foreach (Transform t in walk)
            {
                RegisterTransform(t);
            }

            _initialWalkObjectIds = _registrationOrder.ToArray();
        }

        private void Start()
        {
            if (_inert) return;
            InitializeRecorder();
        }

        /// <summary>
        /// Drives capture on an accumulator rather than a Unity timestep callback.
        ///
        /// LateUpdate specifically: it runs after animation and after every Update, so it observes
        /// the transform state the frame actually rendered -- including physics interpolation --
        /// which is what the player saw and therefore what a replay should reproduce.
        /// </summary>
        private void LateUpdate()
        {
            if (_inert) return;

            MoodyBluesLog.DrainOffThreadQueue(this);
            UpdateRecordingLifecycle();

            // Connection upkeep runs every frame on the real clock, independent of the capture
            // rate: a paused game must still hold its connection open.
            _maintenance?.Tick(Time.unscaledDeltaTime, Time.timeAsDouble);

            // Time.deltaTime (scaled), to match Time.timeAsDouble on the wire. At timeScale 0.5
            // this captures 40 samples per *game* second; at timeScale 0 it captures nothing and
            // the timestamp doesn't advance, so a pause shows up in the replay as a pause.
            _captureAccumulator += Time.deltaTime;

            double interval = CaptureInterval;
            if (_captureAccumulator < interval) return;

            // At most one sample per frame. Reading the same transforms twice within a frame
            // returns identical values, so a catch-up sample would cost a full pass and produce
            // nothing but zero-delta events. The leftover is clamped to a single interval so a
            // long hitch can't build a backlog it could never work off.
            _captureAccumulator -= interval;
            if (_captureAccumulator > interval) _captureAccumulator = interval;

            CaptureTick();
        }

        private void CaptureTick()
        {
            // Drains even when nothing is recording: otherwise the pending queue would grow until
            // the next StartRecording and then land in one spike.
            DrainPendingMutations();

            if (DataStreamer == null) return;
            if (_transforms.length == 0) return;

            // A dropped event breaks every affected object's delta chain, so the receiver's
            // baselines are stale until something absolute re-anchors them. Only worth doing once
            // the socket can actually carry the burst.
            if (DataStreamer.ConsumeResyncRequest() && DataStreamer.IsConnected)
            {
                _forceAbsoluteThisTick = true;
                Debug.LogWarning(
                    $"MoodyBluesSessionManager: re-sending absolute transforms for {_transforms.length} object(s) " +
                    "to recover from dropped events.");
            }

            if (useJobDiff)
            {
                RunDiffJob();
            }
            else
            {
                RunDiffMainThread();
            }

            SerializeChangedTransforms();
            DataStreamer.FlushIfFull();

            _forceAbsoluteThisTick = false;

#if UNITY_EDITOR
            AuditParentsForBypassedReparents();
#endif
        }

#if UNITY_EDITOR
        // Development-only. Catches gameplay code that reparented a tracked object with a raw
        // Transform.SetParent instead of MoodyBluesRuntimeManager.SetObjectParent.
        //
        // Worth the scan because a bypassed reparent has no symptom: the object keeps reporting
        // local-space deltas, they are simply measured in the wrong parent's space from then on,
        // so the recording looks plausible and is wrong. Compiled out of player builds entirely --
        // the sanctioned wrapper is the contract, this just helps you notice you broke it.
        private readonly Dictionary<ushort, Transform> _lastKnownParent = new();
        private int _parentAuditCountdown;

        private void AuditParentsForBypassedReparents()
        {
            // Amortised to roughly once per second rather than every tick: it is a managed walk
            // over the whole capture set, and a bypassed reparent is a code-level mistake that
            // does not need sub-second detection.
            if (--_parentAuditCountdown > 0) return;
            _parentAuditCountdown = Mathf.Max(1, Mathf.RoundToInt(captureHz));

            int count = _transforms.length;
            for (int i = 0; i < count; i++)
            {
                Transform t = _transforms[i];
                if (t == null) continue;

                ushort id = _objectIds[i];
                Transform parent = t.parent;

                if (_lastKnownParent.TryGetValue(id, out Transform previous) && previous != parent)
                {
                    Debug.LogWarning(
                        $"MoodyBluesSessionManager: ObjectID {id} ('{t.name}') changed parent from " +
                        $"'{(previous == null ? "<scene root>" : previous.name)}' to " +
                        $"'{(parent == null ? "<scene root>" : parent.name)}' without going through " +
                        "MoodyBluesRuntimeManager.SetObjectParent. The receiver still thinks it is under " +
                        "the old parent, so its recorded motion will be wrong from here on.");
                }

                _lastKnownParent[id] = parent;
            }
        }
#endif

        private void RunDiffJob()
        {
            var job = new TransformDiffJob
            {
                States = _states.AsArray(),
                Sampled = _sampled.AsArray(),
                PositionEpsilon = TransformEventDispatcher.PositionEpsilon,
                ScaleEpsilon = TransformEventDispatcher.ScaleEpsilon,
                RotationDotEpsilon = TransformEventDispatcher.RotationDotEpsilon,
            };

            // Scheduled and completed within this tick: no user code can run in between, so the
            // capture arrays cannot be mutated underneath the job.
            job.ScheduleReadOnly(_transforms, DiffJobBatchSize).Complete();
        }

        /// <summary>
        /// Single-threaded equivalent of <see cref="TransformDiffJob"/>, byte-for-byte identical in
        /// output. Kept as the comparison baseline for the capture-cost benchmark, and as a
        /// fallback if Burst is unavailable on some target.
        /// </summary>
        private void RunDiffMainThread()
        {
            int count = _transforms.length;
            for (int i = 0; i < count; i++)
            {
                Transform t = _transforms[i];
                Vector3 position = t.localPosition;
                Quaternion rotation = t.localRotation;
                Vector3 scale = t.localScale;

                TrackedState last = _states[i];

                byte mask = 0;
                if (TransformEventDispatcher.HasPositionChanged(position, last.LastSentPosition)) mask |= SampledTransform.PositionChangedBit;
                if (TransformEventDispatcher.HasRotationChanged(rotation, last.LastSentRotation)) mask |= SampledTransform.RotationChangedBit;
                if (TransformEventDispatcher.HasScaleChanged(scale, last.LastSentScale)) mask |= SampledTransform.ScaleChangedBit;

                _sampled[i] = new SampledTransform
                {
                    Position = position,
                    Rotation = rotation,
                    Scale = scale,
                    ChangedMask = mask,
                };
            }
        }

        private void SerializeChangedTransforms()
        {
            // Set the first time this tick actually enqueues something, so exactly one TimeStamp
            // goes out per tick and only for ticks with something to report.
            bool timeStampSentThisTick = false;

            int count = _transforms.length;
            for (int i = 0; i < count; i++)
            {
                SampledTransform sample = _sampled[i];
                if (!_forceAbsoluteThisTick && !sample.AnythingChanged) continue;

                if (!timeStampSentThisTick)
                {
                    DataStreamer.EnqueueTimeStamp(Time.timeAsDouble);
                    timeStampSentThisTick = true;
                }

                TrackedState last = _states[i];

                var data = new TransformData
                {
                    ObjectId = _objectIds[i],

                    // Absolute when recovering from dropped events: the receiver's baseline for
                    // this object is unknown, so a delta against our own baseline would be wrong.
                    SendTrue = _forceAbsoluteThisTick,

                    PositionChanged = sample.PositionChanged,
                    RotationChanged = sample.RotationChanged,
                    ScaleChanged = sample.ScaleChanged,
                    currentPosition = sample.Position,
                    currentRotation = sample.Rotation,
                    currentScale = sample.Scale,
                    previousPosition = last.LastSentPosition,
                    previousRotation = last.LastSentRotation,
                    previousScale = last.LastSentScale,
                };

                TransformEncodeResult encoded = DataStreamer.EnqueueTransform(data);
                if (!encoded.Wrote) continue;

                AccumulateEncodeDiagnostics(encoded, _objectIds[i]);

                // Store what the RECEIVER will reconstruct, not the true float. Delta* events are
                // integrated by the receiver, so feeding the true value back here would leave the
                // two sides' baselines permanently diverging and let the rounding error compound
                // every tick. With the encoded value as the baseline, error stays pinned at a
                // single quantization step no matter how long the session runs.
                _states[i] = new TrackedState
                {
                    LastSentPosition = encoded.Position,
                    LastSentRotation = encoded.Rotation,
                    LastSentScale = encoded.Scale,
                };
            }
        }

        // ---- Capture set maintenance ----------------------------------------------------------

        private void DrainPendingMutations()
        {
            // Registrations before unregistrations: an object registered and then unregistered in
            // the same frame has to net to nothing, and processing removals first would leave the
            // later insertion standing.
            if (_pendingRegistrations.Count > 0)
            {
                foreach (PendingRegistration pending in _pendingRegistrations)
                {
                    if (pending.Target == null) continue;
                    if (_indexById.ContainsKey(pending.ObjectId)) continue;

                    _indexById[pending.ObjectId] = _transforms.length;
                    _transforms.Add(pending.Target);
                    _states.Add(pending.Seed);
                    _objectIds.Add(pending.ObjectId);
                }

                _pendingRegistrations.Clear();
            }

            if (_pendingUnregistrations.Count > 0)
            {
                foreach (ushort id in _pendingUnregistrations)
                {
                    RemoveFromCaptureSet(id);
                }

                _pendingUnregistrations.Clear();
            }

            SweepDestroyedTransforms();

            if (_sampled.Length != _transforms.length)
            {
                _sampled.ResizeUninitialized(_transforms.length);
            }
        }

        /// <summary>
        /// Catches transforms destroyed without going through MoodyBluesRuntimeManager.DeleteObject.
        ///
        /// This is deliberately NOT development-only. DeleteObject now recurses the subtree, so the
        /// sanctioned API unregisters everything it destroys -- but gameplay code calling
        /// <c>Destroy()</c> directly stays possible, and the diff job has no managed access and so
        /// cannot null-check. A dead entry left in the capture set would be sampled as garbage
        /// every tick. One Unity null-check per tracked object per tick is the price of tolerating
        /// raw Destroy(), and it is small next to the transform reads the same pass performs.
        /// </summary>
        private void SweepDestroyedTransforms()
        {
            // TransformAccessArray is maintained by the engine, which receives native notifications
            // when a contained transform is destroyed. If it ever evicts an entry on our behalf,
            // _transforms would shrink while _states/_objectIds did not -- misaligning every index
            // past that point and attributing the wrong ObjectID to the wrong transform, silently.
            // Two integer compares per tick rules that out.
            if (_transforms.length != _states.Length || _transforms.length != _objectIds.Length)
            {
                Debug.LogError(
                    "MoodyBluesSessionManager: capture arrays are misaligned " +
                    $"(transforms={_transforms.length}, states={_states.Length}, ids={_objectIds.Length}). " +
                    "Something mutated the TransformAccessArray outside the drain step. Stopping the " +
                    "recording rather than emitting events against the wrong ObjectIDs.");
                FailFatally(Serializer.SessionEndReason.ClientError);
                return;
            }

            int i = 0;
            while (i < _transforms.length)
            {
                Transform t = _transforms[i];
                if (t != null)
                {
                    i++;
                    continue;
                }

                ushort deadId = _objectIds[i];
                Debug.LogWarning(
                    $"MoodyBluesSessionManager: ObjectID {deadId} was destroyed without going through " +
                    "MoodyBluesRuntimeManager.DeleteObject. Emitting DeleteObject so the recording stays " +
                    "correct, but prefer the sanctioned API -- it also reports the object's descendants.");

                DataStreamer?.EnqueueDeleteObject(deadId);

                _transformById.Remove(deadId);
                _idByTransform.Remove(t); // key is the destroyed wrapper; reference still matches
                RemoveFromCaptureSet(deadId);

                // Do NOT advance: RemoveAtSwapBack moved the last element into slot i.
            }
        }

        private void RemoveFromCaptureSet(ushort id)
        {
            if (!_indexById.TryGetValue(id, out int index)) return;

            int last = _transforms.length - 1;
            ushort movedId = _objectIds[last];

            // Swap-back across all three arrays in lockstep. Intra-tick ordering between distinct
            // objects therefore isn't stable run to run, which carries no meaning: every event in
            // a tick shares that tick's single TimeStamp.
            _transforms.RemoveAtSwapBack(index);
            _states.RemoveAtSwapBack(index);
            _objectIds.RemoveAtSwapBack(index);

            _indexById.Remove(id);
            if (index != last)
            {
                _indexById[movedId] = index;
            }
        }

        // ---- Public registry API --------------------------------------------------------------

        /// <summary>
        /// Registers a whole spawned subtree as one block of consecutive ObjectIDs, in the order
        /// given (walk order, root first). A receiver maps the block onto the template's subtree by
        /// position, so the IDs must be consecutive: if the ID space cannot hold the whole block,
        /// nothing is registered and 0 is returned. Otherwise returns the root's ID.
        /// </summary>
        public ushort RegisterSubtree(IReadOnlyList<Transform> nodes)
        {
            if (nodes == null || nodes.Count == 0) return 0;

            if (_nextObjectId + nodes.Count - 1 > ushort.MaxValue)
            {
                if (!_warnedIdExhaustion)
                {
                    _warnedIdExhaustion = true;
                    Debug.LogError(
                        $"MoodyBluesSessionManager: not enough ObjectIDs left for a {nodes.Count}-object spawn. " +
                        "Further spawns will not be recorded. IDs are never reused -- pool and reuse spawned " +
                        "objects instead of destroying them, or shorten the session.");
                }
                return 0;
            }

            foreach (Transform t in nodes)
            {
                if (t == null || _idByTransform.ContainsKey(t))
                {
                    Debug.LogError("MoodyBluesSessionManager.RegisterSubtree: the subtree contains a missing or already tracked transform.");
                    return 0;
                }
            }

            ushort rootId = 0;
            foreach (Transform t in nodes)
            {
                ushort id = RegisterTransform(t);
                if (rootId == 0) rootId = id;
            }

            return rootId;
        }

        /// <summary>
        /// For a just-spawned clone child: if its local transform differs from the template pose the
        /// receiver assumes for it, sends an absolute transform; either way its baseline becomes
        /// what the receiver now holds. Must be called right after the InstantiateObject that
        /// introduced it, so it shares that event's TimeStamp. Returns true if something was sent.
        /// </summary>
        public bool SendSpawnPoseIfDifferent(MoodyBluesStreamer streamer, ushort id, Vector3 assumedPosition, Quaternion assumedRotation, Vector3 assumedScale)
        {
            if (streamer == null) return false;
            if (!_transformById.TryGetValue(id, out Transform t) || t == null) return false;

            Vector3 position = t.localPosition;
            Quaternion rotation = t.localRotation;
            Vector3 scale = t.localScale;

            bool changed =
                TransformEventDispatcher.HasPositionChanged(position, assumedPosition) ||
                TransformEventDispatcher.HasRotationChanged(rotation, assumedRotation) ||
                TransformEventDispatcher.HasScaleChanged(scale, assumedScale);
            if (!changed)
            {
                // The receiver holds the template pose; keep the baseline identical to it.
                SetBaseline(id, new TrackedState { LastSentPosition = assumedPosition, LastSentRotation = assumedRotation, LastSentScale = assumedScale });
                return false;
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
                previousPosition = assumedPosition,
                previousRotation = assumedRotation,
                previousScale = assumedScale,
            };

            TransformEncodeResult encoded = streamer.EnqueueTransform(data);
            if (!encoded.Wrote) return false;

            AccumulateEncodeDiagnostics(encoded, id);
            SetBaseline(id, new TrackedState
            {
                LastSentPosition = encoded.Position,
                LastSentRotation = encoded.Rotation,
                LastSentScale = encoded.Scale,
            });
            return true;
        }

        /// <summary>Sets an object's delta baseline, whether it is in the capture set yet or still pending.</summary>
        private void SetBaseline(ushort id, TrackedState state)
        {
            if (_indexById.TryGetValue(id, out int index))
            {
                _states[index] = state;
                return;
            }

            for (int i = 0; i < _pendingRegistrations.Count; i++)
            {
                if (_pendingRegistrations[i].ObjectId != id) continue;
                _pendingRegistrations[i] = new PendingRegistration(id, _pendingRegistrations[i].Target, state);
                return;
            }
        }

        /// <summary>
        /// Assigns t a new, permanent ObjectID and queues it for per-tick sampling. Used for both
        /// the initial scene walk and runtime spawns (see MoodyBluesRuntimeManager.InstantiateObject).
        /// The ID and every lookup are live immediately; only entry into the sampling arrays is
        /// deferred to the next capture tick.
        /// </summary>
        public ushort RegisterTransform(Transform t)
        {
            if (t == null)
            {
                Debug.LogError("MoodyBluesSessionManager.RegisterTransform: transform is null.");
                return 0;
            }

            if (_idByTransform.TryGetValue(t, out ushort existing))
            {
                Debug.LogWarning($"MoodyBluesSessionManager.RegisterTransform: '{t.name}' is already tracked as ObjectID {existing}.");
                return existing;
            }

            // Kept as int, not ushort, specifically so exhaustion is detectable: a ushort counter
            // wraps 65535 -> 0 silently, and 0 is the ring buffer's reserved skip marker, so a
            // spawn-heavy session would start emitting events that the accumulator reads as
            // padding. Refusing to allocate is recoverable; corrupting the stream is not.
            if (_nextObjectId > ushort.MaxValue)
            {
                if (!_warnedIdExhaustion)
                {
                    _warnedIdExhaustion = true;
                    Debug.LogError(
                        $"MoodyBluesSessionManager: exhausted the {ushort.MaxValue}-ObjectID space for this " +
                        "session. Further objects will not be recorded. IDs are never reused, so a session " +
                        "that spawns and destroys continuously will eventually reach this -- pool and reuse " +
                        "spawned objects instead of destroying them, or shorten the session.");
                }
                return 0;
            }

            ushort id = (ushort)_nextObjectId++;

            _idByTransform.Add(t, id);
            _transformById.Add(id, t);
            _registrationOrder.Add(id);

            // Seed the baseline now, not at drain time. InstantiateObject carries the transform as
            // of this instant, so the first delta has to diff against this value -- seeding a
            // frame later would diff against wherever the object had already moved to.
            _pendingRegistrations.Add(new PendingRegistration(id, t, TrackedState.FromTransform(t)));

            return id;
        }

        /// <summary>
        /// Stops tracking/sampling the given ObjectID. Does not destroy the GameObject or free the
        /// ID for reuse; use MoodyBluesRuntimeManager.DeleteObject to also destroy it and emit
        /// DeleteObject.
        /// </summary>
        public bool UnregisterObject(ushort id)
        {
            if (!_transformById.TryGetValue(id, out Transform t)) return false;

            _transformById.Remove(id);
            if (t != null) _idByTransform.Remove(t);
#if UNITY_EDITOR
            _lastKnownParent.Remove(id);
#endif

            // Array removal is deferred so the capture set stays stable for the whole tick. The
            // object may therefore still be sampled once more this tick, which is harmless: it is
            // about to be destroyed and its ID is never reused.
            _pendingUnregistrations.Add(id);
            return true;
        }

        public bool TryGetTransform(ushort id, out Transform transform)
        {
            if (_transformById.TryGetValue(id, out transform) && transform != null) return true;
            transform = null;
            return false;
        }

        public bool TryGetIdForTransform(Transform t, out ushort id) => _idByTransform.TryGetValue(t, out id);

        /// <summary>
        /// Immediately checks whether id's current transform differs from LastSent*, and if so sends
        /// an authoritative True* event and updates LastSent* to match. Call this right after
        /// reactivating an object that was hidden (see MoodyBluesRuntimeManager.ObjectSetActive) --
        /// hidden objects aren't sampled, so LastSent* is stale by definition. Sends nothing if
        /// nothing changed.
        /// </summary>
        public void ResyncTransformIfChanged(ushort id)
        {
            if (DataStreamer == null) return;

            // Not yet in the capture set means it was registered this frame and the pending queue
            // hasn't drained. Nothing to resync: InstantiateObject already carried this exact
            // transform, so the baseline is current.
            if (!_indexById.TryGetValue(id, out int index)) return;

            Transform t = _transforms[index];
            if (t == null) return;

            Vector3 position = t.localPosition;
            Quaternion rotation = t.localRotation;
            Vector3 scale = t.localScale;
            TrackedState last = _states[index];

            bool positionChanged = TransformEventDispatcher.HasPositionChanged(position, last.LastSentPosition);
            bool rotationChanged = TransformEventDispatcher.HasRotationChanged(rotation, last.LastSentRotation);
            bool scaleChanged = TransformEventDispatcher.HasScaleChanged(scale, last.LastSentScale);

            if (!positionChanged && !rotationChanged && !scaleChanged) return;

            var data = new TransformData
            {
                ObjectId = id,
                SendTrue = true,
                currentPosition = position,
                currentRotation = rotation,
                currentScale = scale,

                // Populated even though SendTrue events are absolute: the encode result carries
                // the post-event baseline forward for any property the chosen event doesn't
                // happen to transmit, and it can only do that if it starts from real values.
                previousPosition = last.LastSentPosition,
                previousRotation = last.LastSentRotation,
                previousScale = last.LastSentScale,
            };

            TransformEncodeResult encoded = DataStreamer.EnqueueTransform(data);
            if (!encoded.Wrote) return;

            AccumulateEncodeDiagnostics(encoded, id);

            _states[index] = new TrackedState
            {
                LastSentPosition = encoded.Position,
                LastSentRotation = encoded.Rotation,
                LastSentScale = encoded.Scale,
            };
        }

        /// <summary>
        /// Rolls per-event encode diagnostics into session totals. These are reported in
        /// EndSession, so a tight quantization range shows up as a number rather than as motion
        /// that silently tops out.
        /// </summary>
        private void AccumulateEncodeDiagnostics(in TransformEncodeResult encoded, ushort objectId)
        {
            if (encoded.PromotedMask != 0)
            {
                _deltaPromotionCount++;
                if (!_warnedPromotion)
                {
                    _warnedPromotion = true;
                    Debug.LogWarning(
                        $"MoodyBluesSessionManager: ObjectID {objectId} moved further in one capture tick than the " +
                        "configured delta range covers, so an absolute event was sent instead. Correctness is " +
                        "preserved, but widening the delta ranges on MoodyBluesClientConfig will cut bytes. " +
                        "Reported once per session; the total is in EndSession.");
                }
            }

            if (encoded.RotationWidened)
            {
                _rotationWidenCount++;
            }

            if (encoded.HardClamped)
            {
                _hardClampCount++;
                if (!_warnedHardClamp)
                {
                    _warnedHardClamp = true;
                    Debug.LogError(
                        $"MoodyBluesSessionManager: ObjectID {objectId} is outside the configured absolute " +
                        "position/scale range, which no promotion can fix -- its recorded transform WILL be wrong. " +
                        "Raise positionRange/scaleMax on MoodyBluesClientConfig. Reported once per session.");
                }
            }
        }

        /// <summary>
        /// Emits a ReparentObject for a tracked object and re-baselines it. Called by
        /// MoodyBluesRuntimeManager.SetObjectParent *after* the actual SetParent, so the local
        /// transform read here is already the recomputed one the receiver needs.
        /// </summary>
        public void EmitReparent(ushort id, ushort newParentObjectId)
        {
            if (DataStreamer == null) return;
            if (!_transformById.TryGetValue(id, out Transform t) || t == null) return;

            TransformEncodeResult encoded = DataStreamer.EnqueueReparentObject(
                id, newParentObjectId, t.localPosition, t.localRotation, t.localScale);

            AccumulateEncodeDiagnostics(encoded, id);

#if UNITY_EDITOR
            // This reparent went through the sanctioned wrapper; tell the bypass audit so it does
            // not report it as a raw SetParent on its next pass.
            _lastKnownParent[id] = t.parent;
#endif

            // Re-baseline, or the first delta after the reparent would be measured against the
            // old parent's space.
            if (_indexById.TryGetValue(id, out int index))
            {
                _states[index] = new TrackedState
                {
                    LastSentPosition = encoded.Position,
                    LastSentRotation = encoded.Rotation,
                    LastSentScale = encoded.Scale,
                };
                return;
            }

            // Registered this frame and still queued, so its seed holds the pre-reparent values.
            RebaselinePendingRegistration(id, encoded);
        }

        private void RebaselinePendingRegistration(ushort id, in TransformEncodeResult encoded)
        {
            for (int i = 0; i < _pendingRegistrations.Count; i++)
            {
                if (_pendingRegistrations[i].ObjectId != id) continue;

                _pendingRegistrations[i] = new PendingRegistration(
                    id,
                    _pendingRegistrations[i].Target,
                    new TrackedState
                    {
                        LastSentPosition = encoded.Position,
                        LastSentRotation = encoded.Rotation,
                        LastSentScale = encoded.Scale,
                    });
                return;
            }
        }

        // ---- Shutdown -------------------------------------------------------------------------
        //
        // Recording always ends when the component goes away, in both recording modes, so a
        // session is never left open for the backend's inactivity timeout to clean up. These
        // paths stop synchronously (blocking for at most the 2 s close timeout) because Unity
        // tears the object down as soon as they return.

        private void OnApplicationQuit()
        {
            StopRecordingImmediate(Serializer.SessionEndReason.ApplicationQuit);
        }

        private void OnDisable()
        {
            // A component or GameObject being switched off is the host stopping the recording;
            // anything else reaching OnDisable is the scene being unloaded or the object destroyed.
            bool switchedOff = !enabled || !gameObject.activeInHierarchy;
            StopRecordingImmediate(switchedOff
                ? Serializer.SessionEndReason.StoppedByHost
                : Serializer.SessionEndReason.SceneUnloaded);
        }

        private void OnDestroy()
        {
            StopRecordingImmediate(Serializer.SessionEndReason.SceneUnloaded);
            _inert = true;

            _idByTransform.Clear();
            _transformById.Clear();
            _indexById.Clear();
            _pendingRegistrations.Clear();
            _pendingUnregistrations.Clear();
            DisposeCaptureSet();

            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Native containers are not garbage collected -- without this Unity reports a leaked
        /// allocation on exiting play mode. Only OnDestroy calls it: the capture set outlives each
        /// individual recording.
        /// </summary>
        private void DisposeCaptureSet()
        {
            if (_transforms.isCreated) _transforms.Dispose();
            if (_states.IsCreated) _states.Dispose();
            if (_objectIds.IsCreated) _objectIds.Dispose();
            if (_sampled.IsCreated) _sampled.Dispose();
        }
    }
}
