using System;
using UnityEngine;

namespace MoodyBlues
{
    /// <summary>
    /// What one encoded transform event did, returned by
    /// <see cref="TransformEventDispatcher.SerializeBestFitEvent"/>.
    ///
    /// <see cref="Position"/>/<see cref="Rotation"/>/<see cref="Scale"/> are the state the
    /// *receiver* will hold once it applies this event -- decoded back out of the bytes just
    /// written, not recomputed. Store them as the new baseline: that is what keeps quantization
    /// error pinned at one step instead of accumulating (see Serializer.TryApplyTransformEvent).
    /// </summary>
    public struct TransformEncodeResult
    {
        public int BytesWritten;

        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;

        /// <summary>
        /// Properties whose delta exceeded its range and forced an absolute (True*) encoding.
        /// Bits are <see cref="SampledTransform"/>'s *ChangedBit values. Non-zero means the
        /// configured delta range is too tight for this object's motion.
        /// </summary>
        public byte PromotedMask;

        /// <summary>
        /// A rotation delta that was axis-aligned but too large for the 2-byte single-axis
        /// encoding, so the 3-byte drop-W form was used instead. Costs one byte; purely a
        /// signal that the single-axis angle range is tight.
        /// </summary>
        public bool RotationWidened;

        /// <summary>
        /// Even the widest available encoding had to clamp -- the scene genuinely exceeds the
        /// configured position/scale range, which no promotion can fix.
        /// </summary>
        public bool HardClamped;

        public bool Wrote => BytesWritten > 0;
    }

    /// <summary>
    /// Picks the smallest wire event that covers whatever changed on a transform this tick and
    /// serializes it into the destination span (called from MoodyBluesStreamer.EnqueueTransform).
    /// Event selection rules live in Spec.md Section 5.3; this class also owns the epsilon checks
    /// (HasPositionChanged/HasRotationChanged/HasScaleChanged) that decide "did this change enough
    /// to care about" for both the caller's gating and this dispatcher's own property selection.
    ///
    /// Ranges are deliberately tight so precision stays concentrated where motion actually lives.
    /// Values that fall outside them are *promoted* to a wider encoding rather than clamped: a
    /// clamped Delta* is integrated by the receiver and never recovered, so clamping trades a few
    /// saved bytes for permanent, silent divergence.
    /// </summary>
    public static class TransformEventDispatcher
    {
        // Public so TransformDiffJob can be handed the same values -- the job needs them as
        // fields (Burst can't read managed statics), and duplicating the numbers there would let
        // the job and the main-thread paths silently drift apart.
        public const float PositionEpsilon = 0.0001f;
        public const float ScaleEpsilon = 0.0001f;
        public const float RotationDotEpsilon = 0.999999f; // ~0.08 degree tolerance for "unchanged"

        private const float UniformScaleEpsilon = 0.0005f;
        private const float SingleAxisAlignmentDegrees = 0.25f; // how close to a cardinal axis counts as "single axis"

        public enum Axis : byte { X = 0, Y = 1, Z = 2 }

        public static bool HasPositionChanged(Vector3 current, Vector3 previous) =>
            !TransformDiffJob.ApproximatelyEqual(current, previous, PositionEpsilon);

        public static bool HasRotationChanged(Quaternion current, Quaternion previous) =>
            !TransformDiffJob.ApproximatelyEqualRotation(current, previous, RotationDotEpsilon);

        public static bool HasScaleChanged(Vector3 current, Vector3 previous) =>
            !TransformDiffJob.ApproximatelyEqual(current, previous, ScaleEpsilon);

        /// <summary>
        /// Serializes the best-fit event into destination (at least Serializer.MaxEventSize long).
        /// Relies on data.SendTrue/PositionChanged/RotationChanged/ScaleChanged already being
        /// computed by the caller. The returned result carries the new baseline to store.
        /// </summary>
        public static TransformEncodeResult SerializeBestFitEvent(ushort objectId, in TransformData data, Span<byte> destination)
        {
            var result = new TransformEncodeResult
            {
                // Default to "nothing changed": properties an event doesn't carry pass through.
                Position = data.previousPosition,
                Rotation = data.previousRotation,
                Scale = data.previousScale,
            };

            if (data.SendTrue)
            {
                result.BytesWritten = SerializeTrueForProperties(
                    objectId, data, position: true, rotation: true, scale: true, destination);
            }
            else
            {
                result.BytesWritten = SerializeDeltaWithPromotion(
                    objectId, data, destination, out result.PromotedMask, out result.RotationWidened);
            }

            if (result.BytesWritten <= 0) return result;

            result.HardClamped =
                !Serializer.FitsTruePosition(data.currentPosition) ||
                !Serializer.FitsTrueScale(data.currentScale);

            // Decode the bytes we just wrote and apply them exactly as the receiver will. Doing it
            // this way -- rather than recomputing the rounded values independently -- makes it
            // impossible for the feedback baseline to drift away from what was actually sent.
            Serializer.TryApplyTransformEvent(
                destination.Slice(0, result.BytesWritten),
                data.previousPosition,
                data.previousRotation,
                data.previousScale,
                out result.Position,
                out result.Rotation,
                out result.Scale);

            return result;
        }

