using System;

namespace MoodyBlues
{
    /// <summary>
    /// Wire DTOs for the backend handshake (see Spec.md Section 13). Field names/casing must match
    /// the backend's JSON contract exactly -- JsonUtility maps them verbatim, no [JsonProperty]
    /// renaming is applied. The project is identified by the ingest key header, not by a field.
    /// </summary>
    [Serializable]
    public class HandshakeRequest
    {
        public string sceneId;
        public string sceneHash;
        public string sessionId;
    }

    [Serializable]
    public class HandshakeResponse
    {
        public string webSocketUrl;
        /// <summary>Secret for this session's WebSocket, sent in the X-MoodyBlues-Stream-Token header.</summary>
        public string streamToken;
        public bool sceneUploadRequired;
        public string sceneUploadUrl;
    }

    /// <summary>
    /// POST /scenes/check (Spec.md Section 13). Asks whether the backend holds an export matching
    /// this scene hash, without starting a session.
    /// </summary>
    [Serializable]
    public class SceneCheckRequest
    {
        public string sceneId;
        public string sceneHash;
    }

    [Serializable]
    public class SceneCheckResponse
    {
        public bool sceneUploadRequired;
        public string sceneUploadUrl;
    }

    /// <summary>GET /connection (Spec.md Section 13.6): the project the ingest key belongs to.</summary>
    [Serializable]
    public class ConnectionResponse
    {
        public string projectName;
    }
}
