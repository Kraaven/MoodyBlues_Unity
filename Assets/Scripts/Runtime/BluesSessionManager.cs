using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Walks the scene once at startup to assign every relevant Transform a permanent, never-reused
/// ObjectID, then every FixedUpdate tick diffs each tracked Transform against its last-sent state
/// and streams changes via BluesStreamer. Also the registry BluesRuntimeManager uses to register/
/// unregister runtime-spawned objects into the same ID space. See Spec.md Sections 4-5.
/// </summary>
public class BluesSessionManager : MonoBehaviour
{
    public static BluesSessionManager Instance { get; private set; }

    private readonly Dictionary<ushort, TransformReference> _tracked = new();
    private readonly Dictionary<Transform, ushort> _idByTransform = new();
    private ushort _nextObjectId = 1;

    // Reused scratch list for pruning destroyed objects during FixedUpdate.
    private readonly List<ushort> _deadIdsScratch = new();

    private BluesStreamer DataStreamer;
    public BluesStreamer Streamer => DataStreamer;

    private void Awake()
    {
        Instance = this;

        // Must exist before Start() so anything spawned early can already enqueue events.
        DataStreamer = new BluesStreamer();

        // Runs after BluesRuntimeManager's Awake (DefaultExecutionOrder -1000), so its
        // PrefabInstanceLibrary templates are already in the hierarchy and get picked up
        // here like any other scene object.
        foreach (var rootObject in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            InsertTransformIntoList(rootObject.transform);
        }
    }

    private void FixedUpdate()
    {
        // Set the first time this tick actually enqueues a transform delta, so exactly one
        // TimeStamp goes out per tick, and only for ticks that have something to report.
        bool timeStampSentThisTick = false;

        foreach (var kvp in _tracked)
        {
            ushort id = kvp.Key;
            TransformReference reference = kvp.Value;
            Transform t = reference.PollingTransform;

            if (t == null)
            {
                // Destroyed by code that didn't go through BluesRuntimeManager.DeleteObject --
                // tell the receiver it's gone and prune it rather than skipping it forever.
                _deadIdsScratch.Add(id);
                continue;
            }

            // Hidden objects aren't polled; BluesRuntimeManager.ObjectSetActive resyncs them
            // immediately on reactivation instead (see ResyncTransformIfChanged).
            if (!t.gameObject.activeInHierarchy) continue;

            bool positionChanged = TransformEventDispatcher.HasPositionChanged(t.localPosition, reference.LastSentPosition);
            bool rotationChanged = TransformEventDispatcher.HasRotationChanged(t.localRotation, reference.LastSentRotation);
            bool scaleChanged = TransformEventDispatcher.HasScaleChanged(t.localScale, reference.LastSentScale);

            if (!positionChanged && !rotationChanged && !scaleChanged) continue;

            if (!timeStampSentThisTick)
            {
                DataStreamer.EnqueueTimeStamp(Time.timeAsDouble);
                timeStampSentThisTick = true;
            }

            TransformData data = new TransformData
            {
                ObjectId = id,
                SendTrue = false,
                PositionChanged = positionChanged,
                RotationChanged = rotationChanged,
                ScaleChanged = scaleChanged,
                currentPosition = t.localPosition,
                currentRotation = t.localRotation,
                currentScale = t.localScale,
                previousPosition = reference.LastSentPosition,
                previousRotation = reference.LastSentRotation,
                previousScale = reference.LastSentScale
            };

            DataStreamer.EnqueueTransform(data);

            reference.LastSentPosition = data.currentPosition;
            reference.LastSentRotation = data.currentRotation;
            reference.LastSentScale = data.currentScale;
        }

        if (_deadIdsScratch.Count > 0)
        {
            foreach (ushort deadId in _deadIdsScratch)
            {
                DataStreamer.EnqueueDeleteObject(deadId);
                UnregisterObject(deadId);
            }
            _deadIdsScratch.Clear();
        }

        DataStreamer.FlushPendingWrites();
    }

    private void InsertTransformIntoList(Transform t)
    {
        // Static objects never move -- skip tracking, but still recurse into children,
        // since a static parent can have a non-static moving child.
        if (!t.gameObject.isStatic)
        {
            RegisterTransform(t);
        }

        for (int i = 0; i < t.childCount; i++)
        {
            InsertTransformIntoList(t.GetChild(i));
        }
    }

    /// <summary>
    /// Assigns t a new, permanent ObjectID and starts tracking it for per-tick polling. Used for
    /// both the initial scene walk and runtime spawns (see BluesRuntimeManager.InstantiateObject).
    /// LastSent* is seeded from t's current transform, so nothing is sent until it actually moves.
    /// </summary>
    public ushort RegisterTransform(Transform t)
    {
        ushort id = _nextObjectId++;
        var reference = new TransformReference
        {
            PollingTransform = t,
            LastSentPosition = t.localPosition,
            LastSentRotation = t.localRotation,
            LastSentScale = t.localScale
        };
        _tracked.Add(id, reference);
        _idByTransform.Add(t, id);
        return id;
    }

    /// <summary>
    /// Stops tracking/polling the given ObjectID. Does not destroy the GameObject or free the ID
    /// for reuse; use BluesRuntimeManager.DeleteObject to also destroy it and emit DeleteObject.
    /// </summary>
    public bool UnregisterObject(ushort id)
    {
        if (!_tracked.TryGetValue(id, out TransformReference reference)) return false;
        _tracked.Remove(id);
        if (reference.PollingTransform != null)
        {
            _idByTransform.Remove(reference.PollingTransform);
        }
        return true;
    }

    public bool TryGetTransform(ushort id, out Transform transform)
    {
        if (_tracked.TryGetValue(id, out TransformReference reference) && reference.PollingTransform != null)
        {
            transform = reference.PollingTransform;
            return true;
        }
        transform = null;
        return false;
    }

    public bool TryGetIdForTransform(Transform t, out ushort id) => _idByTransform.TryGetValue(t, out id);

    /// <summary>
    /// Immediately checks whether id's current transform differs from LastSent*, and if so sends
    /// an authoritative True* event and updates LastSent* to match. Call this right after
    /// reactivating an object that was hidden (see BluesRuntimeManager.ObjectSetActive) -- hidden
    /// objects aren't polled, so LastSent* may be stale. Sends nothing if nothing changed.
    /// </summary>
    public void ResyncTransformIfChanged(ushort id)
    {
        if (!_tracked.TryGetValue(id, out TransformReference reference)) return;
        Transform t = reference.PollingTransform;
        if (t == null) return;

        bool positionChanged = TransformEventDispatcher.HasPositionChanged(t.localPosition, reference.LastSentPosition);
        bool rotationChanged = TransformEventDispatcher.HasRotationChanged(t.localRotation, reference.LastSentRotation);
        bool scaleChanged = TransformEventDispatcher.HasScaleChanged(t.localScale, reference.LastSentScale);

        if (!positionChanged && !rotationChanged && !scaleChanged) return;

        TransformData data = new TransformData
        {
            ObjectId = id,
            SendTrue = true,
            currentPosition = t.localPosition,
            currentRotation = t.localRotation,
            currentScale = t.localScale
        };

        DataStreamer.EnqueueTransform(data);

        reference.LastSentPosition = data.currentPosition;
        reference.LastSentRotation = data.currentRotation;
        reference.LastSentScale = data.currentScale;
    }

    private void OnDestroy()
    {
        DataStreamer?.Dispose();
        if (Instance == this) Instance = null;
    }
}
