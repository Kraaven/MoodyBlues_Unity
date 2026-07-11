using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Holds the current scene's stable, backend-facing SceneId (see Spec.md Section 9). This is
/// Unity's own scene-asset GUID, so it never needs manual bookkeeping. In the Editor (including
/// Play Mode) it's resolved live via AssetDatabase; in a built Player it's read from the
/// BakedSceneId component that SceneIdentityBuildProcessor stamps into the scene at build time.
/// </summary>
public static class SceneIdentity
{
    public static string GetCurrentSceneId()
    {
#if UNITY_EDITOR
        string path = SceneManager.GetActiveScene().path;
        string guid = AssetDatabase.AssetPathToGUID(path);
        if (!string.IsNullOrEmpty(guid)) return guid;
#endif
        BakedSceneId baked = Object.FindAnyObjectByType<BakedSceneId>();
        if (baked != null && !string.IsNullOrEmpty(baked.SceneId)) return baked.SceneId;

        Debug.LogError(
            "SceneIdentity: could not resolve a SceneId. Outside the Editor this requires a " +
            "BakedSceneId component, stamped in by SceneIdentityBuildProcessor at build time -- " +
            "was this scene built through the normal Build pipeline?");
        return null;
    }
}

/// <summary>
/// Plain data holder stamped into each built scene by SceneIdentityBuildProcessor (editor-only,
/// build-time). Never created or edited by hand.
/// </summary>
public class BakedSceneId : MonoBehaviour
{
    [SerializeField] private string sceneId;

    public string SceneId => sceneId;

    public void SetSceneId(string id) => sceneId = id;
}
