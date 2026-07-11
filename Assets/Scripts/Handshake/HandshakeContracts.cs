using System;

/// <summary>
/// Wire DTOs for the backend handshake (see Spec.md Section 9). Field names/casing must match
/// the backend's JSON contract exactly -- JsonUtility maps them verbatim, no [JsonProperty]
/// renaming is applied.
/// </summary>
[Serializable]
public class HandshakeRequest
{
    public string developerId;
    public string sceneId;
    public string sceneHash;
    public string sessionId;
}

[Serializable]
public class HandshakeResponse
{
    public string webSocketUrl;
    public bool sceneUploadRequired;
    public string sceneUploadUrl;
}
