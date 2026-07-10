using System;
using UnityEngine;

/// <summary>
/// Picks the smallest wire event that covers whatever changed on a transform this tick and
/// serializes it into the destination span (called from BluesStreamer.EnqueueTransform).
/// Event selection rules live in Spec.md Section 5.3; this class also owns the epsilon checks
/// (HasPositionChanged/HasRotationChanged/HasScaleChanged) that decide "did this change enough
/// to care about" for both the caller's gating and this dispatcher's own property selection.
/// </summary>
public static class TransformEventDispatcher
{
    private const float PositionEpsilon = 0.0001f;
    private const float ScaleEpsilon = 0.0001f;
    private const float RotationDotEpsilon = 0.999999f; // ~0.08 degree tolerance for "unchanged"
    private const float UniformScaleEpsilon = 0.0005f;
    private const float SingleAxisAlignmentDegrees = 0.25f; // how close to a cardinal axis counts as "single axis"

    public enum Axis : byte { X = 0, Y = 1, Z = 2 }

    public static bool HasPositionChanged(Vector3 current, Vector3 previous) =>
        !ApproximatelyEqual(current, previous, PositionEpsilon);

    public static bool HasRotationChanged(Quaternion current, Quaternion previous) =>
        !ApproximatelyEqualRotation(current, previous);

    public static bool HasScaleChanged(Vector3 current, Vector3 previous) =>
        !ApproximatelyEqual(current, previous, ScaleEpsilon);

    /// <summary>
    /// Serializes the best-fit event into destination (at least Serializer.MaxEventSize long)
    /// and returns the number of bytes written. Relies on data.SendTrue/PositionChanged/
    /// RotationChanged/ScaleChanged already being computed by the caller.
    /// </summary>
    public static int SerializeBestFitEvent(ushort objectId, in TransformData data, Span<byte> destination)
    {
        if (data.SendTrue)
        {
            return SerializeTrueBestFit(objectId, data, destination);
        }

        bool positionChanged = data.PositionChanged;
        bool rotationChanged = data.RotationChanged;
        bool scaleChanged = data.ScaleChanged;

        // Caller is expected to skip enqueuing entirely when nothing changed; handle it safely anyway.
        if (!positionChanged && !rotationChanged && !scaleChanged) return 0;

        Quaternion deltaRotation = rotationChanged
            ? Quaternion.Inverse(data.previousRotation) * data.currentRotation
            : Quaternion.identity;
        Vector3 deltaPosition = positionChanged ? data.currentPosition - data.previousPosition : Vector3.zero;
        Vector3 deltaScale = scaleChanged ? data.currentScale - data.previousScale : Vector3.zero;

        bool deltaScaleIsUniform = scaleChanged && IsUniform(deltaScale);

        Axis axis = default;
        float angleDeg = 0f;
        bool deltaRotationIsSingleAxis = rotationChanged && TryGetSingleAxis(deltaRotation, out axis, out angleDeg);

        // All three changed
        if (positionChanged && rotationChanged && scaleChanged)
        {
            return Serializer.SerializeDeltaTransform(objectId, deltaPosition, deltaRotation, deltaScale, destination);
        }

        // Position + Rotation
        if (positionChanged && rotationChanged && !scaleChanged)
        {
            if (deltaRotationIsSingleAxis)
                return Serializer.SerializeDeltaPositionRotationSingleAxis(objectId, deltaPosition, (byte)axis, angleDeg, destination);
            return Serializer.SerializeDeltaPositionRotation(objectId, deltaPosition, deltaRotation, destination);
        }

        // Rotation + Scale
        if (!positionChanged && rotationChanged && scaleChanged)
        {
            if (deltaScaleIsUniform)
                return Serializer.SerializeDeltaRotationUniformScale(objectId, deltaRotation, deltaScale.x, destination);
            return Serializer.SerializeDeltaRotationScale(objectId, deltaRotation, deltaScale, destination);
        }

        // Position + Scale
        if (positionChanged && !rotationChanged && scaleChanged)
        {
            if (deltaScaleIsUniform)
                return Serializer.SerializeDeltaPositionUniformScale(objectId, deltaPosition, deltaScale.x, destination);
            return Serializer.SerializeDeltaPositionScale(objectId, deltaPosition, deltaScale, destination);
        }

        // Position only
        if (positionChanged)
        {
            return Serializer.SerializeDeltaPosition(objectId, deltaPosition, destination);
        }

        // Rotation only
        if (rotationChanged)
        {
            if (deltaRotationIsSingleAxis)
                return Serializer.SerializeDeltaRotationSingleAxis(objectId, (byte)axis, angleDeg, destination);
            return Serializer.SerializeDeltaRotation(objectId, deltaRotation, destination);
        }

        // Scale only
        if (deltaScaleIsUniform)
            return Serializer.SerializeDeltaUniformScale(objectId, deltaScale.x, destination);
        return Serializer.SerializeDeltaScale(objectId, deltaScale, destination);
    }

