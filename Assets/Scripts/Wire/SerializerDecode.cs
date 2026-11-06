using System;
using UnityEngine;

namespace MoodyBlues
{
    /// <summary>
    /// Client-side mirror of how a receiver applies a transform event to its running per-object
    /// state (True* events are absolute, Delta* events are cumulative -- Spec.md Section 5.3).
    ///
    /// This exists so the sender can track what the receiver will *actually reconstruct* rather
    /// than what the sender knows to be true. Quantization makes those two different, and because
    /// Delta* events are integrated by the receiver, feeding the true value back as the next
    /// baseline lets the error compound without bound. Decoding the bytes we just wrote and
    /// storing that as the baseline keeps the two sides in step, so the error stays pinned at a
    /// single quantization step forever instead of accumulating.
    ///
    /// Deliberately decodes the real wire bytes rather than recomputing the rounded values a
    /// second way: that makes it impossible for the feedback path and the encode path to disagree.
    /// It is also the reference the EditMode drift test asserts against, with no server involved.
    /// </summary>
    public static partial class Serializer
    {
        /// <summary>
        /// Applies one encoded transform event to a baseline, returning the state a receiver will
        /// hold afterwards. Properties the event doesn't carry pass through unchanged.
        /// Returns false if <paramref name="eventBytes"/> isn't a transform event (1-23).
        /// </summary>
        public static bool TryApplyTransformEvent(
            ReadOnlySpan<byte> eventBytes,
            Vector3 basePosition,
            Quaternion baseRotation,
            Vector3 baseScale,
            out Vector3 position,
            out Quaternion rotation,
            out Vector3 scale)
        {
            position = basePosition;
            rotation = baseRotation;
            scale = baseScale;

            if (eventBytes.Length < EnvelopeSize) return false;

            var eventType = (EventType)eventBytes[0];
            ReadOnlySpan<byte> p = eventBytes.Slice(EnvelopeSize);

            switch (eventType)
            {
                // ---- Absolute (True*) -------------------------------------------------------
                case EventType.TruePosition:
                    position = DecodeTruePosition(p);
                    return true;

                case EventType.TrueRotation:
                    rotation = DecodeTrueRotation(p);
                    return true;

                case EventType.TrueRotationSingleAxis:
                    rotation = DecodeTrueSingleAxisRotation(p);
                    return true;

                case EventType.TrueScale:
                    scale = DecodeScale(p);
                    return true;

                case EventType.TrueUniformScale:
                    scale = Uniform(DeserializeFloatFromUInt16Buffer(ScaleMin, ScaleMax, p.Slice(0, 2)));
                    return true;

                case EventType.TrueTransform:
                    position = DecodeTruePosition(p);
                    rotation = DecodeTrueRotation(p.Slice(6));
                    scale = DecodeScale(p.Slice(10));
                    return true;

                case EventType.TruePositionRotation:
                    position = DecodeTruePosition(p);
                    rotation = DecodeTrueRotation(p.Slice(6));
                    return true;

                case EventType.TrueRotationScale:
                    rotation = DecodeTrueRotation(p);
                    scale = DecodeScale(p.Slice(4));
                    return true;

                case EventType.TruePositionScale:
                    position = DecodeTruePosition(p);
                    scale = DecodeScale(p.Slice(6));
                    return true;

                case EventType.TruePositionRotationUniformScale:
                    position = DecodeTruePosition(p);
                    rotation = DecodeTrueRotation(p.Slice(6));
                    scale = Uniform(DeserializeFloatFromUInt16Buffer(ScaleMin, ScaleMax, p.Slice(10, 2)));
                    return true;

                case EventType.TruePositionRotationSingleAxis:
                    position = DecodeTruePosition(p);
                    rotation = DecodeTrueSingleAxisRotation(p.Slice(6));
                    return true;

                // ---- Lifecycle event that nonetheless re-baselines -------------------------
                // ReparentObject is a lifecycle event, but it carries an absolute local
                // transform (SetParent recomputes the local values, so the receiver's running
                // value is invalidated). Handled here so the sender's baseline follows the
                // quantized values it actually transmitted -- otherwise reparenting would
                // quietly reintroduce the accumulating-drift bug for that object.
                case EventType.ReparentObject:
                    position = DecodeTruePosition(p.Slice(2));
                    rotation = DecodeTrueRotation(p.Slice(8));
                    scale = DecodeScale(p.Slice(12));
                    return true;

                // ---- Cumulative (Delta*) ----------------------------------------------------
                case EventType.DeltaPosition:
                    position = basePosition + DecodeDeltaPosition(p);
                    return true;

                case EventType.DeltaRotation:
                    rotation = baseRotation * DecodeDeltaRotation(p);
                    return true;

                case EventType.DeltaRotationSingleAxis:
                    rotation = baseRotation * DecodeDeltaSingleAxisRotation(p);
                    return true;

                case EventType.DeltaScale:
                    scale = baseScale + DecodeDeltaScale(p);
                    return true;

                case EventType.DeltaUniformScale:
                    scale = baseScale + Uniform(DeserializeFloatFromByteBuffer(-DeltaScaleRange, DeltaScaleRange, p));
                    return true;

                case EventType.DeltaTransform:
                    position = basePosition + DecodeDeltaPosition(p);
                    rotation = baseRotation * DecodeDeltaRotation(p.Slice(4));
                    scale = baseScale + DecodeDeltaScale(p.Slice(7));
                    return true;

                case EventType.DeltaPositionRotation:
                    position = basePosition + DecodeDeltaPosition(p);
                    rotation = baseRotation * DecodeDeltaRotation(p.Slice(4));
                    return true;

                case EventType.DeltaRotationScale:
                    rotation = baseRotation * DecodeDeltaRotation(p);
                    scale = baseScale + DecodeDeltaScale(p.Slice(3));
                    return true;

                case EventType.DeltaPositionScale:
                    position = basePosition + DecodeDeltaPosition(p);
                    scale = baseScale + DecodeDeltaScale(p.Slice(4));
                    return true;

                case EventType.DeltaPositionRotationSingleAxis:
                    position = basePosition + DecodeDeltaPosition(p);
                    rotation = baseRotation * DecodeDeltaSingleAxisRotation(p.Slice(4));
                    return true;

                case EventType.DeltaPositionUniformScale:
                    position = basePosition + DecodeDeltaPosition(p);
                    scale = baseScale + Uniform(DeserializeFloatFromByteBuffer(-DeltaScaleRange, DeltaScaleRange, p.Slice(4)));
                    return true;

                case EventType.DeltaRotationUniformScale:
                    rotation = baseRotation * DecodeDeltaRotation(p);
                    scale = baseScale + Uniform(DeserializeFloatFromByteBuffer(-DeltaScaleRange, DeltaScaleRange, p.Slice(3)));
                    return true;

                default:
                    return false; // TimeStamp / lifecycle events carry no transform state
            }
        }

