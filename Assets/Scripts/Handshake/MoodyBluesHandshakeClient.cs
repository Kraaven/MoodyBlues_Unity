using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace MoodyBlues
{
    /// <summary>
    /// HTTP calls to the backend (see Spec.md Sections 9 and 13): the session handshake, the
    /// scene check, and the scene upload. Pure transport: MoodyBluesSessionManager decides when
    /// to call these and what to do with the result. All methods must be called from the main
    /// thread (UnityWebRequest requires it) and never throw.
    /// </summary>
    public static class MoodyBluesHandshakeClient
    {
        private static readonly int[] BackoffMillis = { 1000, 2000, 4000, 8000 };

        /// <summary>
        /// POSTs /handshake, retrying transient failures until <paramref name="deadlineUtc"/>.
        /// Returns null on failure, after logging why. <paramref name="isCancelled"/> is checked
        /// between attempts so a StopRecording during the handshake ends the retries.
        /// </summary>
        public static Task<HandshakeResponse> HandshakeAsync(
            MoodyBluesClientConfig config,
            HandshakeRequest request,
            DateTime deadlineUtc,
            Func<bool> isCancelled)
        {
            return PostJsonWithRetriesAsync<HandshakeResponse>(
                $"{config.BackendBaseUrl}/handshake", JsonUtility.ToJson(request), "handshake", deadlineUtc, isCancelled);
        }

        /// <summary>POSTs /scenes/check. Returns null on failure, after logging why.</summary>
        public static Task<SceneCheckResponse> CheckSceneAsync(
            MoodyBluesClientConfig config,
            SceneCheckRequest request,
            DateTime deadlineUtc)
        {
            return CheckSceneAsync(config.BackendBaseUrl, request, deadlineUtc);
        }

        /// <summary>As above, against an explicit backend URL (used by the editor's saved exports).</summary>
        public static Task<SceneCheckResponse> CheckSceneAsync(
            string backendBaseUrl,
            SceneCheckRequest request,
            DateTime deadlineUtc)
        {
            return PostJsonWithRetriesAsync<SceneCheckResponse>(
                $"{backendBaseUrl}/scenes/check", JsonUtility.ToJson(request), "scene check", deadlineUtc, null);
        }

        /// <summary>Uploads an exported scene. Returns false on failure, after logging why.</summary>
        public static async Task<bool> UploadSceneAsync(string sceneUploadUrl, byte[] glb, int skippedMeshes = 0)
        {
            try
            {
                await SceneUploadClient.UploadAsync(sceneUploadUrl, glb, skippedMeshes);
                Debug.Log($"MoodyBluesHandshakeClient: scene upload complete ({glb.Length} bytes).");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"MoodyBluesHandshakeClient: scene upload failed - {ex.Message}");
                return false;
            }
        }

        private static async Task<T> PostJsonWithRetriesAsync<T>(
            string url,
            string json,
            string what,
            DateTime deadlineUtc,
            Func<bool> isCancelled) where T : class
        {
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    if (isCancelled != null && isCancelled()) return null;

                    int remainingSeconds = (int)Math.Ceiling((deadlineUtc - DateTime.UtcNow).TotalSeconds);
                    if (remainingSeconds <= 0)
                    {
                        Debug.LogError($"MoodyBluesHandshakeClient: {what} did not succeed before the timeout, giving up.");
                        return null;
                    }

                    using UnityWebRequest req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
                    {
                        uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                        downloadHandler = new DownloadHandlerBuffer(),
                        timeout = remainingSeconds,
                    };
                    req.SetRequestHeader("Content-Type", "application/json");

                    await SendAsync(req);

                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        try
                        {
                            return JsonUtility.FromJson<T>(req.downloadHandler.text);
                        }
                        catch (Exception ex)
                        {
                            // Backend contract mismatch, not a transient failure -- retrying won't help.
                            Debug.LogError($"MoodyBluesHandshakeClient: failed to parse {what} response - {ex.Message}\nBody: {req.downloadHandler.text}");
                            return null;
                        }
                    }

                    // A 4xx (other than timeout/rate limit) means the request itself is wrong, e.g.
                    // an unknown developer ID. Retrying cannot fix that.
                    long status = req.responseCode;
                    if (status >= 400 && status < 500 && status != 408 && status != 429)
                    {
                        Debug.LogError($"MoodyBluesHandshakeClient: {what} rejected by the backend ({status}) - {req.downloadHandler.text}");
                        return null;
                    }

                    int delay = BackoffMillis[Math.Min(attempt, BackoffMillis.Length - 1)];
                    Debug.LogWarning($"MoodyBluesHandshakeClient: {what} attempt {attempt + 1} failed - {req.error}. Retrying in {delay}ms.");

                    if (DateTime.UtcNow + TimeSpan.FromMilliseconds(delay) >= deadlineUtc)
                    {
                        Debug.LogError($"MoodyBluesHandshakeClient: {what} did not succeed before the timeout, giving up.");
                        return null;
                    }

                    await Task.Delay(delay);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"MoodyBluesHandshakeClient: {what} failed unexpectedly - {ex}");
                return null;
            }
        }

        private static Task SendAsync(UnityWebRequest request)
        {
            var tcs = new TaskCompletionSource<bool>();
            UnityWebRequestAsyncOperation op = request.SendWebRequest();
            op.completed += _ => tcs.TrySetResult(true);
            return tcs.Task;
        }
    }
}