    private static int SerializeTrueBestFit(ushort objectId, in TransformData data, Span<byte> destination)
    {
        // LastSent* isn't trusted here (see TransformData.SendTrue), so always send the full
        // transform, unless scale happens to be uniform (saves 4 bytes).
        bool isUniform = IsUniform(data.currentScale);

        if (isUniform)
        {
            return Serializer.SerializeTruePositionRotationUniformScale(
                objectId, data.currentPosition, data.currentRotation, data.currentScale.x, destination);
        }

        return Serializer.SerializeTrueTransform(
            objectId, data.currentPosition, data.currentRotation, data.currentScale, destination);
    }

    private static bool ApproximatelyEqual(Vector3 a, Vector3 b, float epsilon)
    {
        return Mathf.Abs(a.x - b.x) < epsilon
            && Mathf.Abs(a.y - b.y) < epsilon
            && Mathf.Abs(a.z - b.z) < epsilon;
    }

    private static bool ApproximatelyEqualRotation(Quaternion a, Quaternion b)
    {
        // Dot product of two unit quaternions is 1 (or -1, same rotation) when equal.
        float dot = Mathf.Abs(Quaternion.Dot(a, b));
        return dot >= RotationDotEpsilon;
    }

    private static bool IsUniform(Vector3 v)
    {
        return Mathf.Abs(v.x - v.y) < UniformScaleEpsilon
            && Mathf.Abs(v.y - v.z) < UniformScaleEpsilon;
    }

    /// <summary>
    /// True if rotation's axis-angle representation is closely aligned with one cardinal axis;
    /// outs that axis and the signed angle in degrees around it.
    /// </summary>
    private static bool TryGetSingleAxis(Quaternion rotation, out Axis axis, out float signedAngleDegrees)
    {
        rotation.ToAngleAxis(out float angle, out Vector3 rotationAxis);

        // Zero-length axis (identity/near-identity rotation) -- treat as "no rotation".
        if (rotationAxis.sqrMagnitude < 1e-8f)
        {
            axis = Axis.X;
            signedAngleDegrees = 0f;
            return true;
        }

        rotationAxis.Normalize();

        Vector3 absAxis = new Vector3(Mathf.Abs(rotationAxis.x), Mathf.Abs(rotationAxis.y), Mathf.Abs(rotationAxis.z));

        Axis bestAxis = absAxis.x >= absAxis.y && absAxis.x >= absAxis.z ? Axis.X
                       : absAxis.y >= absAxis.z ? Axis.Y
                       : Axis.Z;

        float alignment = bestAxis switch
        {
            Axis.X => absAxis.x,
            Axis.Y => absAxis.y,
            _ => absAxis.z
        };

        // alignment == 1 means the axis is exactly (+/-1,0,0)-like; convert the tolerance to cosine.
        float cosTolerance = Mathf.Cos(SingleAxisAlignmentDegrees * Mathf.Deg2Rad);
        if (alignment < cosTolerance)
        {
            axis = default;
            signedAngleDegrees = 0f;
            return false;
        }

        float sign = bestAxis switch
        {
            Axis.X => Mathf.Sign(rotationAxis.x),
            Axis.Y => Mathf.Sign(rotationAxis.y),
            _ => Mathf.Sign(rotationAxis.z)
        };

        axis = bestAxis;
        signedAngleDegrees = angle * sign;
        return true;
    }
}