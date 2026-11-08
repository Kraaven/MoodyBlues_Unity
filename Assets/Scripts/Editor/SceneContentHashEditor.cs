using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;

namespace MoodyBlues.EditorTools
{
    /// <summary>
    /// Computes a scene's content hash (see SceneContentHash and Spec.md Section 13.2): SHA-256
    /// over Unity's dependency hash of every asset the scene uses, recursively, sorted by path.
    /// Scripts are left out, so a code change alone does not create a new scene version.
    ///
    /// Registered as SceneContentHash.EditorProvider so Play mode computes it live; the build
    /// bakes the same value into each scene (SceneIdentityBuildProcessor).
    /// </summary>
    public static class SceneContentHashEditor
    {
        private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".dll", ".asmdef", ".asmref",
        };

        [InitializeOnLoadMethod]
        private static void Register()
        {
            SceneContentHash.EditorProvider = Compute;
        }

        /// <summary>The content hash of the scene asset at <paramref name="scenePath"/>, lowercase hex.</summary>
        public static string Compute(string scenePath)
        {
            string[] dependencies = AssetDatabase.GetDependencies(scenePath, recursive: true);
            Array.Sort(dependencies, StringComparer.Ordinal);

            var text = new StringBuilder(dependencies.Length * 96);
            foreach (string path in dependencies)
            {
                if (ExcludedExtensions.Contains(Path.GetExtension(path))) continue;

                text.Append(path).Append('\n')
                    .Append(AssetDatabase.GetAssetDependencyHash(path).ToString()).Append('\n');
            }

            using var sha256 = SHA256.Create();
            byte[] digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));

            var hex = new StringBuilder(digest.Length * 2);
            foreach (byte b in digest) hex.Append(b.ToString("x2"));
            return hex.ToString();
        }
    }
}
