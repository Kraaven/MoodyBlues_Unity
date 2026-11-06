using NUnit.Framework;
using UnityEngine;

namespace MoodyBlues.Tests
{
    /// <summary>
    /// Event selection: which of the 23 transform events gets chosen for a given set of changed
    /// properties, and whether out-of-range values are promoted rather than clamped.
    ///
    /// The promotion tests are the interesting ones. Clamping a Delta* is not a one-frame artefact
    /// the way clamping an absolute value is -- the receiver integrates deltas, so a clamped delta
    /// is an error the object never recovers from. The dispatcher is supposed to notice and widen
    /// the encoding instead, and these pin that behaviour down.
    /// </summary>
    [TestFixture]
    public sealed class TransformEventSelectionTests
    {
        private const float DeltaPositionRange = 1.5f;
        private const float DeltaSingleAxisAngleRange = 2f;

        [SetUp]
        public void SetUp()
        {
            Serializer.ConfigureRanges(
                positionRange: 500f,
                scaleMin: -1f,
                scaleMax: 10f,
                deltaPositionRange: DeltaPositionRange,
                deltaRotationComponentRange: 0.5f,
                deltaSingleAxisAngleRange: DeltaSingleAxisAngleRange,
                deltaScaleRange: 1f);
        }

        // ---- Selection matrix -----------------------------------------------------------------

        [Test]
        public void NothingChanged_WritesNothing()
        {
            TransformEncodeResult result = Encode(d =>
            {
                d.PositionChanged = false;
                d.RotationChanged = false;
                d.ScaleChanged = false;
                return d;
            }, out _);

            Assert.IsFalse(result.Wrote);
            Assert.AreEqual(0, result.BytesWritten);
        }

        [Test]
        public void PositionOnly_SmallMove_PicksDeltaPosition()
        {
            Encode(d =>
            {
                d.PositionChanged = true;
                d.currentPosition = d.previousPosition + new Vector3(0.1f, 0.1f, 0.1f);
                return d;
            }, out byte eventType);

            Assert.AreEqual((byte)Serializer.EventType.DeltaPosition, eventType);
        }

        [Test]
        public void AllThreeChanged_SmallMove_PicksDeltaTransform()
        {
            Encode(d =>
            {
                d.PositionChanged = true;
                d.RotationChanged = true;
                d.ScaleChanged = true;
                d.currentPosition = d.previousPosition + new Vector3(0.1f, 0.1f, 0.1f);
                d.currentRotation = d.previousRotation * Quaternion.Euler(1f, 2f, 3f);
                d.currentScale = d.previousScale + new Vector3(0.05f, 0.05f, 0.05f);
                return d;
            }, out byte eventType);

            Assert.AreEqual((byte)Serializer.EventType.DeltaTransform, eventType);
        }

        [Test]
        public void SendTrue_AlwaysPicksAnAbsoluteEvent()
        {
            // Uniform scale, so the smaller absolute variant is the correct choice.
            Encode(d =>
            {
                d.SendTrue = true;
                d.currentScale = Vector3.one;
                return d;
            }, out byte eventType);

            Assert.AreEqual((byte)Serializer.EventType.TruePositionRotationUniformScale, eventType);
        }

        [Test]
        public void SendTrue_NonUniformScale_PicksTrueTransform()
        {
            Encode(d =>
            {
                d.SendTrue = true;
                d.currentScale = new Vector3(1f, 2f, 3f);
                return d;
            }, out byte eventType);

            Assert.AreEqual((byte)Serializer.EventType.TrueTransform, eventType);
        }

        // ---- Rotation ladder ------------------------------------------------------------------

        [Test]
        public void CardinalRotation_WithinSingleAxisRange_PicksTheTwoByteEncoding()
        {
            Encode(d =>
            {
                d.RotationChanged = true;
                d.currentRotation = d.previousRotation * Quaternion.AngleAxis(1f, Vector3.up);
                return d;
            }, out byte eventType);

            Assert.AreEqual((byte)Serializer.EventType.DeltaRotationSingleAxis, eventType);
        }

        [Test]
        public void CardinalRotation_BeyondSingleAxisRange_WidensToDropW()
        {
            // 5 degrees per tick is 200 deg/s at 40Hz -- past the 2-degree single-axis range, but
            // comfortably inside drop-W. This is the spinning-pickup case: before promotion
            // existed it was silently recorded at the range ceiling.
            TransformEncodeResult result = Encode(d =>
            {
                d.RotationChanged = true;
                d.currentRotation = d.previousRotation * Quaternion.AngleAxis(5f, Vector3.up);
                return d;
            }, out byte eventType);

            Assert.AreEqual((byte)Serializer.EventType.DeltaRotation, eventType,
                "should fall to the 3-byte drop-W form, not clamp the single-axis angle");
            Assert.IsTrue(result.RotationWidened, "widening should be reported");
            Assert.AreEqual(0, result.PromotedMask, "drop-W still fits, so this is not a promotion");
        }

