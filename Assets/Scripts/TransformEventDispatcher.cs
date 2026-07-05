using System;
using UnityEngine;

/// <summary>
/// Decides which of the 23 event types best represents a transform's change this tick,
/// and serializes it directly into the destination span. This is the logic that fills
/// in what BluesStreamer.SerializeTransformData was stubbed out for.
///
/// Rules (documented explicitly since none of this was specified upstream):
///
/// 1. TRUE vs DELTA: an object's first-ever tick (IsFirstTick == true, tracked by the
///    caller since TransformData itself doesn't carry "have we sent this before") always
///    sends a True* event, because there's no previous state for a receiver to apply a
///    delta on top of. Every tick after that sends Delta* events.
///
/// 2. WHICH PROPERTIES CHANGED: position/rotation/scale are each compared against the
///    previous tick's value with a small epsilon, and only changed properties are
///    included in the chosen event — this is what makes "combined" events worthwhile
///    (an object that's only rotating doesn't pay for position bytes it doesn't need).
///
/// 3. UNIFORM SCALE: a scale delta/value qualifies as "uniform" only if x == y == z
///    within epsilon; this collapses 3 bytes -> 1 (delta) or 6 bytes -> 2 (true).
///
/// 4. SINGLE-AXIS ROTATION: a rotation delta qualifies as "single axis" only if its
///    axis-angle representation is aligned with a cardinal axis (X/Y/Z) within a small
///    angular tolerance -- e.g. a spinning wheel or turret rotates purely around one
///    axis every tick. Anything else (free rotation, combined pitch+yaw, etc.) falls
///    back to the full quaternion event. This is an optimization on top of correctness,
///    so the tolerance is intentionally tight to avoid visibly wrong playback.
/// </summary>
public static class TransformEventDispatcher
{
    private const float PositionEpsilon = 0.0001f;
    private const float ScaleEpsilon = 0.0001f;
    private const float RotationDotEpsilon = 0.999999f; // ~0.08 degree tolerance for "unchanged"
    private const float UniformScaleEpsilon = 0.0005f;
    private const float SingleAxisAlignmentDegrees = 0.25f; // how close to a cardinal axis counts as "single axis"

    public enum Axis : byte { X = 0, Y = 1, Z = 2 }

    /// <summary>
    /// Serializes the correct event for this transform's change this tick directly into
    /// destination. Returns the number of bytes written (matches one of the SizeXxx
    /// consts in Serializer). destination must be at least Serializer.MaxEventSize long;
    /// the dispatcher only uses however many bytes the chosen event actually needs.
    /// </summary>
    public static int SerializeBestFitEvent(ushort objectId, in TransformData data, bool SendFullTransform, Span<byte> destination)
    {
        if (SendFullTransform)
        {
            return SerializeTrueBestFit(objectId, data, destination);
        }

        bool positionChanged = !ApproximatelyEqual(data.currentPosition, data.previousPosition, PositionEpsilon);
        bool rotationChanged = !ApproximatelyEqualRotation(data.currentRotation, data.previousRotation);
        bool scaleChanged = !ApproximatelyEqual(data.currentScale, data.previousScale, ScaleEpsilon);

        if (!positionChanged && !rotationChanged && !scaleChanged)
        {
            // Nothing changed at all -- cheapest possible representation: treat as a
            // zero position delta so downstream logic doesn't need a 24th "no-op" event
            // type. Caller can choose to skip calling this entirely in this case instead
            // (see BluesStreamer for that optimization) but we handle it safely either way.
            return 0;
        }

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
        // First tick: we don't try to omit "unchanged" properties (there's no previous
        // state to compare against), so this always sends the full Transform unless the
        // object's scale is uniform, in which case we save 4 bytes.
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
    /// Returns true if the rotation's axis-angle representation is closely aligned with
    /// one cardinal axis, out-ing that axis and the signed angle in degrees around it
    /// (negative meaning rotated the opposite way around the positive axis direction).
    /// </summary>
    private static bool TryGetSingleAxis(Quaternion rotation, out Axis axis, out float signedAngleDegrees)
    {
        rotation.ToAngleAxis(out float angle, out Vector3 rotationAxis);

        // Unity can return a zero-length axis for an identity/near-identity rotation;
        // treat that as "no rotation" rather than crashing on normalization.
        if (rotationAxis.sqrMagnitude < 1e-8f)
        {
            axis = Axis.X;
            signedAngleDegrees = 0f;
            return true;
        }

        rotationAxis.Normalize();

        // atan2-based comparison to the ideal cardinal axis, converted to degrees, tells
        // us how far off-axis this rotation is.
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

        // alignment == 1 means the axis vector is exactly (±1,0,0)-like; convert the
        // allowed tolerance in degrees to a cosine threshold.
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