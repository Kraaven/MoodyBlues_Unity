using System;
using NUnit.Framework;
using UnityEngine;

namespace MoodyBlues.Tests
{
    /// <summary>
    /// Protocol v4: symmetric quantization for signed fields (zero is exact), the change gate
    /// ("does the encoding see it") and the decode formula the sender baselines on.
    /// </summary>
    [TestFixture]
    public sealed class SymmetricQuantizationTests
    {
        private const float DeltaPositionRange = 1.5f;
        private const float DeltaRotationRange = 0.25f;
        private const float DeltaSingleAxisRange = 2f;
        private const float DeltaScaleRange = 1f;

        [SetUp]
        public void SetUp()
        {
            Serializer.ConfigureRanges(500f, -1f, 10f, DeltaPositionRange, DeltaRotationRange,
                DeltaSingleAxisRange, DeltaScaleRange);
        }

        // ---- Decode formula -------------------------------------------------------------------

        [Test]
        public void DecodeFormula_IsQTimesRangeOverLevels_ForEveryCode()
        {
            foreach ((int bits, float range) in new[] { (8, 0.25f), (10, 1.5f), (11, 1.5f), (14, 2f), (16, 500f) })
            {
                int levels = (1 << (bits - 1)) - 1;
                for (uint u = 0; u <= (uint)(2 * levels); u++)
                {
                    float step = (float)(range / (float)levels); // normative form (Spec 9)
                    float expected = (float)((float)((int)u - levels) * step);
                    Assert.AreEqual(expected, Quantization.DequantizeSigned(u, range, bits), $"{bits} bits, u={u}");
                }
            }
        }

        [Test]
        public void InvalidCode_AllOnes_DecodesAsInvalid()
        {
            foreach (int bits in new[] { 8, 10, 11, 14, 16 })
            {
                uint invalid = (1u << bits) - 1u;
                Assert.IsTrue(float.IsNaN(Quantization.DequantizeSigned(invalid, 1f, bits)), $"{bits} bits");
            }
        }

        [Test]
        public void Encode_NeverEmitsTheInvalidCode_AndSaturatesAtPlusMinusL()
        {
            foreach (int bits in new[] { 8, 10, 11, 14, 16 })
            {
                uint max = Quantization.QuantizeSigned(1e9f, 1f, bits);
                uint min = Quantization.QuantizeSigned(-1e9f, 1f, bits);
                int levels = Quantization.SignedLevels(bits);
                Assert.AreEqual((uint)(2 * levels), max, $"{bits} bits +");
                Assert.AreEqual(0u, min, $"{bits} bits -");
                Assert.Less(max, (1u << bits) - 1u);
            }
        }

        [Test]
        public void DeltaPositionEvent_WithInvalidCode_IsRejected()
        {
            var buffer = new byte[Serializer.MaxEventSize];
            int size = Serializer.SerializeDeltaPosition(1, Vector3.zero, buffer);

            // Y is the 10-bit field at bits 11..20 of the little-endian uint32 after the envelope.
            uint packed = BitConverter.ToUInt32(buffer, 3);
            packed |= 0x3FFu << 11;
            BitConverter.GetBytes(packed).CopyTo(buffer, 3);

            bool applied = Serializer.TryApplyTransformEvent(
                new ReadOnlySpan<byte>(buffer, 0, size), Vector3.zero, Quaternion.identity, Vector3.one,
                out _, out _, out _);
            Assert.IsFalse(applied);
        }

        // ---- Zero is exact --------------------------------------------------------------------

