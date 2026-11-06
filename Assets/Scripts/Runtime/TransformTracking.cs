using UnityEngine;

namespace MoodyBlues
{
    /// <summary>
    /// The last state actually sent to the receiver for one tracked object. Lives in a
    /// NativeArray held parallel to the TransformAccessArray, so it has to stay an unmanaged
    /// struct -- which is why this replaced the old <c>TransformReference</c> class (a managed
    /// type forced the capture set to be a dictionary of heap objects, scattering every tick's
    /// reads across memory).
    ///
    /// Uses UnityEngine.Vector3/Quaternion rather than Unity.Mathematics float3/quaternion
    /// deliberately: both are blittable and Burst-compatible, and these values are handed
    /// straight to <see cref="Serializer"/>, which speaks Vector3/Quaternion. Converting at both
    /// ends would add noise for no codegen benefit on a pass this simple.
    /// </summary>
    public struct TrackedState
    {
        public Vector3 LastSentPosition;
        public Quaternion LastSentRotation;
        public Vector3 LastSentScale;

        public static TrackedState FromTransform(Transform t) => new TrackedState
        {
            LastSentPosition = t.localPosition,
            LastSentRotation = t.localRotation,
            LastSentScale = t.localScale,
        };
    }

    /// <summary>
    /// One capture tick's reading of one tracked transform, produced by
    /// <see cref="TransformDiffJob"/>.
    ///
    /// Carries the sampled values as well as the change mask so the serialization pass never has
    /// to touch the Transform again. Re-reading it on the main thread would be a second
    /// managed-to-native round trip per object and would undo most of the point of the job.
    /// </summary>
    public struct SampledTransform
    {
        public const byte PositionChangedBit = 1 << 0;
        public const byte RotationChangedBit = 1 << 1;
        public const byte ScaleChangedBit = 1 << 2;

        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;

        /// <summary>Bitmask of <c>*ChangedBit</c>; zero means nothing moved past its epsilon.</summary>
        public byte ChangedMask;

        public bool PositionChanged => (ChangedMask & PositionChangedBit) != 0;
        public bool RotationChanged => (ChangedMask & RotationChangedBit) != 0;
        public bool ScaleChanged => (ChangedMask & ScaleChangedBit) != 0;
        public bool AnythingChanged => ChangedMask != 0;
    }

    /// <summary>
    /// One tick's worth of transform data for a single object, passed from
    /// MoodyBluesSessionManager to TransformEventDispatcher/MoodyBluesStreamer. Changed* flags are
    /// computed once by the caller and never re-derived downstream.
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
}
