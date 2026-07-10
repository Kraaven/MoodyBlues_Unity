using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns every prefab the scene can spawn at runtime, and is the only sanctioned way to
/// show/hide/spawn/destroy tracked objects -- each wrapper here performs the GameObject
/// operation AND emits the matching wire event, so the receiver's view never drifts from
/// what Unity actually did. See Spec.md Sections 4-5.
///
/// On Awake, every configured prefab is pre-instantiated once as an inactive template under a
/// "PrefabInstanceLibrary" object, before BluesSessionManager's own Awake (see
/// [DefaultExecutionOrder]) so each template picks up a normal, permanent ObjectID during the
/// initial scene walk. InstantiateObject() then clones a new active instance from a template
/// each time it's called; templates themselves are never shown or moved.
/// </summary>
[DefaultExecutionOrder(-1000)]
public class BluesRuntimeManager : MonoBehaviour
{
    public static BluesRuntimeManager Instance { get; private set; }

    [SerializeField] private List<GameObject> prefabs = new();

    private GameObject _libraryRoot;
    private readonly List<GameObject> _templateInstances = new();
    private readonly Dictionary<int, ushort> _templateObjectIdByPrefabIndex = new();

    // Spawned (non-template) instances, so DeleteObject/ObjectSetActive can resolve a GameObject
    // back to its ObjectID.
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
        // Safe here: every object's Awake (including BluesSessionManager's ID-assigning walk)
        // has already run by the time any object's Start runs.
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
    /// and emits an InstantiateObject event. Returns null if prefabIndex is invalid.
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

        // RegisterTransform seeds LastSent* from the just-spawned transform, matching what
        // the InstantiateObject event below already carries -- so FixedUpdate won't redundantly
        // re-send it.
        ushort newObjectId = BluesSessionManager.Instance.RegisterTransform(instance.transform);
        _liveInstanceIds[instance] = newObjectId;

        Streamer.EnqueueInstantiateObject(newObjectId, templateObjectId, position, rotation, scale);
        if (!startActive)
        {
            Streamer.EnqueueHideObject(newObjectId);
        }

        return instance;
    }

    /// <summary>
    /// Permanently destroys a tracked object: destroys the GameObject, stops tracking it, and
    /// emits a DeleteObject event.
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
                // Wasn't polled while inactive, so LastSent* may be stale -- resync now,
                // immediately alongside ShowObject rather than lagging to the next tick.
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
