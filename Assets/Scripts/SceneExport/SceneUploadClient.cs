using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// POSTs an exported scene's raw .glb bytes to the backend's sceneUploadUrl (see Spec.md
/// Section 9). Body is gzip-compressed to cut transfer size, since UnityGLTF has no export-side
/// Draco support today (see SceneGltfExporter).
/// </summary>
public static class SceneUploadClient
{
    public static async Task UploadAsync(string sceneUploadUrl, byte[] glbBytes)
    {
        byte[] body = Gzip(glbBytes);

        using UnityWebRequest req = new UnityWebRequest(sceneUploadUrl, UnityWebRequest.kHttpVerbPOST)
        {
            uploadHandler = new UploadHandlerRaw(body),
            downloadHandler = new DownloadHandlerBuffer()
        };
        req.SetRequestHeader("Content-Type", "model/gltf-binary");
        req.SetRequestHeader("Content-Encoding", "gzip");

        var tcs = new TaskCompletionSource<bool>();
        UnityWebRequestAsyncOperation op = req.SendWebRequest();
        op.completed += _ => tcs.SetResult(true);
        await tcs.Task;

        if (req.result != UnityWebRequest.Result.Success)
        {
            throw new Exception($"SceneUploadClient: upload to '{sceneUploadUrl}' failed - {req.error}");
        }
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }
}
