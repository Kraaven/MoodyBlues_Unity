using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Computes the sceneHash sent in the handshake request (see Spec.md Section 9). Must match the
/// backend's own computation byte-for-byte: UTF-8 scene name, 0x00, then for every ObjectID from
/// the initial scene walk (in assignment order) UTF-8 object name, 0x00, ID as 2 bytes
/// little-endian. SHA-256 of the whole buffer, lowercase hex.
/// </summary>
public static class SceneHasher
{
    public static string ComputeSceneHash(BluesSessionManager sessionManager)
    {
        using var buffer = new System.IO.MemoryStream();

        WriteUtf8WithSeparator(buffer, SceneManager.GetActiveScene().name);

        foreach (ushort id in sessionManager.InitialWalkObjectIds)
        {
            string name = sessionManager.TryGetTransform(id, out Transform t) ? t.name : string.Empty;
            WriteUtf8WithSeparator(buffer, name);

            // Little-endian, matching Serializer's wire format for every other ushort field.
            buffer.WriteByte((byte)(id & 0xFF));
            buffer.WriteByte((byte)((id >> 8) & 0xFF));
        }

        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(buffer.ToArray());
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

    private static void WriteUtf8WithSeparator(System.IO.MemoryStream buffer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        buffer.Write(bytes, 0, bytes.Length);
        buffer.WriteByte(0x00);
    }
}