        [Test]
        public void Zero_RoundTripsExactly_ForEverySignedEncoding()
        {
            var buffer = new byte[Serializer.MaxEventSize];
            Vector3 p0 = new Vector3(3.25f, -7.5f, 100f);
            Quaternion r0 = Quaternion.Euler(10f, 20f, 30f);
            Vector3 s0 = new Vector3(2f, 3f, 4f);

            // Delta position (11/10/11)
            int size = Serializer.SerializeDeltaPosition(1, Vector3.zero, buffer);
            Apply(buffer, size, p0, r0, s0, out Vector3 p, out Quaternion r, out Vector3 s);
            Assert.AreEqual(p0, p, "delta position");

            // Delta rotation drop-W: decoded delta is exactly the identity.
            size = Serializer.SerializeDeltaRotation(1, Quaternion.identity, buffer);
            Apply(buffer, size, p0, Quaternion.identity, s0, out _, out r, out _);
            Assert.AreEqual(Quaternion.identity, r, "delta rotation");

            // Delta single axis
            size = Serializer.SerializeDeltaRotationSingleAxis(1, 1, 0f, buffer);
            Apply(buffer, size, p0, Quaternion.identity, s0, out _, out r, out _);
            Assert.AreEqual(Quaternion.identity, r, "delta single axis");

            // Delta scale and delta uniform scale
            size = Serializer.SerializeDeltaScale(1, Vector3.zero, buffer);
            Apply(buffer, size, p0, r0, s0, out _, out _, out s);
            Assert.AreEqual(s0, s, "delta scale");
            size = Serializer.SerializeDeltaUniformScale(1, 0f, buffer);
            Apply(buffer, size, p0, r0, s0, out _, out _, out s);
            Assert.AreEqual(s0, s, "delta uniform scale");

            // Absolute position at zero
            size = Serializer.SerializeTruePosition(1, Vector3.zero, buffer);
            Apply(buffer, size, p0, r0, s0, out p, out _, out _);
            Assert.AreEqual(Vector3.zero, p, "true position");

            // Smallest-three: identity has three exactly-zero components.
            size = Serializer.SerializeTrueRotation(1, Quaternion.identity, buffer);
            Apply(buffer, size, p0, r0, s0, out _, out r, out _);
            Assert.AreEqual(Quaternion.identity, r, "true rotation");
        }

        // ---- Change gate ----------------------------------------------------------------------

        [Test]
        public void Gate_BelowHalfAStep_IsUnchanged_AboveIsChanged()
        {
            DeltaChangeGate gate = Serializer.ChangeGate;
            float stepY = DeltaPositionRange / 511f;
            float stepXZ = DeltaPositionRange / 1023f;
            Vector3 origin = Vector3.zero;

            Assert.IsFalse(gate.PositionChanged(new Vector3(0.49f * stepXZ, 0.49f * stepY, -0.49f * stepXZ), origin));
            Assert.IsTrue(gate.PositionChanged(new Vector3(0.51f * stepXZ, 0f, 0f), origin));
            Assert.IsTrue(gate.PositionChanged(new Vector3(0f, -0.51f * stepY, 0f), origin));
            Assert.IsTrue(gate.PositionChanged(new Vector3(100f, 0f, 0f), origin), "out of range is a change");

            float scaleStep = DeltaScaleRange / 127f;
            Assert.IsFalse(gate.ScaleChanged(Vector3.one * (1f + 0.49f * scaleStep), Vector3.one));
            Assert.IsTrue(gate.ScaleChanged(Vector3.one * (1f + 0.51f * scaleStep), Vector3.one));
            Assert.IsTrue(gate.ScaleChanged(Vector3.one * 5f, Vector3.one), "out of range is a change");
        }

        [Test]
        public void Gate_Rotation_SeesQuaternionSignFlipAsNoChange()
        {
            Quaternion q = Quaternion.Euler(10f, 20f, 30f);
            var negated = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            Assert.IsFalse(Serializer.ChangeGate.RotationChanged(negated, q));
            Assert.IsFalse(Serializer.ChangeGate.RotationChanged(q, q));
        }

        [Test]
        public void StationaryObject_EmitsNothing_AfterItsLastRealChange()
        {
            Vector3 truePosition = new Vector3(1.5f, 2.5f, -3.5f);
            Quaternion trueRotation = Quaternion.identity;
            Vector3 trueScale = Vector3.one;

            Vector3 basePosition = truePosition;
            Quaternion baseRotation = trueRotation;
            Vector3 baseScale = trueScale;

            // Move for a while in every property (rotation about a tilted axis, so it is drop-W).
            Quaternion step = Quaternion.AngleAxis(1.1f, new Vector3(1f, 2f, 3f).normalized);
            for (int tick = 0; tick < 200; tick++)
            {
                truePosition += new Vector3(0.0137f, -0.0041f, 0.0083f);
                trueRotation *= step;
                trueScale += new Vector3(0.003f, 0.001f, 0.002f);
                Tick(truePosition, trueRotation, trueScale, ref basePosition, ref baseRotation, ref baseScale, out _);
            }

            // Stop. Strict silence from the first tick after the last real change: the residue
            // between the decoded baseline and the truth is at most half a step, so every code is zero.
            for (int tick = 0; tick < 500; tick++)
            {
                Tick(truePosition, trueRotation, trueScale, ref basePosition, ref baseRotation, ref baseScale, out bool emitted);
                Assert.IsFalse(emitted, $"tick {tick} after stopping still emitted an event");
            }
        }

