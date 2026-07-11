using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityGLTF;

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

        Transform[] rootTransforms = SceneManager.GetActiveScene()
            .GetRootGameObjects()
            .Select(go => go.transform)
            .ToArray();

        var exporter = new GLTFSceneExporter(rootTransforms, exportContext);
        return exporter.SaveGLBToByteArray(SceneManager.GetActiveScene().name);
    }
}
