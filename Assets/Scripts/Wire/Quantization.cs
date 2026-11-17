using UnityEngine;

namespace MoodyBlues
{
    /// <summary>
    /// Symmetric quantizer for every signed (+/-R) wire field (Spec.md Section 9).
    ///
    /// An n-bit field has L = 2^(n-1) - 1 levels each side of zero and step = R / L. The code is
    /// stored unsigned as u = q + L, so u is in [0, 2L], zero is exactly representable, and
    /// u = 2^n - 1 is never emitted (a decoder treats it as invalid).
    ///
    /// Burst-safe: plain float math, no managed state.
    /// </summary>
    public static class Quantization
    {
        public static int SignedLevels(int bits) => (1 << (bits - 1)) - 1;

        /// <summary>
        /// Step of an n-bit signed field over +/-range. The explicit casts force float32 rounding
        /// of the quotient, so no runtime may keep an extended-precision intermediate.
        /// </summary>
        public static float SignedStep(float range, int levels) => (float)(range / (float)levels);

        /// <summary>The signed code q = clamp(round(v / step), -L, +L).</summary>
        public static int SignedCode(float value, float step, int levels)
        {
            float scaled = Mathf.Clamp(value / step, -(float)levels, (float)levels);
            return Mathf.RoundToInt(scaled);
        }

        public static uint QuantizeSigned(float value, float range, int bits)
        {
            int levels = SignedLevels(bits);
            float step = SignedStep(range, levels);
            return (uint)(SignedCode(value, step, levels) + levels);
        }

        /// <summary>
        /// v = (float)((float)q * step), step = (float)(R / (float)L). Returns NaN for the invalid code u = 2^n - 1, which
        /// <see cref="Serializer.TryApplyTransformEvent"/> turns into a rejected event.
        /// </summary>
        public static float DequantizeSigned(uint code, float range, int bits)
        {
            int levels = SignedLevels(bits);
            if (code > (uint)(2 * levels)) return float.NaN;
            float step = SignedStep(range, levels);
            int q = (int)code - levels;
            return (float)((float)q * step);
        }
    }

    /// <summary>
    /// "Does the encoding see it": a property counts as changed only if its delta against the
    /// last-sent (decoded) baseline quantizes to a non-zero code in the encoding that would carry
    /// it (Spec.md Section 9.1). Sub-half-step motion therefore accumulates against the unchanged
    /// baseline until it crosses half a step, so a stopped object emits nothing.
    ///
    /// A plain struct of steps so <see cref="TransformDiffJob"/> can carry it as a field and
    /// Burst-compile the checks. Build it with <see cref="Serializer.ChangeGate"/>.
    /// </summary>
    public readonly struct DeltaChangeGate
    {
        public const int PositionLevelsXZ = 1023; // 11 bits
        public const int PositionLevelsY = 511;   // 10 bits
        public const int RotationLevels = 127;    // 8 bits
                public const int ScaleLevels = 127;       // 8 bits

        private readonly float _positionStepXZ;
        private readonly float _positionStepY;
        private readonly float _rotationStep;
        private readonly float _scaleStep;

        public DeltaChangeGate(float deltaPositionRange, float deltaRotationComponentRange, float deltaScaleRange)
        {
            _positionStepXZ = Quantization.SignedStep(deltaPositionRange, PositionLevelsXZ);
            _positionStepY = Quantization.SignedStep(deltaPositionRange, PositionLevelsY);
            _rotationStep = Quantization.SignedStep(deltaRotationComponentRange, RotationLevels);
            _scaleStep = Quantization.SignedStep(deltaScaleRange, ScaleLevels);
        }

        public bool PositionChanged(Vector3 current, Vector3 last)
        {
            Vector3 d = current - last;
            return Quantization.SignedCode(d.x, _positionStepXZ, PositionLevelsXZ) != 0
                || Quantization.SignedCode(d.y, _positionStepY, PositionLevelsY) != 0
                || Quantization.SignedCode(d.z, _positionStepXZ, PositionLevelsXZ) != 0;
        }

        public bool ScaleChanged(Vector3 current, Vector3 last)
        {
            Vector3 d = current - last;
            return Quantization.SignedCode(d.x, _scaleStep, ScaleLevels) != 0
                || Quantization.SignedCode(d.y, _scaleStep, ScaleLevels) != 0
                || Quantization.SignedCode(d.z, _scaleStep, ScaleLevels) != 0;
        }

        public bool RotationChanged(Quaternion current, Quaternion last)
        {
            Quaternion d = RelativeRotation(last, current);
            return Quantization.SignedCode(d.x, _rotationStep, RotationLevels) != 0
                || Quantization.SignedCode(d.y, _rotationStep, RotationLevels) != 0
                || Quantization.SignedCode(d.z, _rotationStep, RotationLevels) != 0;
        }

        /// <summary>
        /// inverse(last) * current with the hemisphere forced to w >= 0, the exact delta the
        /// rotation encodings carry. Inverse is written out (conjugate / |q|^2) so this stays
        /// Burst-compilable and identical wherever it is called.
        /// </summary>
        public static Quaternion RelativeRotation(Quaternion last, Quaternion current)
        {
            float norm = last.x * last.x + last.y * last.y + last.z * last.z + last.w * last.w;
            float inv = norm > 0f ? 1f / norm : 0f;
            var inverse = new Quaternion(-last.x * inv, -last.y * inv, -last.z * inv, last.w * inv);
            Quaternion d = inverse * current;
            return d.w < 0f ? new Quaternion(-d.x, -d.y, -d.z, -d.w) : d;
        }

        /// <summary>
        /// Rotation error between two rotations as sin(angle / 2): the length of the vector part of
        /// inverse(a) * b with w >= 0. Monotonic in the angle and accurate near zero, unlike
        /// Quaternion.Angle, which returns exactly 0 for anything under about 0.16 degrees.
        /// </summary>
        public static float RotationError(Quaternion a, Quaternion b)
        {
            Quaternion d = RelativeRotation(a, b);
            return Mathf.Sqrt(d.x * d.x + d.y * d.y + d.z * d.z);
        }

        /// <summary>
        /// Signed angle in degrees, 2 * atan2(component, w), about the axis (0=X, 1=Y, 2=Z) with
        /// the largest |component| of a delta with w >= 0. 0 for the identity.
        /// </summary>
        public static float SingleAxisAngleDegrees(Quaternion delta, out int axis)
        {
            float ax = Mathf.Abs(delta.x), ay = Mathf.Abs(delta.y), az = Mathf.Abs(delta.z);
            float component;
            if (ax >= ay && ax >= az) { axis = 0; component = delta.x; }
            else if (ay >= az) { axis = 1; component = delta.y; }
            else { axis = 2; component = delta.z; }
            return 2f * Mathf.Atan2(component, delta.w) * Mathf.Rad2Deg;
        }
    }
}
