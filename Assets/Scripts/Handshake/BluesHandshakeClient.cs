using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Orchestrates the backend handshake (see Spec.md Section 9): builds the request, POSTs it
/// with retries, then hands the resulting WebSocket URL to BluesSessionManager and -- if the
/// backend asks for it -- kicks off the scene export/upload in the background. Nothing here
/// blocks BluesSessionManager.FixedUpdate; it simply has no streamer to write to until this
/// completes (see BluesSessionManager.AttachStreamer).
/// </summary>
public static class BluesHandshakeClient
{
    private static readonly int[] BackoffMillis = { 1000, 2000, 4000 };

    public static async void PerformHandshakeAndConnect(BluesSessionManager sessionManager)
    {
        BluesClientConfig config = BluesClientConfig.Instance;
        if (config == null) return; // Already logged by BluesClientConfig.Instance.

        string sceneId = SceneIdentity.GetCurrentSceneId();
        if (string.IsNullOrEmpty(sceneId)) return; // Already logged by SceneIdentity.

        var request = new HandshakeRequest
        {
            developerId = config.DeveloperId,
            sceneId = sceneId,
            sceneHash = SceneHasher.ComputeSceneHash(sessionManager),
            sessionId = Guid.NewGuid().ToString()
        };

        HandshakeResponse response = await SendHandshakeWithRetriesAsync(config.BackendBaseUrl, request);
        if (response == null)
        {
            Debug.LogError("BluesHandshakeClient: handshake failed after all retries -- giving up for this session.");
            return;
        }

        Uri webSocketUri;
        try
        {
            webSocketUri = new Uri(response.webSocketUrl);
        }
        catch (Exception ex)
        {
            Debug.LogError($"BluesHandshakeClient: backend returned an invalid webSocketUrl '{response.webSocketUrl}' - {ex}");
            return;
        }

        sessionManager.AttachStreamer(new BluesStreamer(webSocketUri));
        Debug.Log($"BluesHandshakeClient: handshake complete, connecting to {webSocketUri}.");

        if (response.sceneUploadRequired)
        {
            UploadSceneAsync(response.sceneUploadUrl);
        }
    }

    private static async Task<HandshakeResponse> SendHandshakeWithRetriesAsync(string backendBaseUrl, HandshakeRequest request)
    {
        string json = JsonUtility.ToJson(request);
        string url = $"{backendBaseUrl}/handshake";

        for (int attempt = 0; attempt < BackoffMillis.Length + 1; attempt++)
        {
            using UnityWebRequest req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer()
            };
            req.SetRequestHeader("Content-Type", "application/json");

            await SendAsync(req);

            if (req.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    return JsonUtility.FromJson<HandshakeResponse>(req.downloadHandler.text);
                }
                catch (Exception ex)
                {
                    // Backend contract mismatch, not a transient failure -- retrying won't help.
                    Debug.LogError($"BluesHandshakeClient: failed to parse handshake response - {ex}\nBody: {req.downloadHandler.text}");
                    return null;
                }
            }

            Debug.LogWarning($"BluesHandshakeClient: handshake attempt {attempt + 1}/{BackoffMillis.Length + 1} failed - {req.error}");

            if (attempt < BackoffMillis.Length)
            {
                await Task.Delay(BackoffMillis[attempt]);
            }
        }

        return null;
    }

    private static async void UploadSceneAsync(string sceneUploadUrl)
    {
        try
        {
            byte[] glb = SceneGltfExporter.ExportActiveSceneToGlb();
            await SceneUploadClient.UploadAsync(sceneUploadUrl, glb);
            Debug.Log($"BluesHandshakeClient: scene upload complete ({glb.Length} bytes).");
        }
        catch (Exception ex)
        {
            Debug.LogError($"BluesHandshakeClient: scene export/upload failed - {ex}");
        }
    }

    private static Task SendAsync(UnityWebRequest request)
    {
        var tcs = new TaskCompletionSource<bool>();
        UnityWebRequestAsyncOperation op = request.SendWebRequest();
        op.completed += _ => tcs.SetResult(true);
        return tcs.Task;
    }
}
