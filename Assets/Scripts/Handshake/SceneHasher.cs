using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MoodyBlues
{
    /// <summary>
    /// Computes the sceneHash sent in the handshake request (see Spec.md Section 9).
    ///
    /// The question this hash answers is narrow: "is the scene export the backend already holds
    /// still valid for this scene?" So it has to fold in everything the export carries -- the
    /// object set, the ID mapping, the hierarchy, and the transforms baked into each node.
    ///
    /// WHEN it samples matters as much as what. It runs from MoodyBluesSessionManager.Start, which
    /// (with that component's execution order of 1000) is after every other Awake and Start. That
    /// pins the transforms it reads to a defined moment; without it, a script whose Start nudged an
    /// object could land either side of the hash depending on Unity's ordering, so an unmodified
    /// scene would hash differently between runs and re-upload itself for no reason.
    ///
    /// Buffer layout:
    ///   [sceneName UTF8] [0x00]
    ///   for each ObjectID (walk order):
    ///     [hierarchyPath UTF8] [0x00] [ObjectID 2B LE]
    ///     [localPosition 3xfloat32 LE] [localRotation 4xfloat32 LE] [localScale 3xfloat32 LE]
    /// SHA-256 of the whole buffer, lowercase hex.
    /// </summary>
    public static class SceneHasher
    {
        private const int TransformByteCount = 40; // 3 + 4 + 3 floats

        public static string ComputeSceneHash(MoodyBluesSessionManager sessionManager)
        {
            var objectIds = sessionManager.InitialWalkObjectIds;
            string sceneName = SceneManager.GetActiveScene().name;

            // Full hierarchy path rather than the bare name. Two same-named siblings that swap
            // order keep the same (name, id) pairs between runs, so a name-only hash would call
            // the scene unchanged; their paths differ by sibling index, so this does not.
            var paths = new string[objectIds.Count];
            for (int i = 0; i < objectIds.Count; i++)
            {
                paths[i] = sessionManager.TryGetTransform(objectIds[i], out Transform t)
                    ? BuildHierarchyPath(t)
                    : string.Empty;
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

                if (sessionManager.TryGetTransform(id, out Transform t))
                {
                    WriteTransform(t, buffer, offset);
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
