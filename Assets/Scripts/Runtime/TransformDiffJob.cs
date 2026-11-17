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

        /// <summary>The change gate, by value so Burst never reads managed statics.</summary>
        public DeltaChangeGate Gate;

        public void Execute(int index, TransformAccess transform)
        {
            // Local space only. The protocol transmits local transforms, and reading world values
            // here would also pull in matrix work this pass has no use for.
            Vector3 position = transform.localPosition;
            Quaternion rotation = transform.localRotation;
            Vector3 scale = transform.localScale;

            TrackedState last = States[index];

            byte mask = 0;
            if (Gate.PositionChanged(position, last.LastSentPosition))
            {
                mask |= SampledTransform.PositionChangedBit;
            }

            if (Gate.RotationChanged(rotation, last.LastSentRotation))
            {
                mask |= SampledTransform.RotationChangedBit;
            }

            if (Gate.ScaleChanged(scale, last.LastSentScale))
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
    }
}
