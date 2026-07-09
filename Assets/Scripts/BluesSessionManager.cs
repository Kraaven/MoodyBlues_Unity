using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Walks the scene once at startup to assign every relevant Transform a permanent, never-reused
/// ushort ObjectID, then every FixedUpdate tick diffs each tracked Transform against its last-sent
/// state and streams changes out via BluesStreamer. Also the central registry other systems
/// (currently BluesRuntimeManager) use to register/unregister runtime-spawned objects into the
/// same ID space, so there's a single source of truth for "what ObjectID does this Transform have".
/// </summary>
public class BluesSessionManager : MonoBehaviour
{
    public static BluesSessionManager Instance { get; private set; }

    // ObjectIDs are permanent and never reused (see Spec.md) -- a Dictionary keyed by ID gives
    // O(1) add/remove/lookup regardless of how sparse the ID space gets over a long session,
    // unlike the previous List<TransformReference> (which also silently accumulated dead
    // entries forever whenever an object was destroyed outside this class's knowledge).
    private readonly Dictionary<ushort, TransformReference> _tracked = new();
    private readonly Dictionary<Transform, ushort> _idByTransform = new();
    private ushort _nextObjectId = 1;

    // Reused scratch list for pruning destroyed objects during FixedUpdate, so it doesn't need
    // to be reallocated every tick.
    private readonly List<ushort> _deadIdsScratch = new();

    private BluesStreamer DataStreamer;
    public BluesStreamer Streamer => DataStreamer;

    private void Awake()
    {
        Instance = this;

        // DataStreamer needs to exist before Start() so that anything spawned very early
        // (e.g. from another script's own Start) can already enqueue events.
        DataStreamer = new BluesStreamer();

        // Depends on BluesRuntimeManager (DefaultExecutionOrder -1000) having already run its
        // own Awake and parented its PrefabInstanceLibrary template instances into the scene
        // hierarchy, so those templates get picked up here like any other scene object and
        // receive a normal ObjectID -- see Spec.md and BluesRuntimeManager.
        foreach (var rootObject in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            InsertTransformIntoList(rootObject.transform);
        }
    }

    private void FixedUpdate()
    {
        // Every tick's batch of events starts with a TimeStamp event (see Spec.md), written
        // before any object events so it's always the first thing physically written for this
        // tick -- and, since flushing happens once at the end of this method, normally also the
        // first bytes of whatever WebSocket message ends up carrying this tick's data.
        DataStreamer.EnqueueTimeStamp(Time.timeAsDouble);

        foreach (var kvp in _tracked)
        {
            ushort id = kvp.Key;
            TransformReference reference = kvp.Value;
            Transform t = reference.PollingTransform;

            if (t == null)
            {
                // Object was destroyed by code that didn't go through
                // BluesRuntimeManager.DeleteObject (e.g. a plain Destroy() call elsewhere).
                // Tell the receiver it's gone and prune it, rather than leaving a permanent
                // null entry that gets skipped forever.
                _deadIdsScratch.Add(id);
                continue;
            }

            // Hidden objects (inactive prefab-library templates, hidden-for-gameplay objects,
            // etc.) can't visibly move on the receiving end while inactive, so there's nothing
            // useful to poll or send for them. When one gets shown again,
            // BluesRuntimeManager.ObjectSetActive resyncs it immediately at that call site (not
            // here) -- see ResyncTransformIfChanged.
            if (!t.gameObject.activeInHierarchy) continue;

            // Single epsilon-based "did it change" pass, reused both to decide whether to
            // enqueue anything at all (below) and, inside EnqueueTransform, to decide which
            // properties the chosen event needs to carry -- see TransformData and
            // TransformEventDispatcher's doc comment for why this used to be checked twice.
            // Always Delta here: True* events only ever come from the dedicated
            // InstantiateObject event or ResyncTransformIfChanged, both triggered immediately at
            // their own call sites rather than deferred to a flag this loop checks.
            bool positionChanged = TransformEventDispatcher.HasPositionChanged(t.localPosition, reference.LastSentPosition);
            bool rotationChanged = TransformEventDispatcher.HasRotationChanged(t.localRotation, reference.LastSentRotation);
            bool scaleChanged = TransformEventDispatcher.HasScaleChanged(t.localScale, reference.LastSentScale);

            if (!positionChanged && !rotationChanged && !scaleChanged) continue;

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

            // Advance "last sent" state for next tick. This is main-thread bookkeeping on
            // TransformReference, so no locking needed -- serialization is inline/main-thread now.
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
        // Static objects (checked per-Transform, not inherited from a parent's setting) never
        // move, so there's nothing to poll or stream for them -- skip tracking, but still recurse
        // into children, since a static parent can have a non-static moving child.
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
    /// Assigns t a new, permanent ObjectID and starts tracking it for per-tick polling. Used both
    /// for the initial scene walk and for objects spawned later at runtime (see
    /// BluesRuntimeManager.InstantiateObject).
    ///
    /// LastSent* is seeded from t's current transform -- whatever already knows about this ID
    /// (the separate scene export for scene-walk objects, or the caller's own dedicated event for
    /// spawned ones -- see InstantiateObject) is assumed to already have this starting transform,
    /// so the first FixedUpdate poll only sends anything at all once this object actually moves
    /// from here.
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
    /// Stops tracking/polling the given ObjectID. Does NOT destroy the underlying GameObject and
    /// does NOT free the ID for reuse (IDs are permanent -- see Spec.md); callers that also want
    /// the GameObject destroyed and a DeleteObject event emitted should go through
    /// BluesRuntimeManager.DeleteObject instead of calling this directly.
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
    /// Immediately (not deferred to the next FixedUpdate) checks whether id's current transform
    /// differs from LastSent*, and if so sends an authoritative True* event for it right now and
    /// updates LastSent* to match. Call this right after reactivating an object that was hidden
    /// (see BluesRuntimeManager.ObjectSetActive) -- hidden objects aren't polled (see
    /// FixedUpdate's activeInHierarchy check), so LastSent* may be stale, and a Delta* computed
    /// against it on the next regular poll could be wrong. If nothing actually changed while it
    /// was hidden, this sends nothing at all (the ShowObject event alone is enough).
    ///
    /// This is intentionally synchronous/immediate rather than a flag FixedUpdate checks later:
    /// ObjectSetActive can be called from Update (or anywhere else), same as
    /// InstantiateObject/DeleteObject, so the resync should go out with the same timing as the
    /// ShowObject event it accompanies rather than lagging until the next fixed tick.
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

public class TransformReference
{
    public Transform PollingTransform;

    // Last state actually sent, so BluesSessionManager can compute this tick's previous/current
    // pair without needing a second parallel dictionary keyed by ObjectID. Always seeded to the
    // object's real transform at registration time -- see RegisterTransform -- and also updated
    // immediately by ResyncTransformIfChanged when an object is reactivated after being hidden.
    public Vector3 LastSentPosition;
    public Quaternion LastSentRotation;
    public Vector3 LastSentScale;
}

public struct TransformData
{
    public ushort ObjectId;

    // Computed once by BluesSessionManager (see FixedUpdate) and reused by
    // TransformEventDispatcher/BluesStreamer -- never re-derived downstream.
    public bool SendTrue;
    public bool PositionChanged;
    public bool RotationChanged;
    public bool ScaleChanged;

    public Vector3 currentPosition;
    public Quaternion currentRotation;
    public Vector3 currentScale;
    public Vector3 previousPosition;
    public Quaternion previousRotation;
    public Vector3 previousScale;
}
