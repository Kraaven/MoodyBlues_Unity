using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MoodyBlues.EditorTools
{
    /// <summary>
    /// Build-time step that stamps each built scene with a BakedSceneId component holding Unity's
    /// own scene-asset GUID and the scene's content hash, so SceneIdentity and SceneContentHash have
    /// something to read once the Editor-only AssetDatabase is gone (see
    /// Assets/Scripts/Handshake/SceneIdentity.cs). Only affects the in-memory copy of the scene being
    /// built -- never touches the .unity asset.
    /// </summary>
    public class SceneIdentityBuildProcessor : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            // Unity also calls this for every scene entering Play mode, with no report. Play mode
            // reads both values live from the AssetDatabase, so there is nothing to stamp.
            if (report == null) return;

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
                // Created last, so it is the last root: the other roots keep the sibling indices
                // the scene hash saw in the Editor.
                var holder = new GameObject(BakedSceneId.HolderName);
                SceneManager.MoveGameObjectToScene(holder, scene);
                baked = holder.AddComponent<BakedSceneId>();
                holder.isStatic = true;
            }

            baked.SetSceneId(sceneId);
            baked.SetContentHash(SceneContentHashEditor.Compute(scene.path));
        }
    }
}
