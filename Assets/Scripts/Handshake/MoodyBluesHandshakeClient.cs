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
        /// <summary>Header carrying the project's ingest key on every backend call (Spec.md Section 9).</summary>
        public const string IngestKeyHeader = "X-MoodyBlues-Ingest-Key";

        /// <summary>Header carrying the session's stream token on the WebSocket upgrade.</summary>
        public const string StreamTokenHeader = "X-MoodyBlues-Stream-Token";

        private static readonly int[] BackoffMillis = { 1000, 2000, 4000, 8000 };

        /// <summary>Adds the ingest key header, if there is a key.</summary>
        public static void SetIngestKey(UnityWebRequest request, string ingestKey)
        {
            if (!string.IsNullOrEmpty(ingestKey)) request.SetRequestHeader(IngestKeyHeader, ingestKey);
        }

        /// <summary>
        /// POSTs /handshake, retrying transient failures until <paramref name="deadlineUtc"/>.
        /// Returns null on failure, after logging why. <paramref name="isCancelled"/> is checked
        /// between attempts so a StopRecording during the handshake ends the retries.
        /// </summary>
        public static Task<HandshakeResponse> HandshakeAsync(
            MoodyBluesConnection connection,
            HandshakeRequest request,
            DateTime deadlineUtc,
            Func<bool> isCancelled)
        {
            return PostJsonWithRetriesAsync<HandshakeResponse>(
                $"{connection.BaseUrl}/handshake", connection.IngestKey, JsonUtility.ToJson(request), "handshake", deadlineUtc, isCancelled);
        }

        /// <summary>POSTs /scenes/check. Returns null on failure, after logging why.</summary>
        public static Task<SceneCheckResponse> CheckSceneAsync(
            MoodyBluesConnection connection,
            SceneCheckRequest request,
            DateTime deadlineUtc)
        {
            return PostJsonWithRetriesAsync<SceneCheckResponse>(
                $"{connection.BaseUrl}/scenes/check", connection.IngestKey, JsonUtility.ToJson(request), "scene check", deadlineUtc, null);
        }

        /// <summary>
        /// GET /connection, once, no retries: the editor's Test Connection. Never throws. On success
        /// the message names the project; otherwise it says what to fix.
        /// </summary>
        public static async Task<(bool Ok, string Message)> TestConnectionAsync(MoodyBluesConnection connection, int timeoutSeconds = 15)
        {
            try
            {
                using UnityWebRequest req = UnityWebRequest.Get($"{connection.BaseUrl}/connection");
                req.timeout = timeoutSeconds;
                SetIngestKey(req, connection.IngestKey);
                await SendAsync(req);

                if (req.result == UnityWebRequest.Result.Success)
                {
                    ConnectionResponse body = JsonUtility.FromJson<ConnectionResponse>(req.downloadHandler.text);
                    return (true, $"Connected to project \"{body?.projectName}\" on {connection.DisplayHost}.");
                }

                if (req.responseCode == 401)
                {
                    return (false, "The server does not accept this key; it may have been rotated. Copy the current " +
                                   "connection string from the project page of the dashboard.");
                }

                if (req.responseCode == 404)
                {
                    return (false, $"{connection.DisplayHost} answered, but it is not a MoodyBlues server (or an older version). Check the connection string.");
                }

                return (false, req.responseCode > 0
                    ? $"{connection.DisplayHost} answered with HTTP {req.responseCode}. Try again in a moment."
                    : $"Could not reach {connection.DisplayHost}: {req.error}");
            }
            catch (Exception ex)
            {
                return (false, $"The connection test failed unexpectedly: {ex.Message}");
            }
        }

        /// <summary>Uploads an exported scene. Returns false on failure, after logging why.</summary>
        public static async Task<bool> UploadSceneAsync(string sceneUploadUrl, string ingestKey, byte[] glb, int skippedMeshes = 0)
        {
            try
            {
                await SceneUploadClient.UploadAsync(sceneUploadUrl, ingestKey, glb, skippedMeshes);
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
            string ingestKey,
            string json,
            string what,
            DateTime deadlineUtc,
            Func<bool> isCancelled) where T : class
        {
            try
            {
                if (string.IsNullOrEmpty(ingestKey))
                {
                    Debug.LogError($"MoodyBluesHandshakeClient: {what} skipped. {MoodyBluesProjectSettings.NotSetMessage}");
                    return null;
                }

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
                    SetIngestKey(req, ingestKey);

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
                    // a wrong or rotated ingest key. Retrying cannot fix that.
                    long status = req.responseCode;
                    if (status >= 400 && status < 500 && status != 408 && status != 429)
                    {
                        string hint = status == 401
                            ? " Paste the current connection string from the project page of the dashboard into MoodyBlues > Settings (it changes when the key is rotated)."
                            : "";
                        Debug.LogError($"MoodyBluesHandshakeClient: {what} rejected by the backend ({status}) - {req.downloadHandler.text}{hint}");
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