        private static int SerializeDeltaWithPromotion(
            ushort objectId,
            in TransformData data,
            Span<byte> destination,
            out byte promotedMask,
            out bool rotationWidened)
        {
            promotedMask = 0;
            rotationWidened = false;

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

            if (positionChanged && !Serializer.FitsDeltaPosition(deltaPosition))
            {
                promotedMask |= SampledTransform.PositionChangedBit;
            }

            if (scaleChanged && !Serializer.FitsDeltaScale(deltaScale))
            {
                promotedMask |= SampledTransform.ScaleChangedBit;
            }

            // Rotation has a three-rung ladder: single-axis (2 bytes) -> drop-W (3 bytes) ->
            // absolute smallest-three. The single-axis range is by far the tightest thing in the
            // protocol, and axis-aligned spin is the most common rotation there is, so this rung
            // matters more than it looks: without it a cardinal-axis spin faster than the
            // single-axis ceiling would be silently recorded at the ceiling speed.
            Axis axis = default;
            float angleDeg = 0f;
            bool deltaRotationIsSingleAxis = false;

            if (rotationChanged)
            {
                if (TryGetSingleAxis(deltaRotation, out axis, out angleDeg))
                {
                    if (Serializer.FitsDeltaSingleAxisAngle(angleDeg))
                    {
                        deltaRotationIsSingleAxis = true;
                    }
                    else
                    {
                        // Axis-aligned but spinning too fast for 2 bytes: fall to drop-W.
                        rotationWidened = true;
                    }
                }

                if (!deltaRotationIsSingleAxis && !Serializer.FitsDeltaRotation(deltaRotation))
                {
                    promotedMask |= SampledTransform.RotationChangedBit;
                }
            }

            if (promotedMask != 0)
            {
                // Promote the whole event rather than splitting into a narrower Delta* plus a
                // separate True*: one 19-byte TrueTransform beats a 10-byte delta plus a 7-byte
                // absolute, and it keeps the selection logic something a person can follow.
                return SerializeTrueForProperties(objectId, data, positionChanged, rotationChanged, scaleChanged, destination);
            }

            bool deltaScaleIsUniform = scaleChanged && IsUniform(deltaScale);

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

        /// <summary>
        /// Emits the smallest absolute (True*) event covering exactly the requested properties.
        /// Used both when the caller has no trustworthy baseline (<c>SendTrue</c>, e.g. an object
        /// reactivated after being hidden) and as the overflow promotion target.
        /// </summary>
        private static int SerializeTrueForProperties(
            ushort objectId,
            in TransformData data,
            bool position,
            bool rotation,
            bool scale,
            Span<byte> destination)
        {
            Vector3 p = data.currentPosition;
            Quaternion r = data.currentRotation;
            Vector3 s = data.currentScale;
            bool uniformScale = IsUniform(s);

            if (position && rotation && scale)
            {
                if (uniformScale)
                    return Serializer.SerializeTruePositionRotationUniformScale(objectId, p, r, s.x, destination);
                return Serializer.SerializeTrueTransform(objectId, p, r, s, destination);
            }

            if (position && rotation) return Serializer.SerializeTruePositionRotation(objectId, p, r, destination);
            if (rotation && scale) return Serializer.SerializeTrueRotationScale(objectId, r, s, destination);
            if (position && scale) return Serializer.SerializeTruePositionScale(objectId, p, s, destination);
            if (position) return Serializer.SerializeTruePosition(objectId, p, destination);
            if (rotation) return Serializer.SerializeTrueRotation(objectId, r, destination);

            if (uniformScale) return Serializer.SerializeTrueUniformScale(objectId, s.x, destination);
            return Serializer.SerializeTrueScale(objectId, s, destination);
        }

        private static bool IsUniform(Vector3 v)
        {
            return Mathf.Abs(v.x - v.y) < UniformScaleEpsilon
                && Mathf.Abs(v.y - v.z) < UniformScaleEpsilon;
        }

        /// <summary>
        /// True if rotation's axis-angle representation is closely aligned with one cardinal axis;
        /// outs that axis and the signed angle in degrees around it. Whether that angle actually
        /// *fits* the single-axis encoding is a separate question -- see
        /// Serializer.FitsDeltaSingleAxisAngle.
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
}
