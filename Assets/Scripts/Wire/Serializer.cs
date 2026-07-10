using System;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Non-allocating (de)serializers for the wire events described in Spec.md. Multi-byte values
/// are written in the host's native byte order, which the static constructor below asserts is
/// little-endian (true for every currently-supported Unity build target).
/// </summary>
public static partial class Serializer
{
    static Serializer()
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException(
                "Serializer packs multi-byte values using the host's native byte order and assumes " +
                "little-endian (see Spec.md). This platform is big-endian, which would silently " +
                "produce a wire-incompatible stream.");
        }
    }

    public static float RemapRange(float x, float fromMin, float fromMax, float toMin, float toMax)
    {
        if (Mathf.Approximately(fromMax, fromMin)) return toMin;

        float normalized = (x - fromMin) / (fromMax - fromMin);
        return normalized * (toMax - toMin) + toMin;
    }

    // -------- 4-byte XYZ BitPacking: X(11) Y(10) Z(11) --------
    public static void SerializeBitPackedXYZToBuffer(int x, int y, int z, Span<byte> destination)
    {
        UInt32 packed = 0;
        packed |= (uint)(x & 0x7FF);
        packed |= (uint)(y & 0x3FF) << 11;
        packed |= (uint)(z & 0x7FF) << 21;

        SerializeUInt32ToBuffer(packed, destination);
    }

    public static void SerializeUInt32ToBuffer(UInt32 value, Span<byte> destination)
    {
        MemoryMarshal.Cast<byte, UInt32>(destination)[0] = value;
    }

    public static UInt32 DeserializeUInt32(ReadOnlySpan<byte> source)
    {
        return MemoryMarshal.Cast<byte, UInt32>(source)[0];
    }

    // Event catalog (IDs, payload layout, sizes, ranges) is documented in Spec.md Section 5.

    #region Event Type

    public enum EventType : byte
    {
        TruePosition = 1,
        TrueRotation = 2,
        TrueRotationSingleAxis = 3,
        TrueScale = 4,
        TrueUniformScale = 5,
        DeltaPosition = 6,
        DeltaRotation = 7,
        DeltaRotationSingleAxis = 8,
        DeltaScale = 9,
        DeltaUniformScale = 10,
        TrueTransform = 11,
        TruePositionRotation = 12,
        TrueRotationScale = 13,
        TruePositionScale = 14,
        TruePositionRotationUniformScale = 15,
        TruePositionRotationSingleAxis = 16,
        DeltaTransform = 17,
        DeltaPositionRotation = 18,
        DeltaRotationScale = 19,
        DeltaPositionScale = 20,
        DeltaPositionRotationSingleAxis = 21,
        DeltaPositionUniformScale = 22,
        DeltaRotationUniformScale = 23,

        // Session / lifecycle events -- see Spec.md Section 5.1.
        TimeStamp = 24,
        ShowObject = 25,
        HideObject = 26,
        InstantiateObject = 27,
        DeleteObject = 28
    }

    public static byte GetEventTypeByte(EventType eventType)
    {
        return (byte)eventType;
    }

    #endregion

    #region Ranges

    private const int EnvelopeSize = 3; // 1 byte Event ID + 2 byte Object ID

    private const float PositionRange = 500f;                   // Event 1
    private const float TrueRotationComponentRange = 0.70711f;  // Event 2
    private const float TrueSingleAxisAngleMin = 0f;            // Event 3
    private const float TrueSingleAxisAngleMax = 360f;          // Event 3
    private const float ScaleMin = -1.0f;                        // Events 4, 5, 15
    private const float ScaleMax = 10.0f;                        // Events 4, 5, 15

    // TODO: tune to the game's actual max per-tick displacement.
    private const float DeltaPositionRange = 1.5f;                // Event 6
    private const float DeltaRotationComponentRange = 0.5f;      // Event 7
    private const float DeltaSingleAxisAngleRange = 2.0f;        // Event 8
    private const float DeltaScaleRange = 1.0f;                  // Events 9, 10, 22, 23

    #endregion

    #region Envelope

    private static void WriteEnvelope(EventType eventType, ushort objectId, Span<byte> destination)
    {
        destination[0] = GetEventTypeByte(eventType);
        MemoryMarshal.Cast<byte, ushort>(destination.Slice(1, 2))[0] = objectId;
    }

    public static (EventType eventType, ushort objectId) ReadEnvelope(ReadOnlySpan<byte> source)
    {
        EventType eventType = (EventType)source[0];
        ushort objectId = MemoryMarshal.Cast<byte, ushort>(source.Slice(1, 2))[0];
        return (eventType, objectId);
    }

    #endregion

    #region Quantization Helpers

    private static uint QuantizeToBits(float value, float min, float max, int bits)
    {
        uint maxValue = (1u << bits) - 1u;
        float remapped = RemapRange(value, min, max, 0f, maxValue);
        remapped = Mathf.Clamp(remapped, 0f, maxValue);
        return (uint)Mathf.RoundToInt(remapped);
    }

    private static float DequantizeFromBits(uint value, float min, float max, int bits)
    {
        uint maxValue = (1u << bits) - 1u;
        return RemapRange(value, 0f, maxValue, min, max);
    }

    #endregion

    #region Support Serialization Functions

    private static void SerializeFloatToUInt16Buffer(float value, float min, float max, Span<byte> destination)
    {
        ushort quantized = (ushort)QuantizeToBits(value, min, max, 16);
        MemoryMarshal.Cast<byte, ushort>(destination)[0] = quantized;
    }

    private static float DeserializeFloatFromUInt16Buffer(float min, float max, ReadOnlySpan<byte> source)
    {
        ushort quantized = MemoryMarshal.Cast<byte, ushort>(source)[0];
        return DequantizeFromBits(quantized, min, max, 16);
    }

    private static void SerializeFloatToByteBuffer(float value, float min, float max, Span<byte> destination)
    {
        destination[0] = (byte)QuantizeToBits(value, min, max, 8);
    }

    private static float DeserializeFloatFromByteBuffer(float min, float max, ReadOnlySpan<byte> source)
    {
        return DequantizeFromBits(source[0], min, max, 8);
    }

    private static void SerializeVector3ToUInt16Buffer(Vector3 v, float min, float max, Span<byte> destination)
    {
        SerializeFloatToUInt16Buffer(v.x, min, max, destination.Slice(0, 2));
        SerializeFloatToUInt16Buffer(v.y, min, max, destination.Slice(2, 2));
        SerializeFloatToUInt16Buffer(v.z, min, max, destination.Slice(4, 2));
    }

    private static Vector3 DeserializeVector3FromUInt16Buffer(float min, float max, ReadOnlySpan<byte> source)
    {
        float x = DeserializeFloatFromUInt16Buffer(min, max, source.Slice(0, 2));
        float y = DeserializeFloatFromUInt16Buffer(min, max, source.Slice(2, 2));
        float z = DeserializeFloatFromUInt16Buffer(min, max, source.Slice(4, 2));
        return new Vector3(x, y, z);
    }

    private static void SerializeVector3ToByteBuffer(Vector3 v, float min, float max, Span<byte> destination)
    {
        destination[0] = (byte)QuantizeToBits(v.x, min, max, 8);
        destination[1] = (byte)QuantizeToBits(v.y, min, max, 8);
        destination[2] = (byte)QuantizeToBits(v.z, min, max, 8);
    }

    private static Vector3 DeserializeVector3FromByteBuffer(float min, float max, ReadOnlySpan<byte> source)
    {
        float x = DequantizeFromBits(source[0], min, max, 8);
        float y = DequantizeFromBits(source[1], min, max, 8);
        float z = DequantizeFromBits(source[2], min, max, 8);
        return new Vector3(x, y, z);
    }

    private static void SerializeQuaternionSmallestThreeToBuffer(Quaternion q, float componentRange, Span<byte> destination)
    {
        Span<float> components = stackalloc float[4] { q.x, q.y, q.z, q.w };

        int droppedIndex = 0;
        float largestAbs = Mathf.Abs(components[0]);
        for (int i = 1; i < 4; i++)
        {
            float abs = Mathf.Abs(components[i]);
            if (abs > largestAbs)
            {
                largestAbs = abs;
                droppedIndex = i;
            }
        }

        if (components[droppedIndex] < 0f)
        {
            components[0] = -components[0];
            components[1] = -components[1];
            components[2] = -components[2];
            components[3] = -components[3];
        }

        uint packed = (uint)(droppedIndex & 0x3);
        int shift = 2;
        for (int i = 0; i < 4; i++)
        {
            if (i == droppedIndex) continue;
            uint quantized = QuantizeToBits(components[i], -componentRange, componentRange, 10);
            packed |= (quantized & 0x3FFu) << shift;
            shift += 10;
        }

        SerializeUInt32ToBuffer(packed, destination);
    }

    private static Quaternion DeserializeQuaternionSmallestThreeFromBuffer(float componentRange, ReadOnlySpan<byte> source)
    {
        uint packed = DeserializeUInt32(source);
        int droppedIndex = (int)(packed & 0x3);

        Span<float> components = stackalloc float[4];
        int shift = 2;
        float sumSquares = 0f;
        for (int i = 0; i < 4; i++)
        {
            if (i == droppedIndex) continue;
            uint quantized = (packed >> shift) & 0x3FFu;
            float value = DequantizeFromBits(quantized, -componentRange, componentRange, 10);
            components[i] = value;
            sumSquares += value * value;
            shift += 10;
        }

        components[droppedIndex] = Mathf.Sqrt(Mathf.Max(0f, 1f - sumSquares));

        return new Quaternion(components[0], components[1], components[2], components[3]);
    }

    private static void SerializeQuaternionDropWToBuffer(Quaternion q, float componentRange, Span<byte> destination)
    {
        float x = q.x, y = q.y, z = q.z, w = q.w;
        if (w < 0f)
        {
            x = -x;
            y = -y;
            z = -z;
            w = -w;
        }

        destination[0] = (byte)QuantizeToBits(x, -componentRange, componentRange, 8);
        destination[1] = (byte)QuantizeToBits(y, -componentRange, componentRange, 8);
        destination[2] = (byte)QuantizeToBits(z, -componentRange, componentRange, 8);
    }

    private static Quaternion DeserializeQuaternionDropWFromBuffer(float componentRange, ReadOnlySpan<byte> source)
    {
        float x = DequantizeFromBits(source[0], -componentRange, componentRange, 8);
        float y = DequantizeFromBits(source[1], -componentRange, componentRange, 8);
        float z = DequantizeFromBits(source[2], -componentRange, componentRange, 8);

        float sumSquares = x * x + y * y + z * z;
        float w = Mathf.Sqrt(Mathf.Max(0f, 1f - sumSquares));

        return new Quaternion(x, y, z, w);
    }

    private static void SerializeRotationSingleAxisToBuffer(byte axisId, float angle, float min, float max, Span<byte> destination)
    {
        uint quantizedAngle = QuantizeToBits(angle, min, max, 14);
        ushort packed = (ushort)((axisId & 0x3) | (quantizedAngle << 2));
        MemoryMarshal.Cast<byte, ushort>(destination)[0] = packed;
    }

    private static (byte axisId, float angle) DeserializeRotationSingleAxisFromBuffer(float min, float max, ReadOnlySpan<byte> source)
    {
        ushort packed = MemoryMarshal.Cast<byte, ushort>(source)[0];
        byte axisId = (byte)(packed & 0x3);
        uint quantizedAngle = (uint)(packed >> 2) & 0x3FFFu;
        float angle = DequantizeFromBits(quantizedAngle, min, max, 14);
        return (axisId, angle);
    }

    private static void PackDeltaPositionToBuffer(Vector3 delta, float range, Span<byte> destination)
    {
        int qx = (int)QuantizeToBits(delta.x, -range, range, 11);
        int qy = (int)QuantizeToBits(delta.y, -range, range, 10);
        int qz = (int)QuantizeToBits(delta.z, -range, range, 11);
        SerializeBitPackedXYZToBuffer(qx, qy, qz, destination);
    }

    private static Vector3 UnpackDeltaPositionFromBuffer(float range, ReadOnlySpan<byte> source)
    {
        uint packed = DeserializeUInt32(source);
        uint qx = packed & 0x7FFu;
        uint qy = (packed >> 11) & 0x3FFu;
        uint qz = (packed >> 21) & 0x7FFu;
        float x = DequantizeFromBits(qx, -range, range, 11);
        float y = DequantizeFromBits(qy, -range, range, 10);
        float z = DequantizeFromBits(qz, -range, range, 11);
        return new Vector3(x, y, z);
    }

    #endregion

    #region Event Serialization Functions (non-allocating: write into caller-supplied Span<byte>)

    // ---------------- Single-Property Events ----------------

    public const int TruePositionSize = EnvelopeSize + 6;
    public static int SerializeTruePosition(ushort objectId, Vector3 position, Span<byte> destination)
    {
        WriteEnvelope(EventType.TruePosition, objectId, destination);
        SerializeVector3ToUInt16Buffer(position, -PositionRange, PositionRange, destination.Slice(EnvelopeSize, 6));
        return TruePositionSize;
    }

    public const int TrueRotationSize = EnvelopeSize + 4;
    public static int SerializeTrueRotation(ushort objectId, Quaternion rotation, Span<byte> destination)
    {
        WriteEnvelope(EventType.TrueRotation, objectId, destination);
        SerializeQuaternionSmallestThreeToBuffer(rotation, TrueRotationComponentRange, destination.Slice(EnvelopeSize, 4));
        return TrueRotationSize;
    }

    public const int TrueRotationSingleAxisSize = EnvelopeSize + 2;
    public static int SerializeTrueRotationSingleAxis(ushort objectId, byte axisId, float angle, Span<byte> destination)
    {
        WriteEnvelope(EventType.TrueRotationSingleAxis, objectId, destination);
        SerializeRotationSingleAxisToBuffer(axisId, angle, TrueSingleAxisAngleMin, TrueSingleAxisAngleMax, destination.Slice(EnvelopeSize, 2));
        return TrueRotationSingleAxisSize;
    }

    public const int TrueScaleSize = EnvelopeSize + 6;
    public static int SerializeTrueScale(ushort objectId, Vector3 scale, Span<byte> destination)
    {
        WriteEnvelope(EventType.TrueScale, objectId, destination);
        SerializeVector3ToUInt16Buffer(scale, ScaleMin, ScaleMax, destination.Slice(EnvelopeSize, 6));
        return TrueScaleSize;
    }

    public const int TrueUniformScaleSize = EnvelopeSize + 2;
    public static int SerializeTrueUniformScale(ushort objectId, float scale, Span<byte> destination)
    {
        WriteEnvelope(EventType.TrueUniformScale, objectId, destination);
        SerializeFloatToUInt16Buffer(scale, ScaleMin, ScaleMax, destination.Slice(EnvelopeSize, 2));
        return TrueUniformScaleSize;
    }

    public const int DeltaPositionSize = EnvelopeSize + 4;
    public static int SerializeDeltaPosition(ushort objectId, Vector3 delta, Span<byte> destination)
    {
        WriteEnvelope(EventType.DeltaPosition, objectId, destination);
        PackDeltaPositionToBuffer(delta, DeltaPositionRange, destination.Slice(EnvelopeSize, 4));
        return DeltaPositionSize;
    }

    public const int DeltaRotationSize = EnvelopeSize + 3;
    public static int SerializeDeltaRotation(ushort objectId, Quaternion deltaRotation, Span<byte> destination)
    {
        WriteEnvelope(EventType.DeltaRotation, objectId, destination);
        SerializeQuaternionDropWToBuffer(deltaRotation, DeltaRotationComponentRange, destination.Slice(EnvelopeSize, 3));
        return DeltaRotationSize;
    }

    public static Quaternion DeserializeDeltaRotation(ReadOnlySpan<byte> source)
    {
        return DeserializeQuaternionDropWFromBuffer(DeltaRotationComponentRange, source);
    }

    public const int DeltaRotationSingleAxisSize = EnvelopeSize + 2;
    public static int SerializeDeltaRotationSingleAxis(ushort objectId, byte axisId, float angleDelta, Span<byte> destination)
    {
        WriteEnvelope(EventType.DeltaRotationSingleAxis, objectId, destination);
        SerializeRotationSingleAxisToBuffer(axisId, angleDelta, -DeltaSingleAxisAngleRange, DeltaSingleAxisAngleRange, destination.Slice(EnvelopeSize, 2));
        return DeltaRotationSingleAxisSize;
    }

    public const int DeltaScaleSize = EnvelopeSize + 3;
    public static int SerializeDeltaScale(ushort objectId, Vector3 deltaScale, Span<byte> destination)
    {
        WriteEnvelope(EventType.DeltaScale, objectId, destination);
        SerializeVector3ToByteBuffer(deltaScale, -DeltaScaleRange, DeltaScaleRange, destination.Slice(EnvelopeSize, 3));
        return DeltaScaleSize;
    }

    public const int DeltaUniformScaleSize = EnvelopeSize + 1;
    public static int SerializeDeltaUniformScale(ushort objectId, float deltaScale, Span<byte> destination)
    {
        WriteEnvelope(EventType.DeltaUniformScale, objectId, destination);
        SerializeFloatToByteBuffer(deltaScale, -DeltaScaleRange, DeltaScaleRange, destination.Slice(EnvelopeSize, 1));
        return DeltaUniformScaleSize;
    }

    // ---------------- Combined "Hot Path" Events ----------------

    public const int TrueTransformSize = EnvelopeSize + 16;
    public static int SerializeTrueTransform(ushort objectId, Vector3 position, Quaternion rotation, Vector3 scale, Span<byte> destination)
    {
        WriteEnvelope(EventType.TrueTransform, objectId, destination);
        int offset = EnvelopeSize;
        SerializeVector3ToUInt16Buffer(position, -PositionRange, PositionRange, destination.Slice(offset, 6));
        offset += 6;
        SerializeQuaternionSmallestThreeToBuffer(rotation, TrueRotationComponentRange, destination.Slice(offset, 4));
        offset += 4;
        SerializeVector3ToUInt16Buffer(scale, ScaleMin, ScaleMax, destination.Slice(offset, 6));
        return TrueTransformSize;
    }

    public const int TruePositionRotationSize = EnvelopeSize + 10;
    public static int SerializeTruePositionRotation(ushort objectId, Vector3 position, Quaternion rotation, Span<byte> destination)
    {
        WriteEnvelope(EventType.TruePositionRotation, objectId, destination);
        int offset = EnvelopeSize;
        SerializeVector3ToUInt16Buffer(position, -PositionRange, PositionRange, destination.Slice(offset, 6));
        offset += 6;
        SerializeQuaternionSmallestThreeToBuffer(rotation, TrueRotationComponentRange, destination.Slice(offset, 4));
        return TruePositionRotationSize;
    }

    public const int TrueRotationScaleSize = EnvelopeSize + 10;
    public static int SerializeTrueRotationScale(ushort objectId, Quaternion rotation, Vector3 scale, Span<byte> destination)
    {
        WriteEnvelope(EventType.TrueRotationScale, objectId, destination);
        int offset = EnvelopeSize;
        SerializeQuaternionSmallestThreeToBuffer(rotation, TrueRotationComponentRange, destination.Slice(offset, 4));
        offset += 4;
        SerializeVector3ToUInt16Buffer(scale, ScaleMin, ScaleMax, destination.Slice(offset, 6));
        return TrueRotationScaleSize;
    }

    public const int TruePositionScaleSize = EnvelopeSize + 12;
    public static int SerializeTruePositionScale(ushort objectId, Vector3 position, Vector3 scale, Span<byte> destination)
    {
        WriteEnvelope(EventType.TruePositionScale, objectId, destination);
        int offset = EnvelopeSize;
        SerializeVector3ToUInt16Buffer(position, -PositionRange, PositionRange, destination.Slice(offset, 6));
        offset += 6;
        SerializeVector3ToUInt16Buffer(scale, ScaleMin, ScaleMax, destination.Slice(offset, 6));
        return TruePositionScaleSize;
    }

    public const int TruePositionRotationUniformScaleSize = EnvelopeSize + 12;
    public static int SerializeTruePositionRotationUniformScale(ushort objectId, Vector3 position, Quaternion rotation, float uniformScale, Span<byte> destination)
    {
        WriteEnvelope(EventType.TruePositionRotationUniformScale, objectId, destination);
        int offset = EnvelopeSize;
        SerializeVector3ToUInt16Buffer(position, -PositionRange, PositionRange, destination.Slice(offset, 6));
        offset += 6;
        SerializeQuaternionSmallestThreeToBuffer(rotation, TrueRotationComponentRange, destination.Slice(offset, 4));
        offset += 4;
        SerializeFloatToUInt16Buffer(uniformScale, ScaleMin, ScaleMax, destination.Slice(offset, 2));
        return TruePositionRotationUniformScaleSize;
    }

    public const int TruePositionRotationSingleAxisSize = EnvelopeSize + 8;
    public static int SerializeTruePositionRotationSingleAxis(ushort objectId, Vector3 position, byte axisId, float angle, Span<byte> destination)
    {
        WriteEnvelope(EventType.TruePositionRotationSingleAxis, objectId, destination);
        int offset = EnvelopeSize;
        SerializeVector3ToUInt16Buffer(position, -PositionRange, PositionRange, destination.Slice(offset, 6));
        offset += 6;
        SerializeRotationSingleAxisToBuffer(axisId, angle, TrueSingleAxisAngleMin, TrueSingleAxisAngleMax, destination.Slice(offset, 2));
        return TruePositionRotationSingleAxisSize;
    }

    public const int DeltaTransformSize = EnvelopeSize + 10;
    public static int SerializeDeltaTransform(ushort objectId, Vector3 deltaPosition, Quaternion deltaRotation, Vector3 deltaScale, Span<byte> destination)
    {
        WriteEnvelope(EventType.DeltaTransform, objectId, destination);
        int offset = EnvelopeSize;
        PackDeltaPositionToBuffer(deltaPosition, DeltaPositionRange, destination.Slice(offset, 4));
        offset += 4;
        SerializeQuaternionDropWToBuffer(deltaRotation, DeltaRotationComponentRange, destination.Slice(offset, 3));
        offset += 3;
        SerializeVector3ToByteBuffer(deltaScale, -DeltaScaleRange, DeltaScaleRange, destination.Slice(offset, 3));
        return DeltaTransformSize;
    }

    public const int DeltaPositionRotationSize = EnvelopeSize + 7;
    public static int SerializeDeltaPositionRotation(ushort objectId, Vector3 deltaPosition, Quaternion deltaRotation, Span<byte> destination)
    {
        WriteEnvelope(EventType.DeltaPositionRotation, objectId, destination);
        int offset = EnvelopeSize;
        PackDeltaPositionToBuffer(deltaPosition, DeltaPositionRange, destination.Slice(offset, 4));
        offset += 4;
        SerializeQuaternionDropWToBuffer(deltaRotation, DeltaRotationComponentRange, destination.Slice(offset, 3));
        return DeltaPositionRotationSize;
    }

    public const int DeltaRotationScaleSize = EnvelopeSize + 6;
    public static int SerializeDeltaRotationScale(ushort objectId, Quaternion deltaRotation, Vector3 deltaScale, Span<byte> destination)
    {
        WriteEnvelope(EventType.DeltaRotationScale, objectId, destination);
        int offset = EnvelopeSize;
        SerializeQuaternionDropWToBuffer(deltaRotation, DeltaRotationComponentRange, destination.Slice(offset, 3));
        offset += 3;
        SerializeVector3ToByteBuffer(deltaScale, -DeltaScaleRange, DeltaScaleRange, destination.Slice(offset, 3));
        return DeltaRotationScaleSize;
    }

    public const int DeltaPositionScaleSize = EnvelopeSize + 7;
    public static int SerializeDeltaPositionScale(ushort objectId, Vector3 deltaPosition, Vector3 deltaScale, Span<byte> destination)
    {
        WriteEnvelope(EventType.DeltaPositionScale, objectId, destination);
        int offset = EnvelopeSize;
        PackDeltaPositionToBuffer(deltaPosition, DeltaPositionRange, destination.Slice(offset, 4));
        offset += 4;
        SerializeVector3ToByteBuffer(deltaScale, -DeltaScaleRange, DeltaScaleRange, destination.Slice(offset, 3));
        return DeltaPositionScaleSize;
    }

    public const int DeltaPositionRotationSingleAxisSize = EnvelopeSize + 6;
    public static int SerializeDeltaPositionRotationSingleAxis(ushort objectId, Vector3 deltaPosition, byte axisId, float angleDelta, Span<byte> destination)
    {
        WriteEnvelope(EventType.DeltaPositionRotationSingleAxis, objectId, destination);
        int offset = EnvelopeSize;
        PackDeltaPositionToBuffer(deltaPosition, DeltaPositionRange, destination.Slice(offset, 4));
        offset += 4;
        SerializeRotationSingleAxisToBuffer(axisId, angleDelta, -DeltaSingleAxisAngleRange, DeltaSingleAxisAngleRange, destination.Slice(offset, 2));
        return DeltaPositionRotationSingleAxisSize;
    }

    public const int DeltaPositionUniformScaleSize = EnvelopeSize + 5;
    public static int SerializeDeltaPositionUniformScale(ushort objectId, Vector3 deltaPosition, float deltaUniformScale, Span<byte> destination)
    {
        WriteEnvelope(EventType.DeltaPositionUniformScale, objectId, destination);
        int offset = EnvelopeSize;
        PackDeltaPositionToBuffer(deltaPosition, DeltaPositionRange, destination.Slice(offset, 4));
        offset += 4;
        SerializeFloatToByteBuffer(deltaUniformScale, -DeltaScaleRange, DeltaScaleRange, destination.Slice(offset, 1));
        return DeltaPositionUniformScaleSize;
    }

    public const int DeltaRotationUniformScaleSize = EnvelopeSize + 4;
    public static int SerializeDeltaRotationUniformScale(ushort objectId, Quaternion deltaRotation, float deltaUniformScale, Span<byte> destination)
    {
        WriteEnvelope(EventType.DeltaRotationUniformScale, objectId, destination);
        int offset = EnvelopeSize;
        SerializeQuaternionDropWToBuffer(deltaRotation, DeltaRotationComponentRange, destination.Slice(offset, 3));
        offset += 3;
        SerializeFloatToByteBuffer(deltaUniformScale, -DeltaScaleRange, DeltaScaleRange, destination.Slice(offset, 1));
        return DeltaRotationUniformScaleSize;
    }

    // ---------------- Session / Lifecycle Events ----------------

    // No standard envelope: this event describes the stream itself, not an object, so there's
    // no Object ID to write. Just EventType byte + an 8-byte double (seconds).
    public const int TimeStampSize = 1 + 8;
    public static int SerializeTimeStamp(double timeSeconds, Span<byte> destination)
    {
        destination[0] = GetEventTypeByte(EventType.TimeStamp);
        MemoryMarshal.Cast<byte, double>(destination.Slice(1, 8))[0] = timeSeconds;
        return TimeStampSize;
    }

    public static double DeserializeTimeStamp(ReadOnlySpan<byte> source)
    {
        return MemoryMarshal.Cast<byte, double>(source.Slice(1, 8))[0];
    }

    public const int ShowObjectSize = EnvelopeSize;
    public static int SerializeShowObject(ushort objectId, Span<byte> destination)
    {
        WriteEnvelope(EventType.ShowObject, objectId, destination);
        return ShowObjectSize;
    }

    public const int HideObjectSize = EnvelopeSize;
    public static int SerializeHideObject(ushort objectId, Span<byte> destination)
    {
        WriteEnvelope(EventType.HideObject, objectId, destination);
        return HideObjectSize;
    }

    public const int DeleteObjectSize = EnvelopeSize;
    public static int SerializeDeleteObject(ushort objectId, Span<byte> destination)
    {
        WriteEnvelope(EventType.DeleteObject, objectId, destination);
        return DeleteObjectSize;
    }

    // Envelope's Object ID is the NEW instance's ID; templateObjectId identifies which
    // PrefabInstanceLibrary template (itself a normal tracked object from the initial scene
    // walk) this instance was cloned from.
    public const int InstantiateObjectSize = EnvelopeSize + 2 + 16;
    public static int SerializeInstantiateObject(ushort newObjectId, ushort templateObjectId, Vector3 position, Quaternion rotation, Vector3 scale, Span<byte> destination)
    {
        WriteEnvelope(EventType.InstantiateObject, newObjectId, destination);
        int offset = EnvelopeSize;
        MemoryMarshal.Cast<byte, ushort>(destination.Slice(offset, 2))[0] = templateObjectId;
        offset += 2;
        SerializeVector3ToUInt16Buffer(position, -PositionRange, PositionRange, destination.Slice(offset, 6));
        offset += 6;
        SerializeQuaternionSmallestThreeToBuffer(rotation, TrueRotationComponentRange, destination.Slice(offset, 4));
        offset += 4;
        SerializeVector3ToUInt16Buffer(scale, ScaleMin, ScaleMax, destination.Slice(offset, 6));
        return InstantiateObjectSize;
    }

    public static (ushort templateObjectId, Vector3 position, Quaternion rotation, Vector3 scale) DeserializeInstantiateObject(ReadOnlySpan<byte> source)
    {
        int offset = EnvelopeSize;
        ushort templateObjectId = MemoryMarshal.Cast<byte, ushort>(source.Slice(offset, 2))[0];
        offset += 2;
        Vector3 position = DeserializeVector3FromUInt16Buffer(-PositionRange, PositionRange, source.Slice(offset, 6));
        offset += 6;
        Quaternion rotation = DeserializeQuaternionSmallestThreeFromBuffer(TrueRotationComponentRange, source.Slice(offset, 4));
        offset += 4;
        Vector3 scale = DeserializeVector3FromUInt16Buffer(ScaleMin, ScaleMax, source.Slice(offset, 6));
        return (templateObjectId, position, rotation, scale);
    }

    // Largest possible single-event payload across every event type -- currently InstantiateObject.
    public const int MaxEventSize = InstantiateObjectSize;

    #endregion
}