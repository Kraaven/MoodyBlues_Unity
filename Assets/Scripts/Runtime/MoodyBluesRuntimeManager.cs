using System.Collections.Generic;
using UnityEngine;

namespace MoodyBlues
{
    /// <summary>
    /// Owns every prefab the scene can spawn at runtime, and is the only sanctioned way to
    /// show/hide/spawn/destroy tracked objects -- each wrapper here performs the GameObject
    /// operation AND emits the matching wire event, so the receiver's view never drifts from
    /// what Unity actually did. See Spec.md Sections 4-5.
    ///
    /// On Awake, every configured prefab is pre-instantiated once as an inactive template under a
    /// "PrefabInstanceLibrary" object, before MoodyBluesSessionManager's own Awake (see
    /// [DefaultExecutionOrder]) so each template picks up a normal, permanent ObjectID during the
    /// initial scene walk. InstantiateObject() then clones a new active instance from a template
    /// each time it's called; templates themselves are never shown or moved.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public class MoodyBluesRuntimeManager : MonoBehaviour
    {
        public static MoodyBluesRuntimeManager Instance { get; private set; }

        [SerializeField] private List<GameObject> prefabs = new();

        private GameObject _libraryRoot;
        private readonly List<GameObject> _templateInstances = new();
        private readonly Dictionary<int, ushort> _templateObjectIdByPrefabIndex = new();

        // Name -> index into `prefabs`. Exists so callers can spawn by name instead of by list
        // position: reordering or inserting a prefab in the Inspector silently changes every
        // later index, which would make existing call sites spawn the wrong thing and would also
        // shift the template ObjectIDs that previously recorded sessions refer to.
        private readonly Dictionary<string, int> _templateIndexByName = new();

        // Spawned (non-template) instances, so DeleteObject/ObjectSetActive can resolve a GameObject
        // back to its ObjectID.
        private readonly Dictionary<GameObject, ushort> _liveInstanceIds = new();

        // Reused by DeleteObject's subtree walk so a delete allocates nothing.
        private readonly List<ushort> _deleteFrontierScratch = new();
        private readonly List<ushort> _deleteSubtreeIdsScratch = new();
        private readonly List<Transform> _deleteSubtreeTransformsScratch = new();

        /// <summary>
        /// Null-safe accessor. The session manager can be destroyed before this component on
        /// scene unload, and gameplay code may still be calling into the wrappers below at that
        /// point -- so every public entry point checks this rather than dereferencing blindly.
        /// </summary>
        private static MoodyBluesSessionManager Session => MoodyBluesSessionManager.Instance;

        private MoodyBluesStreamer Streamer => Session != null ? Session.Streamer : null;

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
                    Debug.LogWarning($"MoodyBluesRuntimeManager: prefab slot {i} is empty, skipping.");
                    _templateInstances.Add(null);
                    continue;
                }

                GameObject template = Instantiate(prefab, _libraryRoot.transform);
                template.name = prefab.name;
                _templateInstances.Add(template);

