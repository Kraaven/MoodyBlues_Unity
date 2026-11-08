using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MoodyBlues
{
    /// <summary>
    /// The content half of the scene hash (see SceneHasher): a hash of every asset the scene uses,
    /// so editing a mesh, material or texture gives the scene a new version even when nothing
    /// moved. Unity only knows asset content in the Editor, so:
    ///
    /// - in the Editor (Play mode and editor tooling) it is computed live by the editor assembly,
    ///   which registers <see cref="EditorProvider"/> at load;
    /// - in a Player it is read from the BakedSceneId component the build stamps into each scene.
    /// </summary>
    public static class SceneContentHash
    {
#if UNITY_EDITOR
        /// <summary>Set by MoodyBlues.EditorTools: scene asset path -> content hash.</summary>
        public static Func<string, string> EditorProvider;
#endif

        /// <summary>The content hash of a loaded scene, or an empty string if none is available.</summary>
        public static string For(Scene scene)
        {
#if UNITY_EDITOR
            if (EditorProvider != null && !string.IsNullOrEmpty(scene.path))
            {
                return EditorProvider(scene.path) ?? string.Empty;
            }
#endif
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.TryGetComponent(out BakedSceneId baked) && !string.IsNullOrEmpty(baked.ContentHash))
                {
                    return baked.ContentHash;
                }
            }

            Debug.LogWarning(
                "SceneContentHash: this build has no content hash for the scene (it was not stamped at build " +
                "time), so asset edits will not produce a new scene version. Rebuild the player.");
            return string.Empty;
        }
    }
}
