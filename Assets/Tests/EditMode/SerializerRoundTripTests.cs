using NUnit.Framework;
using UnityEngine;

namespace MoodyBlues.Tests
{
    /// <summary>
    /// Encoder/decoder agreement, split into two kinds of assertion on purpose.
    ///
    /// Events with no quantized payload get exact-byte assertions -- those bytes are the wire
    /// contract a backend decoder has to match, and nothing about them is approximate.
    ///
    /// Quantized events get property assertions (round-trip within the documented bound, correct
    /// endpoints, correct event selection) rather than hardcoded bytes. Pinning exact bytes for
    /// those means pinning a specific rounding mode too, which is better captured by the generated
    /// golden corpus than by numbers written out by hand.
    /// </summary>
    [TestFixture]
    public sealed class SerializerRoundTripTests
    {
        private const float PositionRange = 500f;
        private const float ScaleMin = -1f;
        private const float ScaleMax = 10f;

        /// <summary>16 bits spread over a 1000-unit span, so one step is ~0.0153 units.</summary>
        private const float PositionStep = (PositionRange * 2f) / 65535f;

        [SetUp]
        public void SetUp()
        {
            Serializer.ConfigureRanges(
                positionRange: PositionRange,
                scaleMin: ScaleMin,
                scaleMax: ScaleMax,
                deltaPositionRange: 1.5f,
                deltaRotationComponentRange: 0.5f,
                deltaSingleAxisAngleRange: 2f,
                deltaScaleRange: 1f);
        }

        // ---- Exact wire bytes -----------------------------------------------------------------

        [Test]
        public void KeepAlive_IsASingleTypeByte()
        {
            var buffer = new byte[Serializer.KeepAliveSize];
            int written = Serializer.SerializeKeepAlive(buffer);

            Assert.AreEqual(1, written);
            Assert.AreEqual(new byte[] { 30 }, buffer);
        }

        [Test]
        public void EnvelopeOnlyEvents_WriteTypeThenLittleEndianObjectId()
        {
            var buffer = new byte[Serializer.ShowObjectSize];

            Serializer.SerializeShowObject(0x1234, buffer);
            Assert.AreEqual(new byte[] { 25, 0x34, 0x12 }, buffer, "ShowObject");

            Serializer.SerializeHideObject(0x1234, buffer);
            Assert.AreEqual(new byte[] { 26, 0x34, 0x12 }, buffer, "HideObject");

            Serializer.SerializeDeleteObject(0x1234, buffer);
            Assert.AreEqual(new byte[] { 28, 0x34, 0x12 }, buffer, "DeleteObject");
        }

        [Test]
        public void TimeStamp_WritesTypeThenIeee754Double()
        {
            var buffer = new byte[Serializer.TimeStampSize];
            Serializer.SerializeTimeStamp(1.0, buffer);

            Assert.AreEqual(9, Serializer.TimeStampSize);
            Assert.AreEqual(24, buffer[0]);

            // 1.0 as a little-endian IEEE-754 double.
            Assert.AreEqual(
                new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F },
                new[] { buffer[1], buffer[2], buffer[3], buffer[4], buffer[5], buffer[6], buffer[7], buffer[8] });
        }

        [Test]
        public void BeginSession_StartsWithTypeThenProtocolVersion()
        {
            var buffer = new byte[Serializer.BeginSessionSize];
            int written = Serializer.SerializeBeginSession(40f, buffer);

            Assert.AreEqual(Serializer.BeginSessionSize, written);
            Assert.AreEqual(29, buffer[0], "event type");

            ushort version = (ushort)(buffer[1] | (buffer[2] << 8));
            Assert.AreEqual(Serializer.ProtocolVersion, version);
        }

        [Test]
        public void BeginSession_IsTheLargestEvent()
        {
            // MaxEventSize bounds every stackalloc scratch buffer and the ring buffer's
            // single-event reservation, so it has to actually be the maximum.
            Assert.AreEqual(Serializer.BeginSessionSize, Serializer.MaxEventSize);
            Assert.GreaterOrEqual(Serializer.MaxEventSize, Serializer.InstantiateObjectSize);
            Assert.GreaterOrEqual(Serializer.MaxEventSize, Serializer.ReparentObjectSize);
            Assert.GreaterOrEqual(Serializer.MaxEventSize, Serializer.TrueTransformSize);
            Assert.GreaterOrEqual(Serializer.MaxEventSize, Serializer.EndSessionSize);
            Assert.GreaterOrEqual(Serializer.MaxEventSize, Serializer.TimeStampSize);
        }

        // ---- Quantized round-trips ------------------------------------------------------------

