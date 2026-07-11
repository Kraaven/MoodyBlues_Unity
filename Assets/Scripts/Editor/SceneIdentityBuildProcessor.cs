using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Build-time step that stamps each built scene with a BakedSceneId component holding Unity's
/// own scene-asset GUID, so SceneIdentity.GetCurrentSceneId() has something to read once the
/// Editor-only AssetDatabase lookup is gone (see Assets/Scripts/Handshake/SceneIdentity.cs).
/// Only affects the in-memory copy of the scene being built -- never touches the .unity asset.
/// </summary>
public class SceneIdentityBuildProcessor : IProcessSceneWithReport
{
    public int callbackOrder => 0;

    public void OnProcessScene(Scene scene, BuildReport report)
    {
        string sceneId = AssetDatabase.AssetPathToGUID(scene.path);
        if (string.IsNullOrEmpty(sceneId))
        {
            Debug.LogError($"SceneIdentityBuildProcessor: could not resolve an asset GUID for scene '{scene.path}'.");
            return;
        }

        BakedSceneId baked = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            baked = root.GetComponentInChildren<BakedSceneId>(true);
            if (baked != null) break;
        }

        if (baked == null)
        {
            var holder = new GameObject("BakedSceneId");
            SceneManager.MoveGameObjectToScene(holder, scene);
            baked = holder.AddComponent<BakedSceneId>();
        }

        baked.SetSceneId(sceneId);
    }
}
