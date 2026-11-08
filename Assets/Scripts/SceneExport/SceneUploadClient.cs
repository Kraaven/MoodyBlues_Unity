using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace MoodyBlues
{
    /// <summary>
    /// POSTs an exported scene's raw .glb bytes to the backend's sceneUploadUrl (see Spec.md
    /// Section 9). Body is gzip-compressed to cut transfer size, since UnityGLTF has no export-side
    /// Draco support today (see SceneGltfExporter). Works in Play mode and in the Editor.
    /// </summary>
    public static class SceneUploadClient
    {
        /// <summary>Tells the backend how many meshes the export had to leave out.</summary>
        public const string SkippedMeshesHeader = "X-MoodyBlues-Skipped-Meshes";

        public static async Task UploadAsync(string sceneUploadUrl, byte[] glbBytes, int skippedMeshes = 0)
        {
            byte[] body = Gzip(glbBytes);

            using UnityWebRequest req = new UnityWebRequest(sceneUploadUrl, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(body),
                downloadHandler = new DownloadHandlerBuffer()
            };
            req.SetRequestHeader("Content-Type", "model/gltf-binary");
            req.SetRequestHeader("Content-Encoding", "gzip");
            if (skippedMeshes > 0) req.SetRequestHeader(SkippedMeshesHeader, skippedMeshes.ToString());

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
}
