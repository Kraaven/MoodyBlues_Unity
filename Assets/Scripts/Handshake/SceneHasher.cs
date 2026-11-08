using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MoodyBlues
{
    /// <summary>
    /// Computes the sceneHash sent in the handshake request (see Spec.md Section 13.2). It answers
    /// one question: "is the scene export the backend already holds valid for this scene?" It is
    /// built from two parts, combined by <see cref="Combine"/>:
    ///
    /// - The <b>content hash</b> (SceneContentHash): Unity's own dependency hashes of every asset
    ///   the scene uses -- meshes, materials, textures, shaders, prefabs -- computed in the Editor
    ///   and baked into builds. It catches content edits that move nothing.
    /// - The <b>structural hash</b> (this class): the object set, the ID mapping, the hierarchy,
    ///   and every transform, as the scene actually is at Start.
    ///
    /// Neither part needs a GLB export, so the hash is cheap enough to compute on every load.
    ///
    /// WHEN the structural hash samples matters as much as what. At runtime it runs from
    /// MoodyBluesSessionManager.Start, which (with that component's execution order of 1000) is
    /// after every other Awake and Start, so the transforms it reads have a defined moment.
    ///
    /// Structural buffer layout:
    ///   [sceneName UTF8] [0x00]
    ///   for each ObjectID (walk order):
    ///     [hierarchyPath UTF8] [0x00] [ObjectID 2B LE]
    ///     [localPosition 3xfloat32 LE] [localRotation 4xfloat32 LE] [localScale 3xfloat32 LE]
    /// SHA-256 of the whole buffer, lowercase hex.
    /// </summary>
    public static class SceneHasher
    {
        private const int TransformByteCount = 40; // 3 + 4 + 3 floats

        /// <summary>The structural hash of the running scene, over its initial-walk ObjectIDs.</summary>
        public static string ComputeSceneHash(MoodyBluesSessionManager sessionManager)
        {
            var objectIds = sessionManager.InitialWalkObjectIds;
            var transforms = new Transform[objectIds.Count];
            for (int i = 0; i < objectIds.Count; i++)
            {
                sessionManager.TryGetTransform(objectIds[i], out transforms[i]);
            }

            return ComputeStructuralHash(SceneManager.GetActiveScene().name, objectIds, transforms);
        }

        /// <summary>
        /// The structural hash for an editor-side walk (<see cref="SceneWalk"/>): ObjectID n is the
        /// n-th transform, exactly as the running game assigns them.
        /// </summary>
        public static string ComputeStructuralHash(string sceneName, IReadOnlyList<Transform> walk)
        {
            var ids = new ushort[walk.Count];
            for (int i = 0; i < ids.Length; i++) ids[i] = (ushort)(i + 1);
            return ComputeStructuralHash(sceneName, ids, walk);
        }

        /// <summary>
        /// The full scene hash sent in the handshake: SHA-256 over the content hash (the assets the
        /// scene uses, see SceneContentHash), the structural hash, and, for scenes exported on every
        /// run, the hash of that export. Lowercase hex.
        /// </summary>
        public static string Combine(string contentHash, string structuralHash, string exportHash = null)
        {
            string text = $"content:{contentHash ?? ""}\nstructure:{structuralHash}\nexport:{exportHash ?? ""}";
            using var sha256 = SHA256.Create();
            return ToLowerHex(sha256.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        public static string HashBytes(byte[] bytes)
        {
            using var sha256 = SHA256.Create();
            return ToLowerHex(sha256.ComputeHash(bytes));
        }

        private static string ComputeStructuralHash(string sceneName, IReadOnlyList<ushort> objectIds, IReadOnlyList<Transform> transforms)
        {

            // Full hierarchy path rather than the bare name. Two same-named siblings that swap
            // order keep the same (name, id) pairs between runs, so a name-only hash would call
            // the scene unchanged; their paths differ by sibling index, so this does not.
            var paths = new string[objectIds.Count];
            for (int i = 0; i < objectIds.Count; i++)
            {
                paths[i] = transforms[i] != null ? BuildHierarchyPath(transforms[i]) : string.Empty;
            }

            int size = Encoding.UTF8.GetByteCount(sceneName) + 1;
            for (int i = 0; i < objectIds.Count; i++)
            {
                size += Encoding.UTF8.GetByteCount(paths[i]) + 1; // path + \x00
                size += 2;                                        // ObjectID
                size += TransformByteCount;
            }

            byte[] buffer = new byte[size];
            int offset = 0;

            offset += Encoding.UTF8.GetBytes(sceneName, 0, sceneName.Length, buffer, offset);
            buffer[offset++] = 0x00;

            for (int i = 0; i < objectIds.Count; i++)
            {
                ushort id = objectIds[i];
                string path = paths[i];

                if (path.Length > 0)
                {
                    offset += Encoding.UTF8.GetBytes(path, 0, path.Length, buffer, offset);
                }
                buffer[offset++] = 0x00;

                buffer[offset++] = (byte)(id & 0xFF);
                buffer[offset++] = (byte)((id >> 8) & 0xFF);

                if (transforms[i] != null)
                {
                    WriteTransform(transforms[i], buffer, offset);
                }

                // Destroyed/untracked objects leave the slot zeroed rather than shifting every
                // later field, so one missing object can't change how the rest is interpreted.
                offset += TransformByteCount;
            }

            using var sha256 = SHA256.Create();
            return ToLowerHex(sha256.ComputeHash(buffer));
        }

        private static void WriteTransform(Transform t, byte[] buffer, int offset)
        {
            Vector3 position = t.localPosition;
            Quaternion rotation = t.localRotation;
            Vector3 scale = t.localScale;

            var span = new Span<byte>(buffer, offset, TransformByteCount);
            Span<float> floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(span);
            floats[0] = position.x;
            floats[1] = position.y;
            floats[2] = position.z;
            floats[3] = rotation.x;
            floats[4] = rotation.y;
            floats[5] = rotation.z;
            floats[6] = rotation.w;
            floats[7] = scale.x;
            floats[8] = scale.y;
            floats[9] = scale.z;
        }

        /// <summary>
        /// "Root/Child/Grandchild", with each step's sibling index appended so identically named
        /// siblings stay distinguishable.
        /// </summary>
        private static string BuildHierarchyPath(Transform t)
        {
            var sb = new StringBuilder(64);
            BuildHierarchyPath(t, sb);
            return sb.ToString();
        }

        private static void BuildHierarchyPath(Transform t, StringBuilder sb)
        {
            if (t.parent != null)
            {
                BuildHierarchyPath(t.parent, sb);
                sb.Append('/');
            }

            sb.Append(t.name).Append('[').Append(t.GetSiblingIndex()).Append(']');
        }

        private static string ToLowerHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }
    }
}
