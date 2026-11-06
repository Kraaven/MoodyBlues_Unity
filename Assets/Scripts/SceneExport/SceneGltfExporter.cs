using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityGLTF;

namespace MoodyBlues
{
    /// <summary>
    /// Exports the active scene to an in-memory, self-contained .glb (see Spec.md Section 9).
    ///
    /// Note on mesh compression: UnityGLTF's KHR_draco_mesh_compression support is import-only as
    /// of this writing (com.unity.cloud.draco has no export-side hook in UnityGLTF) -- confirmed by
    /// inspecting UnityGLTF's own source/README ("Import only" section). Draco is installed for
    /// forward-compatibility, but isn't actually applied here; SceneUploadClient gzips the upload
    /// instead to still cut transfer size.
    /// </summary>
    public static class SceneGltfExporter
    {
        public static byte[] ExportActiveSceneToGlb()
        {
            var settings = ScriptableObject.CreateInstance<GLTFSettings>();
            settings.ExportDisabledGameObjects = true; // Include inactive PrefabInstanceLibrary templates.

            var exportContext = new ExportContext(settings);
            exportContext.AfterNodeExport += ObjectIdExportPlugin.AfterNodeExport;

            Transform[] rootTransforms = CollectExportRoots();

            var exporter = new GLTFSceneExporter(rootTransforms, exportContext);
            return exporter.SaveGLBToByteArray(SceneManager.GetActiveScene().name);
        }

        /// <summary>
        /// Every root except the build-time scene-identity holder, which is excluded for the same
        /// reason the ObjectID walk excludes it: it exists only in Player builds, so including it
        /// would make the exported scene differ between an Editor upload and a build upload of the
        /// same scene.
        /// </summary>
        private static Transform[] CollectExportRoots()
        {
            GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();
            var result = new List<Transform>(roots.Length);

            foreach (GameObject root in roots)
            {
                if (BakedSceneId.IsHolder(root.transform)) continue;
                result.Add(root.transform);
            }

            return result.ToArray();
        }
    }
}
