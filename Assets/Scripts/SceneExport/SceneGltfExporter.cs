using System.Collections.Generic;
using System.Text;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityGLTF;
using UnityGLTF.Extensions;
using UnityGLTF.Plugins;

namespace MoodyBlues
{
    /// <summary>
    /// The pose and visibility a node should have in an export, when that differs from what the
    /// transform holds right now (see <see cref="SceneGltfExporter.Export(Scene, IReadOnlyDictionary{Transform, ushort}, IReadOnlyDictionary{ushort, ExportNodeState}, out int)"/>).
    /// </summary>
    public readonly struct ExportNodeState
    {
        public readonly Vector3 LocalPosition;
        public readonly Quaternion LocalRotation;
        public readonly Vector3 LocalScale;
        public readonly bool ActiveSelf;

        public ExportNodeState(Vector3 localPosition, Quaternion localRotation, Vector3 localScale, bool activeSelf)
        {
            LocalPosition = localPosition;
            LocalRotation = localRotation;
            LocalScale = localScale;
            ActiveSelf = activeSelf;
        }
    }

    /// <summary>
    /// Exports a scene to an in-memory, self-contained .glb (see Spec.md Section 9). Used by the
    /// running game (runtime export) and by the editor tooling (build-time and Tools menu export).
    ///
    /// Exactly the transforms in the ObjectID map are exported, each carrying its ObjectID in
    /// node.extras (see <see cref="ObjectIdExportPlugin"/>). Anything else in the scene -- objects
    /// spawned after the walk, EditorOnly objects, the scene-identity holder -- is left out.
    ///
    /// Meshes that cannot be exported are left off their node and counted:
    /// - in a Player, meshes without Read/Write (UnityGLTF skips them itself);
    /// - statically batched renderers, whose MeshFilter holds the whole combined batch rather
    ///   than the object's own mesh. Only happens at runtime: batching runs after the scene loads.
    ///
    /// Note on mesh compression: UnityGLTF's KHR_draco_mesh_compression support is import-only, so
    /// SceneUploadClient gzips the upload instead.
    /// </summary>
    public static class SceneGltfExporter
    {
        // UnityGLTF's own constant (SchemaExtensions.InvertDirection) is internal.
        private static readonly Quaternion InvertLookDirection = new Quaternion(0, -1, 0, 0);

        private const int MaxSkippedNamesLogged = 10;

        /// <summary>The export in progress, read by ObjectIdExportPlugin. Main thread only.</summary>
        internal static ExportJob Current { get; private set; }

        /// <summary>Exports <paramref name="ids"/>' transforms as they are now.</summary>
        public static byte[] Export(Scene scene, IReadOnlyDictionary<Transform, ushort> ids, out int skippedMeshes) =>
            Export(scene, ids, null, out skippedMeshes);

        /// <summary>
        /// Exports <paramref name="ids"/>' transforms. Objects listed in <paramref name="stateOverrides"/>
        /// are written with that pose and visibility instead of their current one, which is how the
        /// running game exports the scene as it was at Start after things have started moving.
        /// Throws if UnityGLTF fails.
        /// </summary>
        public static byte[] Export(
            Scene scene,
            IReadOnlyDictionary<Transform, ushort> ids,
            IReadOnlyDictionary<ushort, ExportNodeState> stateOverrides,
            out int skippedMeshes)
        {
            var settings = ScriptableObject.CreateInstance<GLTFSettings>();
            settings.ExportDisabledGameObjects = true; // Inactive objects (and the template library) are part of the scene.
            settings.UseMainCameraVisibility = false;  // Every tracked object, whatever the camera's culling mask.
            settings.ExportAnimations = false;         // Replays drive transforms from the recording.
            EnsureObjectIdPlugin(settings);

            var job = new ExportJob(ids, stateOverrides);
            Current = job;
            try
            {
                var exporter = new GLTFSceneExporter(CollectExportRoots(scene, ids), new ExportContext(settings));
                byte[] glb = exporter.SaveGLBToByteArray(scene.name);

                skippedMeshes = job.SkippedMeshes;
                if (skippedMeshes > 0)
                {
                    Debug.LogWarning(
                        $"SceneGltfExporter: {skippedMeshes} mesh(es) could not be exported and will be missing from the " +
                        $"replay ({job.DescribeSkipped()}). Meshes need Read/Write for a runtime export " +
                        "(Tools > MoodyBlues > Make Scene Meshes Readable), and statically batched meshes cannot be " +
                        "exported at runtime: export from the Editor instead (Tools > MoodyBlues > Export This Scene).");
                }

                return glb;
            }
            finally
            {
                Current = null;
                DestroySettings(settings);
            }
        }