        [Test]
        public void ObjectStillAfterAnAbsoluteEvent_RefinesThenGoesSilent()
        {
            Vector3 p = new Vector3(4.2f, 0.1f, -9.9f);
            Quaternion r = Quaternion.Euler(33f, 66f, 99f);
            Vector3 s = new Vector3(1.5f, 2f, 0.5f);

            // Seed the baseline the way the client does: an absolute event, decoded. Absolute
            // steps are coarser than the delta gate, so up to 3 small delta events may refine it
            // (measured: 3), after which the object is strictly silent.
            var data = new TransformData
            {
                ObjectId = 1, SendTrue = true,
                currentPosition = p, currentRotation = r, currentScale = s,
                previousPosition = p, previousRotation = r, previousScale = s,
            };
            var scratch = new byte[Serializer.MaxEventSize];
            TransformEncodeResult seed = TransformEventDispatcher.SerializeBestFitEvent(1, in data, scratch);
            Vector3 bp = seed.Position; Quaternion br = seed.Rotation; Vector3 bs = seed.Scale;

            const int SettleTicks = 3;
            for (int tick = 0; tick < 200; tick++)
            {
                Tick(p, r, s, ref bp, ref br, ref bs, out bool emitted);
                if (tick >= SettleTicks) Assert.IsFalse(emitted, $"tick {tick}");
            }
        }

        [Test]
        public void SlowDrift_BelowHalfAStep_AccumulatesThenEmitsOneStep()
        {
            Vector3 basePosition = Vector3.zero;
            Quaternion baseRotation = Quaternion.identity;
            Vector3 baseScale = Vector3.one;
            Vector3 truePosition = Vector3.zero;

            float stepXZ = DeltaPositionRange / 1023f;
            float perTick = 0.2f * stepXZ; // 0.2 step per tick: crosses half a step on tick 3
            int emissions = 0;
            for (int tick = 0; tick < 50; tick++)
            {
                truePosition.x += perTick;
                Tick(truePosition, Quaternion.identity, Vector3.one, ref basePosition, ref baseRotation, ref baseScale, out bool emitted);
                if (emitted) emissions++;
                Assert.LessOrEqual(Mathf.Abs(truePosition.x - basePosition.x), 0.5f * stepXZ * 1.001f, $"tick {tick}: error exceeds half a step");
            }

            Assert.AreEqual(10, emissions, "50 ticks * 0.2 step = 10 steps, one per step crossed");
        }

        // ---- Helpers --------------------------------------------------------------------------

        /// <summary>One capture tick: gate, encode, re-baseline on the decoded result. emitted = any bytes written.</summary>
        private static void Tick(Vector3 p, Quaternion r, Vector3 s,
            ref Vector3 basePosition, ref Quaternion baseRotation, ref Vector3 baseScale, out bool emitted)
        {
            DeltaChangeGate gate = Serializer.ChangeGate;
            var data = new TransformData
            {
                ObjectId = 1,
                SendTrue = false,
                PositionChanged = gate.PositionChanged(p, basePosition),
                RotationChanged = gate.RotationChanged(r, baseRotation),
                ScaleChanged = gate.ScaleChanged(s, baseScale),
                currentPosition = p,
                currentRotation = r,
                currentScale = s,
                previousPosition = basePosition,
                previousRotation = baseRotation,
                previousScale = baseScale,
            };

            emitted = false;
            if (!data.PositionChanged && !data.RotationChanged && !data.ScaleChanged) return;

            var scratch = new byte[Serializer.MaxEventSize];
            TransformEncodeResult result = TransformEventDispatcher.SerializeBestFitEvent(1, in data, scratch);
            emitted = result.Wrote;
            basePosition = result.Position;
            baseRotation = result.Rotation;
            baseScale = result.Scale;
        }

        private static void Apply(byte[] buffer, int size, Vector3 p0, Quaternion r0, Vector3 s0,
            out Vector3 p, out Quaternion r, out Vector3 s)
        {
            bool ok = Serializer.TryApplyTransformEvent(new ReadOnlySpan<byte>(buffer, 0, size), p0, r0, s0, out p, out r, out s);
            Assert.IsTrue(ok);
        }
    }
}
