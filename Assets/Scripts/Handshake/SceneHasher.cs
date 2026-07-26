using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Computes the sceneHash sent in the handshake request (see Spec.md Section 9). Buffer layout:
///   [sceneName UTF8] [0x00]
///   for each ObjectID (walk order):
///     [objectName UTF8] [0x00] [ObjectID 2B LE]
///     [localPosition 3×float32 LE] [localRotation 4×float32 LE] [localScale 3×float32 LE]
/// SHA-256 of the whole buffer, lowercase hex.
/// </summary>
public static class SceneHasher
{
    public static string ComputeSceneHash(BluesSessionManager sessionManager)
    {
        var objectIds = sessionManager.InitialWalkObjectIds;
        string sceneName = SceneManager.GetActiveScene().name;

        int size = Encoding.UTF8.GetByteCount(sceneName) + 1;

        foreach (ushort id in objectIds)
        {
            sessionManager.TryGetTransform(id, out Transform t);
            string name = t != null ? t.name : string.Empty;
            size += Encoding.UTF8.GetByteCount(name) + 1; // name + \x00
            size += 2;  // ObjectID
            size += 40; // position (3f) + rotation (4f) + scale (3f)
        }

        byte[] buffer = new byte[size];
        int offset = 0;

        offset += Encoding.UTF8.GetBytes(sceneName, 0, sceneName.Length, buffer, offset);
        buffer[offset++] = 0x00;

        foreach (ushort id in objectIds)
        {
            sessionManager.TryGetTransform(id, out Transform t);
            string name = t != null ? t.name : string.Empty;

            offset += Encoding.UTF8.GetBytes(name, 0, name.Length, buffer, offset);
            buffer[offset++] = 0x00;

            buffer[offset++] = (byte)(id & 0xFF);
            buffer[offset++] = (byte)((id >> 8) & 0xFF);

            if (t != null)
            {
                var span = new Span<byte>(buffer, offset, 40);
                var floats = MemoryMarshal.Cast<byte, float>(span);
                floats[0] = t.localPosition.x;
                floats[1] = t.localPosition.y;
                floats[2] = t.localPosition.z;
                floats[3] = t.localRotation.x;
                floats[4] = t.localRotation.y;
                floats[5] = t.localRotation.z;
                floats[6] = t.localRotation.w;
                floats[7] = t.localScale.x;
                floats[8] = t.localScale.y;
                floats[9] = t.localScale.z;
                offset += 40;
            }
            else
            {
                offset += 40;
            }
        }

        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(buffer);
        return ToLowerHex(hash);
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