        private static void EnsureObjectIdPlugin(GLTFSettings settings)
        {
            // UnityGLTF registers every GLTFExportPlugin type it finds on new settings, so the
            // plugin is normally there already; this covers a Player where discovery missed it.
            settings.ExportPlugins ??= new List<GLTFExportPlugin>();
            ObjectIdExportPlugin plugin = null;
            foreach (GLTFExportPlugin p in settings.ExportPlugins)
            {
                if (p is ObjectIdExportPlugin found) plugin = found;
            }

            if (plugin == null)
            {
                plugin = ScriptableObject.CreateInstance<ObjectIdExportPlugin>();
                settings.ExportPlugins.Add(plugin);
            }

            plugin.Enabled = true;
        }

        private static void DestroySettings(GLTFSettings settings)
        {
            if (settings == null) return;

            if (settings.ExportPlugins != null)
            {
                foreach (GLTFExportPlugin p in settings.ExportPlugins) DestroyObject(p);
            }
            if (settings.ImportPlugins != null)
            {
                foreach (GLTFImportPlugin p in settings.ImportPlugins) DestroyObject(p);
            }
            DestroyObject(settings);
        }

        private static void DestroyObject(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }

        /// <summary>The scene's roots that carry an ObjectID, in scene order.</summary>
        private static Transform[] CollectExportRoots(Scene scene, IReadOnlyDictionary<Transform, ushort> ids)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            var result = new List<Transform>(roots.Length);

            foreach (GameObject root in roots)
            {
                if (ids.ContainsKey(root.transform)) result.Add(root.transform);
            }

            return result.ToArray();
        }

        /// <summary>State of one export, consulted by ObjectIdExportPlugin's callbacks.</summary>
        internal sealed class ExportJob
        {
            private readonly IReadOnlyDictionary<Transform, ushort> _ids;
            private readonly IReadOnlyDictionary<ushort, ExportNodeState> _overrides;
            private readonly List<string> _skippedNames = new();

            public int SkippedMeshes { get; private set; }

            public ExportJob(IReadOnlyDictionary<Transform, ushort> ids, IReadOnlyDictionary<ushort, ExportNodeState> overrides)
            {
                _ids = ids;
                _overrides = overrides;
            }

            public bool ShouldExport(Transform t) => t != null && _ids.ContainsKey(t);

            public void AfterNodeExport(Transform t, Node node)
            {
                if (!_ids.TryGetValue(t, out ushort objectId)) return;

                bool activeSelf = t.gameObject.activeSelf;
                if (_overrides != null && _overrides.TryGetValue(objectId, out ExportNodeState state))
                {
                    // Same conversion UnityGLTF applies in Node.SetUnityTransform.
                    bool invertLook = t.GetComponent<Camera>() != null || t.GetComponent<Light>() != null;
                    node.Translation = state.LocalPosition.ToGltfVector3Convert();
                    node.Rotation = (state.LocalRotation * (invertLook ? InvertLookDirection : Quaternion.identity)).ToGltfQuaternionConvert();
                    node.Scale = state.LocalScale.ToGltfVector3Raw();
                    activeSelf = state.ActiveSelf;
                }

                CheckMesh(t, node);

                var extras = new JObject { ["objectId"] = objectId };
                MoodyBluesSessionManager.PopulateObjectUserData(t, extras, activeSelf);
                node.Extras = extras;
            }

            private void CheckMesh(Transform t, Node node)
            {
                if (t.TryGetComponent(out MeshRenderer meshRenderer) && t.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null)
                {
                    if (meshRenderer.isPartOfStaticBatch)
                    {
                        // The filter's mesh is the whole combined batch; exporting it would put the
                        // entire batch at this node.
                        node.Mesh = null;
                        AddSkipped(t);
                    }
                    else if (node.Mesh == null && !Application.isEditor && !filter.sharedMesh.isReadable)
                    {
                        AddSkipped(t); // UnityGLTF skipped it
                    }
                }
                else if (t.TryGetComponent(out SkinnedMeshRenderer skinned) && skinned.sharedMesh != null &&
                         node.Mesh == null && !Application.isEditor && !skinned.sharedMesh.isReadable)
                {
                    AddSkipped(t);
                }
            }

            private void AddSkipped(Transform t)
            {
                SkippedMeshes++;
                if (_skippedNames.Count < MaxSkippedNamesLogged) _skippedNames.Add(t.name);
            }

            public string DescribeSkipped()
            {
                var sb = new StringBuilder();
                sb.Append(string.Join(", ", _skippedNames));
                if (SkippedMeshes > _skippedNames.Count) sb.Append($", and {SkippedMeshes - _skippedNames.Count} more");
                return sb.ToString();
            }
        }
    }
}
