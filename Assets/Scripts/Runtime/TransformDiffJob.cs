using Unity.Burst;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Jobs;

namespace MoodyBlues
{
    /// <summary>
    /// Reads every tracked transform and compares it against the last state sent, writing both the
    /// sampled values and a change mask into <see cref="Sampled"/>.
    ///
    /// Scheduled with <c>ScheduleReadOnly</c> (see <c>MoodyBluesSessionManager.RunDiffJob</c>).
    /// That matters: this pass never writes to a Transform, and read-only scheduling parallelises
    /// across all transforms rather than only across separate root hierarchies. Plain
    /// <c>Schedule</c> splits work per hierarchy, so a capture set that is one deep scene tree --
    /// the normal case here -- would execute entirely on a single worker thread.
    /// </summary>
    [BurstCompile]
    public struct TransformDiffJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeArray<TrackedState> States;

        /// <summary>
        /// Index-aligned output. Each invocation writes exactly the one element matching its own
        /// index, so there is no race despite the disabled range check -- which is needed only
        /// because the safety system can't infer per-worker index ranges for transform jobs.
        /// </summary>
        [WriteOnly]
        [NativeDisableParallelForRestriction]
        public NativeArray<SampledTransform> Sampled;

        public float PositionEpsilon;
        public float ScaleEpsilon;
        public float RotationDotEpsilon;

        public void Execute(int index, TransformAccess transform)
        {
            // Local space only. The protocol transmits local transforms, and reading world values
            // here would also pull in matrix work this pass has no use for.
            Vector3 position = transform.localPosition;
            Quaternion rotation = transform.localRotation;
            Vector3 scale = transform.localScale;

            TrackedState last = States[index];

            byte mask = 0;
            if (!ApproximatelyEqual(position, last.LastSentPosition, PositionEpsilon))
            {
                mask |= SampledTransform.PositionChangedBit;
            }

            if (!ApproximatelyEqualRotation(rotation, last.LastSentRotation, RotationDotEpsilon))
            {
                mask |= SampledTransform.RotationChangedBit;
            }

            if (!ApproximatelyEqual(scale, last.LastSentScale, ScaleEpsilon))
            {
                mask |= SampledTransform.ScaleChangedBit;
            }

            Sampled[index] = new SampledTransform
            {
                Position = position,
                Rotation = rotation,
                Scale = scale,
                ChangedMask = mask,
            };
        }

        // Written as explicit float math rather than calling Mathf/Quaternion helpers so the whole
        // comparison is unambiguously Burst-compilable. TransformEventDispatcher exposes the same
        // logic for the main-thread paths, and both read their epsilons from the constants there,
        // so the two cannot silently disagree.

        public static bool ApproximatelyEqual(Vector3 a, Vector3 b, float epsilon)
        {
            float dx = a.x - b.x; if (dx < 0f) dx = -dx;
            float dy = a.y - b.y; if (dy < 0f) dy = -dy;
            float dz = a.z - b.z; if (dz < 0f) dz = -dz;
            return dx < epsilon && dy < epsilon && dz < epsilon;
        }

        public static bool ApproximatelyEqualRotation(Quaternion a, Quaternion b, float dotEpsilon)
        {
            // |dot| of two unit quaternions is 1 when they represent the same rotation. The
            // absolute value is what makes q and -q compare equal, which they must, since they
            // are the same rotation.
            float dot = (a.x * b.x) + (a.y * b.y) + (a.z * b.z) + (a.w * b.w);
            if (dot < 0f) dot = -dot;
            return dot >= dotEpsilon;
        }
    }
}
