using GLTF.Schema;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityGLTF;

/// <summary>
/// Writes each tracked object's ObjectID into node.Extras.objectId during glTF export (see
/// Spec.md Section 9) so the backend can key scene geometry back to wire-protocol ObjectIDs.
/// Wired up directly on the ExportContext.AfterNodeExport delegate (see SceneGltfExporter)
/// rather than through the Project Settings > UnityGLTF plugin list, so no per-project manual
/// enabling step is needed for this to work.
/// </summary>
public static class ObjectIdExportPlugin
{
    public static void AfterNodeExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot, Transform transform, Node node)
    {
        if (BluesSessionManager.Instance == null) return;
        if (!BluesSessionManager.Instance.TryGetIdForTransform(transform, out ushort objectId)) return;

        var UserData = new JObject { ["objectId"] = objectId };
        BluesSessionManager.Instance.PopulateObjectUserData(transform,UserData);

        node.Extras = UserData;
    }
}
