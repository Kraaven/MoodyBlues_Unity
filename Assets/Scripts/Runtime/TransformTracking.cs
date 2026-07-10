using UnityEngine;

/// <summary>
/// Per-object bookkeeping for BluesSessionManager: the transform being polled and the last
/// state actually sent to the receiver (seeded at registration, updated after every send).
/// </summary>
public class TransformReference
{
    public Transform PollingTransform;
    public Vector3 LastSentPosition;
    public Quaternion LastSentRotation;
    public Vector3 LastSentScale;
}

/// <summary>
/// One tick's worth of transform data for a single object, passed from BluesSessionManager to
/// TransformEventDispatcher/BluesStreamer. Changed* flags are computed once by the caller and
/// never re-derived downstream.
/// </summary>
public struct TransformData
{
    public ushort ObjectId;
    public bool SendTrue;
    public bool PositionChanged;
    public bool RotationChanged;
    public bool ScaleChanged;

    public Vector3 currentPosition;
    public Quaternion currentRotation;
    public Vector3 currentScale;
    public Vector3 previousPosition;
    public Quaternion previousRotation;
    public Vector3 previousScale;
}