        private static Vector3 Uniform(float v) => new Vector3(v, v, v);

        private static Vector3 DecodeTruePosition(ReadOnlySpan<byte> p) =>
            DeserializeVector3FromUInt16Buffer(-PositionRange, PositionRange, p.Slice(0, 6));

        private static Vector3 DecodeScale(ReadOnlySpan<byte> p) =>
            DeserializeVector3FromUInt16Buffer(ScaleMin, ScaleMax, p.Slice(0, 6));

        private static Quaternion DecodeTrueRotation(ReadOnlySpan<byte> p) =>
            DeserializeQuaternionSmallestThreeFromBuffer(TrueRotationComponentRange, p.Slice(0, 4));

        private static Vector3 DecodeDeltaPosition(ReadOnlySpan<byte> p) =>
            UnpackDeltaPositionFromBuffer(DeltaPositionRange, p.Slice(0, 4));

        private static Quaternion DecodeDeltaRotation(ReadOnlySpan<byte> p) =>
            DeserializeQuaternionDropWFromBuffer(DeltaRotationComponentRange, p.Slice(0, 3));

        private static Vector3 DecodeDeltaScale(ReadOnlySpan<byte> p) =>
            DeserializeVector3FromByteBuffer(-DeltaScaleRange, DeltaScaleRange, p.Slice(0, 3));

        private static Quaternion DecodeTrueSingleAxisRotation(ReadOnlySpan<byte> p)
        {
            (byte axisId, float angle) = DeserializeRotationSingleAxisFromBuffer(
                TrueSingleAxisAngleMin, TrueSingleAxisAngleMax, p.Slice(0, 2));
            return AxisAngle(axisId, angle);
        }

        private static Quaternion DecodeDeltaSingleAxisRotation(ReadOnlySpan<byte> p)
        {
            (byte axisId, float angle) = DeserializeRotationSingleAxisFromBuffer(
                -DeltaSingleAxisAngleRange, DeltaSingleAxisAngleRange, p.Slice(0, 2));
            return AxisAngle(axisId, angle);
        }

        /// <summary>
        /// Axis id (0=X, 1=Y, 2=Z) plus a signed angle in degrees to a quaternion. Must stay
        /// equivalent to the receiver's reconstruction; both produce (sin(theta/2) * axis,
        /// cos(theta/2)), so Unity's degree-based AngleAxis and a radian-based
        /// CreateFromAxisAngle agree.
        /// </summary>
        private static Quaternion AxisAngle(byte axisId, float angleDegrees)
        {
            Vector3 axis = axisId switch
            {
                1 => Vector3.up,
                2 => Vector3.forward,
                _ => Vector3.right,
            };
            return Quaternion.AngleAxis(angleDegrees, axis);
        }
    }
}