                if (!_templateIndexByName.TryAdd(prefab.name, i))
                {
                    Debug.LogWarning(
                        $"MoodyBluesRuntimeManager: two prefabs are both named '{prefab.name}', so " +
                        "InstantiateObject(string) can only reach the first. Rename one, or use the " +
                        "index overload for the other.");
                }
            }
        }

        private void Start()
        {
            if (Session == null)
            {
                Debug.LogError("MoodyBluesRuntimeManager: no MoodyBluesSessionManager in the scene, so prefab templates have no ObjectIDs and nothing can be spawned for recording.");
                return;
            }

            // Safe here: every object's Awake (including MoodyBluesSessionManager's ID-assigning walk)
            // has already run by the time any object's Start runs.
            for (int i = 0; i < _templateInstances.Count; i++)
            {
                GameObject template = _templateInstances[i];
                if (template == null) continue;

                if (Session.TryGetIdForTransform(template.transform, out ushort id))
                {
                    _templateObjectIdByPrefabIndex[i] = id;
                }
                else
                {
                    Debug.LogError($"MoodyBluesRuntimeManager: template for prefab '{prefabs[i].name}' (index {i}) was never assigned an ObjectID -- was it static, or removed from the hierarchy before MoodyBluesSessionManager's walk?");
                }
            }
        }

        /// <summary>
        /// Spawns by prefab name. Preferred over the index overload: list positions shift whenever
        /// a prefab is inserted, removed, or reordered in the Inspector, which silently changes
        /// which prefab an existing call site spawns.
        /// </summary>
        public GameObject InstantiateObject(string prefabName, Vector3 position, Quaternion rotation, Vector3 scale, Transform parent = null, bool startActive = true)
        {
            if (!_templateIndexByName.TryGetValue(prefabName, out int index))
            {
                Debug.LogError($"MoodyBluesRuntimeManager.InstantiateObject: no prefab named '{prefabName}' is configured.");
                return null;
            }

            return InstantiateObject(index, position, rotation, scale, parent, startActive);
        }

        /// <summary>
        /// Clones a new active instance of prefabs[prefabIndex], registers it for tracking, and
        /// emits an InstantiateObject event. Returns null if prefabIndex is invalid.
        /// </summary>
        public GameObject InstantiateObject(int prefabIndex, Vector3 position, Quaternion rotation, Vector3 scale, Transform parent = null, bool startActive = true)
        {
            if (Session == null)
            {
                Debug.LogError("MoodyBluesRuntimeManager.InstantiateObject: no active MoodyBluesSessionManager, so no ObjectID can be assigned.");
                return null;
            }

            if (prefabIndex < 0 || prefabIndex >= _templateInstances.Count || _templateInstances[prefabIndex] == null)
            {
                Debug.LogError($"MoodyBluesRuntimeManager.InstantiateObject: invalid prefabIndex {prefabIndex}.");
                return null;
            }

            if (!_templateObjectIdByPrefabIndex.TryGetValue(prefabIndex, out ushort templateObjectId))
            {
                Debug.LogError($"MoodyBluesRuntimeManager.InstantiateObject: prefabIndex {prefabIndex} has no template ObjectID (see the error logged in Start).");
                return null;
            }

            GameObject instance = Instantiate(_templateInstances[prefabIndex], position, rotation);
            instance.transform.localScale = scale;
            if (parent != null && !parent.gameObject.isStatic) instance.transform.SetParent(parent);

            // "PrefabName_clone-XXXXXX": the suffix is cosmetic only (ObjectID, not name, drives all
            // tracking/lookup via _liveInstanceIds/_idByTransform), so collisions are harmless.
            // The name comes from the library template, not prefabs[prefabIndex]: a list entry can
            // be a scene object rather than a prefab asset, and may since have been destroyed.
            instance.name = $"{_templateInstances[prefabIndex].name}_clone-{Random.Range(0, 1000000):D6}";
            instance.SetActive(startActive);

            // RegisterTransform seeds LastSent* from the just-spawned transform, matching what
            // the InstantiateObject event below already carries -- so the capture tick won't
            // redundantly re-send it.
            ushort newObjectId = Session.RegisterTransform(instance.transform);
            if (newObjectId == 0)
            {
                // ID space exhausted (RegisterTransform already logged it). The spawn stands --
                // breaking gameplay because the recorder ran out of identifiers would be a far
                // worse failure than an incomplete recording -- it just isn't tracked.
                return instance;
            }

            _liveInstanceIds[instance] = newObjectId;

            if (Streamer == null)
            {
                // Handshake hasn't completed yet -- spawn proceeds locally, but there's no socket to
                // report it on. LastSent* above is already seeded from this spawn, so nothing's lost.
                Debug.LogWarning($"MoodyBluesRuntimeManager.InstantiateObject: spawned '{instance.name}' before the handshake completed, no event emitted.");
                return instance;
            }

            // 0 means "scene root" -- which is also the right answer when the requested parent is
            // untracked (e.g. a static parent, which the scene walk skips), since in that case the
            // SetParent above was skipped too and the instance really is at the root.
            ushort parentObjectId = 0;
            if (parent != null)
            {
                TryGetObjectId(parent.gameObject, out parentObjectId);
            }

            Streamer.EnqueueInstantiateObject(
                newObjectId,
                templateObjectId,
                parentObjectId,
                instance.transform.localPosition,
                instance.transform.localRotation,
                instance.transform.localScale);

            if (!startActive)
            {
                Streamer.EnqueueHideObject(newObjectId);
            }

            return instance;
        }

        /// <summary>
        /// Permanently destroys a tracked object and everything beneath it.
        ///
        /// Destroying a GameObject in Unity destroys its whole subtree, so every tracked descendant
        /// goes with it. Only the *tracked frontier* -- the topmost tracked nodes in the destroyed
        /// subtree -- is put on the wire: a receiver knows the hierarchy (the scene export carries
        /// the initial parentage, and InstantiateObject/ReparentObject carry every change since),
        /// so it can cascade the rest itself. Descendants are still unregistered locally, because
        /// leaving them in the capture set would have the next tick sampling destroyed transforms.
        /// </summary>
        public void DeleteObject(GameObject go)
        {
            if (go == null) return;

            if (Session == null)
            {
                // Recorder already torn down. Still honour the destroy so gameplay behaves normally.
                Destroy(go);
                return;
            }

            _deleteFrontierScratch.Clear();
            _deleteSubtreeIdsScratch.Clear();
            _deleteSubtreeTransformsScratch.Clear();

            CollectTrackedSubtree(
                go.transform,
                hasTrackedAncestor: false,
                _deleteFrontierScratch,
                _deleteSubtreeIdsScratch,
                _deleteSubtreeTransformsScratch);

            if (_deleteSubtreeIdsScratch.Count == 0)
            {
                Debug.LogWarning($"MoodyBluesRuntimeManager.DeleteObject: neither '{go.name}' nor its children are tracked, destroying locally without emitting an event.");
                Destroy(go);
                return;
            }

            if (Streamer != null)
            {
                foreach (ushort id in _deleteFrontierScratch)
                {
                    Streamer.EnqueueDeleteObject(id);
                }
            }
            else
            {
                Debug.LogWarning($"MoodyBluesRuntimeManager.DeleteObject: destroyed '{go.name}' before the handshake completed, no event emitted.");
            }

            for (int i = 0; i < _deleteSubtreeIdsScratch.Count; i++)
            {
                Transform t = _deleteSubtreeTransformsScratch[i];
                if (t != null) _liveInstanceIds.Remove(t.gameObject);
                Session.UnregisterObject(_deleteSubtreeIdsScratch[i]);
            }

            Destroy(go);
        }

        /// <summary>
        /// Walks a subtree collecting every tracked node, and separately the frontier: nodes with
        /// no tracked ancestor inside this subtree.
        ///
        /// Traversal is driven by the transform hierarchy rather than by what is tracked, because
        /// an *untracked* parent can own tracked children -- a static parent with moving children
        /// is exactly the shape the initial scene walk produces (it skips static objects but
        /// recurses into them). Keying off tracked nodes alone would miss those entirely.
        /// </summary>
        private static void CollectTrackedSubtree(
            Transform t,
            bool hasTrackedAncestor,
            List<ushort> frontier,
            List<ushort> allIds,
            List<Transform> allTransforms)
        {
            bool tracked = Session.TryGetIdForTransform(t, out ushort id);
            if (tracked)
            {
                allIds.Add(id);
                allTransforms.Add(t);

                if (!hasTrackedAncestor)
                {
                    frontier.Add(id);
                }
            }

            for (int i = 0; i < t.childCount; i++)
            {
                CollectTrackedSubtree(t.GetChild(i), hasTrackedAncestor || tracked, frontier, allIds, allTransforms);
            }
        }

        /// <summary>
        /// Reparents a tracked object and emits ReparentObject.
        ///
        /// The sanctioned alternative to calling Transform.SetParent directly. A bypassed reparent
        /// has no visible symptom: the object keeps reporting local-space deltas, but the receiver
        /// still believes it sits under its old parent, so everything after that point is wrong
        /// while looking perfectly plausible.
        /// </summary>
        /// <param name="worldPositionStays">
        /// Same meaning as Unity's Transform.SetParent. Either way the emitted event carries the
        /// resulting local transform, read after the reparent.
        /// </param>
        public void SetObjectParent(GameObject go, Transform newParent, bool worldPositionStays = true)
        {
            if (go == null) return;

            if (Session == null)
            {
                go.transform.SetParent(newParent, worldPositionStays);
                return;
            }

            if (!TryGetObjectId(go, out ushort id))
            {
                Debug.LogWarning($"MoodyBluesRuntimeManager.SetObjectParent: '{go.name}' isn't a tracked object, reparenting locally without emitting an event.");
                go.transform.SetParent(newParent, worldPositionStays);
                return;
            }

            if (newParent != null && newParent.gameObject.isStatic)
            {
                // Static objects are never tracked (the scene walk skips them), so there is no id
                // to report. Same outcome as InstantiateObject's handling of a static parent.
                Debug.LogWarning(
                    $"MoodyBluesRuntimeManager.SetObjectParent: '{newParent.name}' is static and therefore " +
                    $"untracked, so '{go.name}' will be reported as parented to the scene root.");
            }

            go.transform.SetParent(newParent, worldPositionStays);

            ushort parentObjectId = 0;
            if (newParent != null)
            {
                TryGetObjectId(newParent.gameObject, out parentObjectId);
            }

            if (Streamer == null)
            {
                Debug.LogWarning($"MoodyBluesRuntimeManager.SetObjectParent: reparented '{go.name}' before the handshake completed, no event emitted.");
                return;
            }

            Session.EmitReparent(id, parentObjectId);
        }

        /// <summary>
        /// Sets a tracked object's active state and emits the matching ShowObject/HideObject event.
        /// A no-op (and emits nothing) when the object is already in the requested state.
        /// </summary>
        public void ObjectSetActive(GameObject go, bool active)
        {
            if (go == null) return;

            // Already in the requested state: nothing changed, so there is nothing to report.
            if (go.activeSelf == active) return;

            go.SetActive(active);

            // Recorder torn down: the local state change still applies, there is just nobody to tell.
            if (Session == null) return;

            if (!TryGetObjectId(go, out ushort id))
            {
                Debug.LogWarning($"MoodyBluesRuntimeManager.ObjectSetActive: '{go.name}' isn't a tracked object, no event emitted.");
                return;
            }

            if (Streamer == null)
            {
                Debug.LogWarning($"MoodyBluesRuntimeManager.ObjectSetActive: toggled '{go.name}' before the handshake completed, no event emitted.");
                return;
            }

            if (active)
            {
                Streamer.EnqueueShowObject(id);

                // Hidden objects are never polled, so LastSent* is stale by definition here --
                // resync immediately rather than lagging to the next capture tick. The early
                // return above guarantees the object really was inactive until now.
                Session.ResyncTransformIfChanged(id);
            }
            else
            {
                Streamer.EnqueueHideObject(id);
            }
        }

        public bool TryGetObjectId(GameObject go, out ushort id)
        {
            if (go != null && _liveInstanceIds.TryGetValue(go, out id)) return true;
            if (go != null && Session != null && Session.TryGetIdForTransform(go.transform, out id)) return true;
            id = 0;
            return false;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
    }
}