        [Test]
        public void TruePosition_RoundTripsWithinOneQuantizationStep()
        {
            var cases = new[]
            {
                Vector3.zero,
                new Vector3(1f, 2f, 3f),
                new Vector3(-123.456f, 78.9f, -0.001f),
                new Vector3(PositionRange, -PositionRange, 0f), // exact endpoints
            };

            var buffer = new byte[Serializer.MaxEventSize];

            foreach (Vector3 source in cases)
            {
                int size = Serializer.SerializeTruePosition(7, source, buffer);

                bool applied = Serializer.TryApplyTransformEvent(
                    new System.ReadOnlySpan<byte>(buffer, 0, size),
                    Vector3.zero, Quaternion.identity, Vector3.one,
                    out Vector3 decoded, out _, out _);

                Assert.IsTrue(applied, $"{source} should decode as a transform event");
                AssertWithin(source, decoded, PositionStep, nameof(TruePosition_RoundTripsWithinOneQuantizationStep));
            }
        }

        [Test]
        public void TrueTransform_RoundTripsAllThreeProperties()
        {
            var position = new Vector3(12.25f, -4.5f, 99.125f);
            Quaternion rotation = Quaternion.Euler(30f, 45f, 60f);
            var scale = new Vector3(2f, 2f, 2f);

            var buffer = new byte[Serializer.MaxEventSize];
            int size = Serializer.SerializeTrueTransform(9, position, rotation, scale, buffer);

            Serializer.TryApplyTransformEvent(
                new System.ReadOnlySpan<byte>(buffer, 0, size),
                Vector3.zero, Quaternion.identity, Vector3.one,
                out Vector3 decodedPosition, out Quaternion decodedRotation, out Vector3 decodedScale);

            AssertWithin(position, decodedPosition, PositionStep, "position");

            float scaleStep = (ScaleMax - ScaleMin) / 65535f;
            AssertWithin(scale, decodedScale, scaleStep, "scale");

            // Smallest-three stores 3 components at 10 bits each; well under a degree in practice.
            Assert.Less(Quaternion.Angle(rotation, decodedRotation), 0.5f, "rotation");
        }

        [Test]
        public void ReparentObject_CarriesParentIdAndReBaselinesTheTransform()
        {
            var localPosition = new Vector3(5f, 0.5f, -2f);
            Quaternion localRotation = Quaternion.AngleAxis(90f, Vector3.up);
            var localScale = new Vector3(1f, 1f, 1f);

            var buffer = new byte[Serializer.MaxEventSize];
            int size = Serializer.SerializeReparentObject(11, 42, localPosition, localRotation, localScale, buffer);

            Assert.AreEqual(Serializer.ReparentObjectSize, size);
            Assert.AreEqual(32, buffer[0], "event type");
            Assert.AreEqual(42, Serializer.DeserializeReparentParentObjectId(buffer), "new parent id");

            // Deliberately pass a baseline nothing like the real values: a reparent is absolute,
            // so the decoded state must come entirely from the payload.
            bool applied = Serializer.TryApplyTransformEvent(
                new System.ReadOnlySpan<byte>(buffer, 0, size),
                new Vector3(-400f, -400f, -400f), Quaternion.AngleAxis(180f, Vector3.right), new Vector3(9f, 9f, 9f),
                out Vector3 decodedPosition, out Quaternion decodedRotation, out Vector3 decodedScale);

            Assert.IsTrue(applied, "ReparentObject must re-baseline, or reparented objects drift");
            AssertWithin(localPosition, decodedPosition, PositionStep, "position");
            AssertWithin(localScale, decodedScale, (ScaleMax - ScaleMin) / 65535f, "scale");
            Assert.Less(Quaternion.Angle(localRotation, decodedRotation), 0.5f, "rotation");
        }

        [Test]
        public void NonTransformEvents_AreNotAppliedAsTransforms()
        {
            var buffer = new byte[Serializer.MaxEventSize];
            int size = Serializer.SerializeShowObject(3, buffer);

            bool applied = Serializer.TryApplyTransformEvent(
                new System.ReadOnlySpan<byte>(buffer, 0, size),
                Vector3.one, Quaternion.identity, Vector3.one,
                out _, out _, out _);

            Assert.IsFalse(applied, "ShowObject carries no transform state");
        }

        [Test]
        public void ConfigureRanges_IsReflectedInCurrentRanges()
        {
            Serializer.ConfigureRanges(100f, 0f, 5f, 0.25f, 0.4f, 1f, 0.5f);
            var ranges = Serializer.CurrentRanges;

            Assert.AreEqual(100f, ranges.PositionRange);
            Assert.AreEqual(0f, ranges.ScaleMin);
            Assert.AreEqual(5f, ranges.ScaleMax);
            Assert.AreEqual(0.25f, ranges.DeltaPositionRange);
            Assert.AreEqual(0.4f, ranges.DeltaRotationComponentRange);
            Assert.AreEqual(1f, ranges.DeltaSingleAxisAngleRange);
            Assert.AreEqual(0.5f, ranges.DeltaScaleRange);
        }

        private static void AssertWithin(Vector3 expected, Vector3 actual, float tolerance, string what)
        {
            Assert.AreEqual(expected.x, actual.x, tolerance, $"{what}.x");
            Assert.AreEqual(expected.y, actual.y, tolerance, $"{what}.y");
            Assert.AreEqual(expected.z, actual.z, tolerance, $"{what}.z");
        }
    }
}
