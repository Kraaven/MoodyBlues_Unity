using NUnit.Framework;
using UnityEngine;

namespace MoodyBlues.Tests
{
    /// <summary>
    /// The test this project most needed and did not have.
    ///
    /// Delta* events are integrated by the receiver, so if the sender keeps its baseline as the
    /// true float rather than the value it actually encoded, the two sides diverge a little every
    /// tick and never recover. Constant velocity is the worst case, not the best: an identical
    /// delta rounds the same direction every single tick, so the error is a steady accumulation
    /// rather than a random walk that cancels out.
    ///
    /// These tests assert the error stays bounded no matter how long the session runs. The bound
    /// is deliberately far tighter than the accumulating behaviour would produce: at 40Hz with the
    /// default +/-1.5 delta range, feeding the true value back drifts on the order of a whole unit
    /// over the tick counts used here, versus the ~0.001 a correct implementation holds.
    /// </summary>
    [TestFixture]
    public sealed class QuantizationDriftTests
    {
        private const float PositionRange = 500f;
        private const float DeltaPositionRange = 1.5f;

        /// <summary>
        /// One quantization step of the 10-bit (coarsest) delta-position component, with slack for
        /// float noise. A correct implementation never exceeds roughly half a step.
        /// </summary>
        private const float BoundedPositionError = 0.01f;

        [SetUp]
        public void SetUp()
        {
            // Static ranges are session configuration, so pin them rather than inheriting whatever
            // a previous test or a previous play session left behind.
            Serializer.ConfigureRanges(
                positionRange: PositionRange,
                scaleMin: -1f,
                scaleMax: 10f,
                deltaPositionRange: DeltaPositionRange,
                deltaRotationComponentRange: 0.5f,
                deltaSingleAxisAngleRange: 2f,
                deltaScaleRange: 1f);
        }

        [Test]
        [TestCase(100)]
        [TestCase(2000)]
        [TestCase(20000)]
        public void ConstantVelocity_PositionErrorStaysBounded(int tickCount)
        {
            // Chosen so the per-tick delta does NOT land on an exact quantization step -- a delta
            // that quantizes perfectly would pass even with the accumulating bug.
            var velocityPerTick = new Vector3(0.0137f, -0.0041f, 0.0083f);

            Vector3 truePosition = new Vector3(1.5f, 2.5f, -3.5f);
            Vector3 baseline = truePosition;

            float worstError = 0f;

            for (int tick = 0; tick < tickCount; tick++)
            {
                truePosition += velocityPerTick;

                TransformEncodeResult encoded = Encode(baseline, truePosition);
                Assert.IsTrue(encoded.Wrote, $"tick {tick}: expected an event for a moving object");

                // This is the whole point: the next delta is measured against what the receiver
                // reconstructed, not against the true value.
                baseline = encoded.Position;

                float error = (truePosition - encoded.Position).magnitude;
                if (error > worstError) worstError = error;
            }

            Assert.Less(
                worstError,
                BoundedPositionError,
                $"after {tickCount} ticks the reconstruction error reached {worstError:F6}. " +
                "That means quantization error is accumulating instead of staying pinned at one step.");
        }

        [Test]
        public void ConstantVelocity_ErrorDoesNotGrowWithTickCount()
        {
            // Stronger than a fixed bound: proves the error is bounded rather than merely small
            // over the sample taken. An accumulating implementation would show the 20000-tick run
            // far worse than the 200-tick run.
            float shortRun = WorstErrorOverTicks(200);
            float longRun = WorstErrorOverTicks(20000);

            Assert.Less(
                longRun,
                shortRun + BoundedPositionError,
                $"error grew with session length ({shortRun:F6} over 200 ticks vs {longRun:F6} over " +
                "20000). Bounded error must not depend on how long the session has been running.");
        }

        [Test]
        public void ConstantAngularVelocity_RotationErrorStaysBounded()
        {
            // 3 degrees per tick about Y = 120 deg/s at 40Hz, which deliberately exceeds the
            // 2-degree single-axis range so this also exercises the widening path.
            Quaternion perTick = Quaternion.AngleAxis(3f, Vector3.up);

            Quaternion trueRotation = Quaternion.identity;
            Quaternion baseline = Quaternion.identity;

            float worstAngleError = 0f;

            for (int tick = 0; tick < 2000; tick++)
            {
                trueRotation = trueRotation * perTick;

                TransformEncodeResult encoded = EncodeRotation(baseline, trueRotation);
                Assert.IsTrue(encoded.Wrote, $"tick {tick}: expected an event for a rotating object");

                baseline = encoded.Rotation;

                float angle = Quaternion.Angle(trueRotation, encoded.Rotation);
                if (angle > worstAngleError) worstAngleError = angle;
            }

            Assert.Less(
                worstAngleError,
                2f,
                $"rotation reconstruction error reached {worstAngleError:F4} degrees, which indicates " +
                "accumulation rather than a bounded per-event rounding error.");
        }

        private static float WorstErrorOverTicks(int tickCount)
        {
            var velocityPerTick = new Vector3(0.0137f, -0.0041f, 0.0083f);
            Vector3 truePosition = Vector3.zero;
            Vector3 baseline = Vector3.zero;
            float worst = 0f;

            for (int tick = 0; tick < tickCount; tick++)
            {
                truePosition += velocityPerTick;
                TransformEncodeResult encoded = Encode(baseline, truePosition);
                baseline = encoded.Position;
                float error = (truePosition - encoded.Position).magnitude;
                if (error > worst) worst = error;
            }

            return worst;
        }

        private static TransformEncodeResult Encode(Vector3 baseline, Vector3 current)
        {
            var data = new TransformData
            {
                ObjectId = 1,
                SendTrue = false,
                PositionChanged = true,
                RotationChanged = false,
                ScaleChanged = false,
                currentPosition = current,
                currentRotation = Quaternion.identity,
                currentScale = Vector3.one,
                previousPosition = baseline,
                previousRotation = Quaternion.identity,
                previousScale = Vector3.one,
            };

            var scratch = new byte[Serializer.MaxEventSize];
            return TransformEventDispatcher.SerializeBestFitEvent(data.ObjectId, in data, scratch);
        }

        private static TransformEncodeResult EncodeRotation(Quaternion baseline, Quaternion current)
        {
            var data = new TransformData
            {
                ObjectId = 1,
                SendTrue = false,
                PositionChanged = false,
                RotationChanged = true,
                ScaleChanged = false,
                currentPosition = Vector3.zero,
                currentRotation = current,
                currentScale = Vector3.one,
                previousPosition = Vector3.zero,
                previousRotation = baseline,
                previousScale = Vector3.one,
            };

            var scratch = new byte[Serializer.MaxEventSize];
            return TransformEventDispatcher.SerializeBestFitEvent(data.ObjectId, in data, scratch);
        }
    }
}