        [Test]
        public void HugeRotation_BeyondDropW_PromotesToAbsolute()
        {
            // Drop-W stores x/y/z over +/-0.5, and for a rotation of theta about a unit axis each
            // component is sin(theta/2) * axisComponent. About (1,1,1)/sqrt(3) that reaches exactly
            // 0.5 at 120 degrees -- which still fits, since the range is inclusive. 150 degrees
            // gives 0.5577 and genuinely overflows, so absolute is the only correct encoding.
            TransformEncodeResult result = Encode(d =>
            {
                d.RotationChanged = true;
                d.currentRotation = d.previousRotation * Quaternion.AngleAxis(150f, new Vector3(1f, 1f, 1f).normalized);
                return d;
            }, out byte eventType);

            Assert.AreEqual((byte)Serializer.EventType.TrueRotation, eventType);
            Assert.AreNotEqual(0, result.PromotedMask & SampledTransform.RotationChangedBit);
        }

        [Test]
        public void RotationExactlyAtTheDropWBoundary_StillUsesTheDelta()
        {
            // Pins the inclusive edge. 120 degrees about (1,1,1) produces components of exactly
            // 0.5, so it must encode as a delta rather than being treated as overflow -- otherwise
            // the promotion threshold is off by one representable value.
            TransformEncodeResult result = Encode(d =>
            {
                d.RotationChanged = true;
                d.currentRotation = d.previousRotation * Quaternion.AngleAxis(120f, new Vector3(1f, 1f, 1f).normalized);
                return d;
            }, out byte eventType);

            Assert.AreEqual((byte)Serializer.EventType.DeltaRotation, eventType);
            Assert.AreEqual(0, result.PromotedMask);
        }

        // ---- Position / scale promotion -------------------------------------------------------

        [Test]
        public void PositionBeyondDeltaRange_PromotesToAbsolute()
        {
            float tooFar = DeltaPositionRange * 3f;

            TransformEncodeResult result = Encode(d =>
            {
                d.PositionChanged = true;
                d.currentPosition = d.previousPosition + new Vector3(tooFar, 0f, 0f);
                return d;
            }, out byte eventType);

            Assert.AreEqual((byte)Serializer.EventType.TruePosition, eventType,
                "a delta that cannot be represented must become absolute, not be clamped");
            Assert.AreNotEqual(0, result.PromotedMask & SampledTransform.PositionChangedBit);
        }

        [Test]
        public void PromotedPosition_ReconstructsTheRealValue()
        {
            // The point of promoting: the receiver ends up where the object actually is.
            float tooFar = DeltaPositionRange * 5f;
            var start = new Vector3(10f, 0f, 0f);
            Vector3 target = start + new Vector3(tooFar, 0f, 0f);

            TransformEncodeResult result = Encode(d =>
            {
                d.previousPosition = start;
                d.PositionChanged = true;
                d.currentPosition = target;
                return d;
            }, out _);

            float positionStep = (500f * 2f) / 65535f;
            Assert.AreEqual(target.x, result.Position.x, positionStep * 2f,
                "a clamped delta would have landed short of the real position");
        }

        [Test]
        public void PositionAndRotationBothOverflow_PromotesTheWholeEvent()
        {
            // Whole-event promotion rather than splitting into a narrow delta plus a separate
            // absolute: fewer bytes and far simpler to reason about.
            Encode(d =>
            {
                d.PositionChanged = true;
                d.RotationChanged = true;
                d.currentPosition = d.previousPosition + new Vector3(DeltaPositionRange * 3f, 0f, 0f);
                d.currentRotation = d.previousRotation * Quaternion.AngleAxis(150f, Vector3.right);
                return d;
            }, out byte eventType);

            Assert.AreEqual((byte)Serializer.EventType.TruePositionRotation, eventType);
        }

        [Test]
        public void PositionOutsideAbsoluteRange_ReportsHardClamp()
        {
            // Nothing is wider than True*, so this is unfixable by promotion and has to be
            // surfaced as a configuration problem instead of absorbed.
            TransformEncodeResult result = Encode(d =>
            {
                d.PositionChanged = true;
                d.previousPosition = new Vector3(499f, 0f, 0f);
                d.currentPosition = new Vector3(5000f, 0f, 0f);
                return d;
            }, out _);

            Assert.IsTrue(result.HardClamped);
        }

        // ---- Helpers --------------------------------------------------------------------------

        private delegate TransformData Mutate(TransformData data);

        private static TransformEncodeResult Encode(Mutate mutate, out byte eventType)
        {
            var data = new TransformData
            {
                ObjectId = 1,
                SendTrue = false,
                PositionChanged = false,
                RotationChanged = false,
                ScaleChanged = false,
                currentPosition = Vector3.zero,
                currentRotation = Quaternion.identity,
                currentScale = Vector3.one,
                previousPosition = Vector3.zero,
                previousRotation = Quaternion.identity,
                previousScale = Vector3.one,
            };

            data = mutate(data);

            var scratch = new byte[Serializer.MaxEventSize];
            TransformEncodeResult result = TransformEventDispatcher.SerializeBestFitEvent(data.ObjectId, in data, scratch);
            eventType = result.Wrote ? scratch[0] : (byte)0;
            return result;
        }
    }
}
