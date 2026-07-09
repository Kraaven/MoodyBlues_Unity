using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns every prefab the scene wants to be able to spawn at runtime, and provides the only
/// sanctioned way to show/hide/spawn/destroy tracked objects -- each wrapper here performs the
/// requested GameObject operation AND emits the matching wire event, so the receiver's view of
/// the world never drifts from what Unity actually did. See Spec.md for the exact events.
///
/// On Awake, every configured prefab is pre-instantiated once as an inactive template under a
/// dedicated "PrefabInstanceLibrary" object. Runs before BluesSessionManager's own Awake (see
/// [DefaultExecutionOrder]) specifically so these template instances are already part of the
/// scene hierarchy by the time BluesSessionManager does its initial walk -- that's what gives
/// each template a normal, permanent ObjectID, which is exactly the ID InstantiateObject events
/// reference to tell the receiver which known prefab a new instance was cloned from (the receiver
/// is expected to already know that prefab's mesh from a separate scene export keyed by the same
/// IDs; the live event stream only carries transform/lifecycle changes, not geometry).
///
/// InstantiateObject() clones a NEW active instance from a template each time it's called --
/// templates themselves are never shown or moved, so a single template supports any number of
/// simultaneous live instances of that prefab.
/// </summary>
[DefaultExecutionOrder(-1000)]
public class BluesRuntimeManager : MonoBehaviour
{
    public static BluesRuntimeManager Instance { get; private set; }

    [SerializeField] private List<GameObject> prefabs = new();

    private GameObject _libraryRoot;
    private readonly List<GameObject> _templateInstances = new();
    private readonly Dictionary<int, ushort> _templateObjectIdByPrefabIndex = new();

    // Live (spawned, not template) instances this manager knows about, so DeleteObject/
    // ObjectSetActive-by-GameObject can resolve back to an ObjectID.
    private readonly Dictionary<GameObject, ushort> _liveInstanceIds = new();

    private BluesStreamer Streamer => BluesSessionManager.Instance.Streamer;

    private void Awake()
    {
        Instance = this;

        _libraryRoot = new GameObject("PrefabInstanceLibrary");
        _libraryRoot.transform.SetParent(transform, worldPositionStays: false);
        _libraryRoot.SetActive(false);

        for (int i = 0; i < prefabs.Count; i++)
        {
            GameObject prefab = prefabs[i];
            if (prefab == null)
            {
                Debug.LogWarning($"BluesRuntimeManager: prefab slot {i} is empty, skipping.");
                _templateInstances.Add(null);
                continue;
            }

            GameObject template = Instantiate(prefab, _libraryRoot.transform);
            template.name = prefab.name;
            _templateInstances.Add(template);
        }
    }

    private void Start()
    {
        // Safe to resolve template ObjectIDs here: Unity guarantees every object's Awake (which
        // is where BluesSessionManager walks the scene and assigns IDs) has already run by the
        // time any object's Start runs, regardless of relative script execution order.
        for (int i = 0; i < _templateInstances.Count; i++)
        {
            GameObject template = _templateInstances[i];
            if (template == null) continue;

            if (BluesSessionManager.Instance.TryGetIdForTransform(template.transform, out ushort id))
            {
                _templateObjectIdByPrefabIndex[i] = id;
            }
            else
            {
                Debug.LogError($"BluesRuntimeManager: template for prefab '{prefabs[i].name}' (index {i}) was never assigned an ObjectID -- was it static, or removed from the hierarchy before BluesSessionManager's walk?");
            }
        }
    }

    /// <summary>
    /// Clones a new active instance of prefabs[prefabIndex], registers it for tracking/polling,
    /// and emits an InstantiateObject event carrying the template's ObjectID plus the new
    /// instance's true transform. Returns the spawned instance, or null if prefabIndex is invalid.
    /// </summary>
    public GameObject InstantiateObject(int prefabIndex, Vector3 position, Quaternion rotation, Vector3 scale, bool startActive = true)
    {
        if (prefabIndex < 0 || prefabIndex >= _templateInstances.Count || _templateInstances[prefabIndex] == null)
        {
            Debug.LogError($"BluesRuntimeManager.InstantiateObject: invalid prefabIndex {prefabIndex}.");
            return null;
        }

        if (!_templateObjectIdByPrefabIndex.TryGetValue(prefabIndex, out ushort templateObjectId))
        {
            Debug.LogError($"BluesRuntimeManager.InstantiateObject: prefabIndex {prefabIndex} has no template ObjectID (see the error logged in Start).");
            return null;
        }

        GameObject instance = Instantiate(_templateInstances[prefabIndex], position, rotation);
        instance.transform.localScale = scale;
        instance.name = prefabs[prefabIndex].name;
        instance.SetActive(startActive);

        // RegisterTransform seeds LastSent* from the instance's current (just-spawned) transform,
        // so FixedUpdate won't redundantly re-send a second True* event -- the InstantiateObject
        // event below already carries this instance's true transform, which is exactly what
        // LastSent* now matches.
        ushort newObjectId = BluesSessionManager.Instance.RegisterTransform(instance.transform);
        _liveInstanceIds[instance] = newObjectId;

        Streamer.EnqueueInstantiateObject(newObjectId, templateObjectId, position, rotation, scale);
        if (!startActive)
        {
            // InstantiateObject already carries the true transform; only need to additionally
            // say "and it started hidden" if that's not the (active) default.
            Streamer.EnqueueHideObject(newObjectId);
        }

        return instance;
    }

    /// <summary>
    /// Permanently destroys a previously-spawned or scene object: destroys the GameObject,
    /// stops tracking it, and emits a DeleteObject event. Its ObjectID is never reused.
    /// </summary>
    public void DeleteObject(GameObject go)
    {
        if (go == null) return;

        if (!TryGetObjectId(go, out ushort id))
        {
            Debug.LogWarning($"BluesRuntimeManager.DeleteObject: '{go.name}' isn't a tracked object, destroying locally without emitting an event.");
            Destroy(go);
            return;
        }

        _liveInstanceIds.Remove(go);
        BluesSessionManager.Instance.UnregisterObject(id);
        Streamer.EnqueueDeleteObject(id);
        Destroy(go);
    }

    /// <summary>
    /// Sets a tracked object's active state and emits the matching ShowObject/HideObject event.
    /// </summary>
    public void ObjectSetActive(GameObject go, bool active)
    {
        if (go == null) return;

        bool wasActive = go.activeSelf;
        go.SetActive(active);

        if (!TryGetObjectId(go, out ushort id))
        {
            Debug.LogWarning($"BluesRuntimeManager.ObjectSetActive: '{go.name}' isn't a tracked object, no event emitted.");
            return;
        }

        if (active)
        {
            Streamer.EnqueueShowObject(id);
            if (!wasActive)
            {
                // This object wasn't polled while inactive (see BluesSessionManager.FixedUpdate),
                // so its LastSent* baseline may be stale. Check right now (not deferred to the
                // next FixedUpdate) whether it actually moved while hidden, and only send a
                // True* resync if it did -- ObjectSetActive can be called from Update or anywhere
                // else, so this should go out immediately alongside ShowObject, not lag behind it.
                BluesSessionManager.Instance.ResyncTransformIfChanged(id);
            }
        }
        else
        {
            Streamer.EnqueueHideObject(id);
        }
    }

    public bool TryGetObjectId(GameObject go, out ushort id)
    {
        if (go != null && _liveInstanceIds.TryGetValue(go, out id)) return true;
        if (go != null && BluesSessionManager.Instance.TryGetIdForTransform(go.transform, out id)) return true;
        id = 0;
        return false;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
