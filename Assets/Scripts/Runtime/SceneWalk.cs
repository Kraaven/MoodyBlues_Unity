using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MoodyBlues
{
    /// <summary>
    /// The one definition of which transforms get ObjectIDs, and in what order (Spec.md Section 5).
    /// The running game, the build-time export and the Tools menu export all use it, so they assign
    /// identical IDs to identical scenes: ObjectID n is the n-th transform this walk visits.
    ///
    /// Every transform is visited, depth first, roots in scene order, except two kinds of subtree
    /// that exist in only one of Editor and Player, and would otherwise shift every later ID:
    /// - the build-time scene-identity holder (Player only);
    /// - objects tagged EditorOnly (stripped from Player builds; UnityGLTF skips them too).
    /// </summary>
    public static class SceneWalk
    {
        private const string EditorOnlyTag = "EditorOnly";

        /// <summary>Appends every transform of the scene to <paramref name="into"/>, in ObjectID order.</summary>
        public static void Collect(Scene scene, List<Transform> into)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                CollectSubtree(root.transform, into);
            }
        }

        /// <summary>Appends <paramref name="root"/> and its descendants, in ObjectID order.</summary>
        public static void CollectSubtree(Transform root, List<Transform> into)
        {
            if (BakedSceneId.IsHolder(root) || root.CompareTag(EditorOnlyTag)) return;

            into.Add(root);
            for (int i = 0; i < root.childCount; i++)
            {
                CollectSubtree(root.GetChild(i), into);
            }
        }
    }
}
