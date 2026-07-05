using System;
using System.Runtime.InteropServices;
using UnityEngine;

public static class Serializer
{
    public static float RemapRange(float x, float fromMin, float fromMax, float toMin, float toMax)
    {
        // Add bounds checking to prevent invalid remapping
        if (Mathf.Approximately(fromMax, fromMin)) return toMin;

        float normalized = (x - fromMin) / (fromMax - fromMin);
        return normalized * (toMax - toMin) + toMin;
    }

    // -------- 4-byte XYZ BitPacking: X(11) Y(10) Z(11) --------
    public static void SerializeBitPackedXYZToBuffer(int x, int y, int z, Span<byte> destination)
    {
        // Pack into 32 bits: X(11 bits) + Y(10 bits) + Z(11 bits)
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


    /*

=== Event Map v2 ===
Envelope note: every event below is preceded by a 1-byte Event ID, and 2 byte Object ID

--- Single-Property Events ---

Event 1  : True Position
-> 3 floats, 2 bytes each. (Total: 6 bytes) [Range: -500 to +500]

Event 2  : True Rotation (Quaternion, smallest-three)
-> 2 bits dropped-index + 3x10 bits component. (Total: 4 bytes) [Component range: ±0.70711]

Event 3  : True RotationSingleAxis
-> 2 bits axis id + 14 bits angle. (Total: 2 bytes) [Range: 0-360]

Event 4  : True Scale
-> 3 floats, 2 bytes each. (Total: 6 bytes) [Range: -1.0 to +10.0]

Event 5  : True UniformScale
-> 1 float, 2 bytes. (Total: 2 bytes) [Range: -1.0 to +10.0]

Event 6  : Delta Position
-> x:11 y:10 z:11 bits. (Total: 4 bytes) [Range: tuned to max per-tick displacement]

Event 7  : Delta Rotation (Quaternion, drop-W)
-> W always dropped (forced non-negative hemisphere) + 3x8 bits component (X,Y,Z). (Total: 3 bytes) [Component range: ±0.5]

Event 8  : Delta RotationSingleAxis
-> 2 bits axis id + 14 bits angle delta. (Total: 2 bytes) [Range: -2.0 to 2.0]

Event 9  : Delta Scale
-> 3 floats, 1 byte each. (Total: 3 bytes) [Range: -1.0 to +1.0]

Event 10 : Delta UniformScale
-> 1 float, 1 byte. (Total: 1 byte) [Range: -1.0 to +1.0]
———————————————————————————————————————
--- Combined "Hot Path" Events ---

Event 11 : True Transform
-> Position(6) + Rotation(4) + Scale(6). (Total: 16 bytes)

Event 12 : True PositionRotation
-> Position(6) + Rotation(4). (Total: 10 bytes)

Event 13 : True RotationScale
-> Rotation(4) + Scale(6). (Total: 10 bytes)

Event 14 : True PositionScale
-> Position(6) + Scale(6). (Total: 12 bytes)

Event 15 : True PositionRotationUniformScale
-> Position(6) + Rotation(4) + UniformScale(2). (Total: 12 bytes)

Event 16 : True PositionRotationSingleAxis
-> Position(6) + RotationSingleAxis(2). (Total: 8 bytes)

Event 17 : Delta Transform
-> Position(4) + Rotation(3) + Scale(3). (Total: 10 bytes)

Event 18 : Delta PositionRotation
-> Position(4) + Rotation(3). (Total: 7 bytes)

Event 19 : Delta RotationScale
-> Rotation(3) + Scale(3). (Total: 6 bytes)

Event 20 : Delta PositionScale
-> Position(4) + Scale(3). (Total: 7 bytes)

Event 21 : Delta PositionRotationSingleAxis
-> Position(4) + RotationSingleAxis(2). (Total: 6 bytes)

Event 22 : Delta PositionUniformScale
-> Position(4) + UniformScale(1). (Total: 5 bytes)

Event 23 : Delta RotationUniformScale
-> Rotation(3) + UniformScale(1). (Total: 4 bytes)

    Implementation follows below.
    */

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
        DeltaRotationUniformScale = 23
    }

    public static byte GetEventTypeByte(EventType eventType)
    {
        return (byte)eventType;
    }

    #endregion

    #region Ranges

    private const int EnvelopeSize = 3; // 1 byte Event ID + 2 byte Object ID

    private const float PositionRange = 500f;                   // Events 1
    private const float TrueRotationComponentRange = 0.70711f;  // Event 2
    private const float TrueSingleAxisAngleMin = 0f;            // Event 3
    private const float TrueSingleAxisAngleMax = 360f;          // Event 3
    private const float ScaleMin = -1.0f;                        // Events 4, 5, 15
    private const float ScaleMax = 10.0f;                        // Events 4, 5, 15

    // TODO: tune to the game's actual max per-tick displacement.
    private const float DeltaPositionRange = 16f;                // Event 6
    private const float DeltaRotationComponentRange = 0.5f;      // Event 7
    // NOTE: a quaternion component can only ever be in [-1, 1]. Keeping this range
    // at ±5.0 wastes most of the 8-bit budget on values that will never occur -
    // consider tightening this to something like ±1.0 for meaningfully better precision.
    private const float DeltaSingleAxisAngleRange = 2.0f;        // Event 8
    private const float DeltaScaleRange = 1.0f;                  // Events 9, 10, 22, 23

    #endregion

    #region Envelope

    private static void WriteEnvelope(EventType eventType, ushort objectId, Span<byte> destination)
    {
        destination[0] = GetEventTypeByte(eventType);
        MemoryMarshal.Cast<byte, ushort>(destination.Slice(1, 2))[0] = objectId;
    }

    private static (EventType eventType, ushort objectId) ReadEnvelope(ReadOnlySpan<byte> source)
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

    // ---- Single float <-> 2 bytes ----
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

    // ---- Single float <-> 1 byte ----
    private static void SerializeFloatToByteBuffer(float value, float min, float max, Span<byte> destination)
    {
        destination[0] = (byte)QuantizeToBits(value, min, max, 8);
    }

    private static float DeserializeFloatFromByteBuffer(float min, float max, ReadOnlySpan<byte> source)
    {
        return DequantizeFromBits(source[0], min, max, 8);
    }

    // ---- Vector3 <-> 6 bytes (3x uint16) ----
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

    // ---- Vector3 <-> 3 bytes (3x byte) ----
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

    // ---- Quaternion (smallest-three) <-> 4 bytes: 2-bit dropped index + 3x10-bit component ----
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

        // Force the dropped (largest-magnitude) component positive so the receiver
        // can reconstruct it as +sqrt(1 - sum of squares) of the other three.
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

    // ---- Quaternion (drop-W) <-> 3 bytes: 3x8-bit component (X, Y, Z only) ----
    // W is always the dropped component (rather than "whichever is largest"). Since W
    // is not guaranteed to be the largest-magnitude component, we can't rely on dropping
    // the largest component to keep the reconstruction sign unambiguous - instead we
    // explicitly force W non-negative by flipping the whole quaternion's sign if needed
    // (valid because q and -q represent the same rotation).
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

    // ---- RotationSingleAxis <-> 2 bytes: 2-bit axis id + 14-bit angle ----
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

    // ---- Delta Position <-> 4 bytes: x(11) y(10) z(11), reusing the existing bit-packer ----
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

    #region Event Serialization Functions

    // ---------------- Single-Property Events ----------------

    // Event 1: True Position -> envelope(3) + 6 bytes
    public static byte[] SerializeTruePosition(ushort objectId, Vector3 position)
    {
        byte[] buffer = new byte[EnvelopeSize + 6];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.TruePosition, objectId, span);
        SerializeVector3ToUInt16Buffer(position, -PositionRange, PositionRange, span.Slice(EnvelopeSize, 6));
        return buffer;
    }

    // Event 2: True Rotation -> envelope(3) + 4 bytes
    public static byte[] SerializeTrueRotation(ushort objectId, Quaternion rotation)
    {
        byte[] buffer = new byte[EnvelopeSize + 4];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.TrueRotation, objectId, span);
        SerializeQuaternionSmallestThreeToBuffer(rotation, TrueRotationComponentRange, span.Slice(EnvelopeSize, 4));
        return buffer;
    }

    // Event 3: True RotationSingleAxis -> envelope(3) + 2 bytes
    public static byte[] SerializeTrueRotationSingleAxis(ushort objectId, byte axisId, float angle)
    {
        byte[] buffer = new byte[EnvelopeSize + 2];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.TrueRotationSingleAxis, objectId, span);
        SerializeRotationSingleAxisToBuffer(axisId, angle, TrueSingleAxisAngleMin, TrueSingleAxisAngleMax, span.Slice(EnvelopeSize, 2));
        return buffer;
    }

    // Event 4: True Scale -> envelope(3) + 6 bytes
    public static byte[] SerializeTrueScale(ushort objectId, Vector3 scale)
    {
        byte[] buffer = new byte[EnvelopeSize + 6];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.TrueScale, objectId, span);
        SerializeVector3ToUInt16Buffer(scale, ScaleMin, ScaleMax, span.Slice(EnvelopeSize, 6));
        return buffer;
    }

    // Event 5: True UniformScale -> envelope(3) + 2 bytes
    public static byte[] SerializeTrueUniformScale(ushort objectId, float scale)
    {
        byte[] buffer = new byte[EnvelopeSize + 2];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.TrueUniformScale, objectId, span);
        SerializeFloatToUInt16Buffer(scale, ScaleMin, ScaleMax, span.Slice(EnvelopeSize, 2));
        return buffer;
    }

    // Event 6: Delta Position -> envelope(3) + 4 bytes
    public static byte[] SerializeDeltaPosition(ushort objectId, Vector3 delta)
    {
        byte[] buffer = new byte[EnvelopeSize + 4];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.DeltaPosition, objectId, span);
        PackDeltaPositionToBuffer(delta, DeltaPositionRange, span.Slice(EnvelopeSize, 4));
        return buffer;
    }

    // Event 7: Delta Rotation -> envelope(3) + 3 bytes (W dropped, reconstructed from X/Y/Z)
    public static byte[] SerializeDeltaRotation(ushort objectId, Quaternion deltaRotation)
    {
        byte[] buffer = new byte[EnvelopeSize + 3];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.DeltaRotation, objectId, span);
        SerializeQuaternionDropWToBuffer(deltaRotation, DeltaRotationComponentRange, span.Slice(EnvelopeSize, 3));
        return buffer;
    }

    public static Quaternion DeserializeDeltaRotation(ReadOnlySpan<byte> source)
    {
        // Assumes `source` is already positioned past the envelope, at the 3-byte payload.
        return DeserializeQuaternionDropWFromBuffer(DeltaRotationComponentRange, source);
    }

    // Event 8: Delta RotationSingleAxis -> envelope(3) + 2 bytes
    public static byte[] SerializeDeltaRotationSingleAxis(ushort objectId, byte axisId, float angleDelta)
    {
        byte[] buffer = new byte[EnvelopeSize + 2];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.DeltaRotationSingleAxis, objectId, span);
        SerializeRotationSingleAxisToBuffer(axisId, angleDelta, -DeltaSingleAxisAngleRange, DeltaSingleAxisAngleRange, span.Slice(EnvelopeSize, 2));
        return buffer;
    }

    // Event 9: Delta Scale -> envelope(3) + 3 bytes
    public static byte[] SerializeDeltaScale(ushort objectId, Vector3 deltaScale)
    {
        byte[] buffer = new byte[EnvelopeSize + 3];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.DeltaScale, objectId, span);
        SerializeVector3ToByteBuffer(deltaScale, -DeltaScaleRange, DeltaScaleRange, span.Slice(EnvelopeSize, 3));
        return buffer;
    }

    // Event 10: Delta UniformScale -> envelope(3) + 1 byte
    public static byte[] SerializeDeltaUniformScale(ushort objectId, float deltaScale)
    {
        byte[] buffer = new byte[EnvelopeSize + 1];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.DeltaUniformScale, objectId, span);
        SerializeFloatToByteBuffer(deltaScale, -DeltaScaleRange, DeltaScaleRange, span.Slice(EnvelopeSize, 1));
        return buffer;
    }

    // ---------------- Combined "Hot Path" Events ----------------

    // Event 11: True Transform -> envelope(3) + 16 bytes
    public static byte[] SerializeTrueTransform(ushort objectId, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        byte[] buffer = new byte[EnvelopeSize + 16];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.TrueTransform, objectId, span);

        int offset = EnvelopeSize;
        SerializeVector3ToUInt16Buffer(position, -PositionRange, PositionRange, span.Slice(offset, 6));
        offset += 6;
        SerializeQuaternionSmallestThreeToBuffer(rotation, TrueRotationComponentRange, span.Slice(offset, 4));
        offset += 4;
        SerializeVector3ToUInt16Buffer(scale, ScaleMin, ScaleMax, span.Slice(offset, 6));

        return buffer;
    }

    // Event 12: True PositionRotation -> envelope(3) + 10 bytes
    public static byte[] SerializeTruePositionRotation(ushort objectId, Vector3 position, Quaternion rotation)
    {
        byte[] buffer = new byte[EnvelopeSize + 10];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.TruePositionRotation, objectId, span);

        int offset = EnvelopeSize;
        SerializeVector3ToUInt16Buffer(position, -PositionRange, PositionRange, span.Slice(offset, 6));
        offset += 6;
        SerializeQuaternionSmallestThreeToBuffer(rotation, TrueRotationComponentRange, span.Slice(offset, 4));

        return buffer;
    }

    // Event 13: True RotationScale -> envelope(3) + 10 bytes
    public static byte[] SerializeTrueRotationScale(ushort objectId, Quaternion rotation, Vector3 scale)
    {
        byte[] buffer = new byte[EnvelopeSize + 10];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.TrueRotationScale, objectId, span);

        int offset = EnvelopeSize;
        SerializeQuaternionSmallestThreeToBuffer(rotation, TrueRotationComponentRange, span.Slice(offset, 4));
        offset += 4;
        SerializeVector3ToUInt16Buffer(scale, ScaleMin, ScaleMax, span.Slice(offset, 6));

        return buffer;
    }

    // Event 14: True PositionScale -> envelope(3) + 12 bytes
    public static byte[] SerializeTruePositionScale(ushort objectId, Vector3 position, Vector3 scale)
    {
        byte[] buffer = new byte[EnvelopeSize + 12];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.TruePositionScale, objectId, span);

        int offset = EnvelopeSize;
        SerializeVector3ToUInt16Buffer(position, -PositionRange, PositionRange, span.Slice(offset, 6));
        offset += 6;
        SerializeVector3ToUInt16Buffer(scale, ScaleMin, ScaleMax, span.Slice(offset, 6));

        return buffer;
    }

    // Event 15: True PositionRotationUniformScale -> envelope(3) + 12 bytes
    public static byte[] SerializeTruePositionRotationUniformScale(ushort objectId, Vector3 position, Quaternion rotation, float uniformScale)
    {
        byte[] buffer = new byte[EnvelopeSize + 12];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.TruePositionRotationUniformScale, objectId, span);

        int offset = EnvelopeSize;
        SerializeVector3ToUInt16Buffer(position, -PositionRange, PositionRange, span.Slice(offset, 6));
        offset += 6;
        SerializeQuaternionSmallestThreeToBuffer(rotation, TrueRotationComponentRange, span.Slice(offset, 4));
        offset += 4;
        SerializeFloatToUInt16Buffer(uniformScale, ScaleMin, ScaleMax, span.Slice(offset, 2));

        return buffer;
    }

    // Event 16: True PositionRotationSingleAxis -> envelope(3) + 8 bytes
    public static byte[] SerializeTruePositionRotationSingleAxis(ushort objectId, Vector3 position, byte axisId, float angle)
    {
        byte[] buffer = new byte[EnvelopeSize + 8];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.TruePositionRotationSingleAxis, objectId, span);

        int offset = EnvelopeSize;
        SerializeVector3ToUInt16Buffer(position, -PositionRange, PositionRange, span.Slice(offset, 6));
        offset += 6;
        SerializeRotationSingleAxisToBuffer(axisId, angle, TrueSingleAxisAngleMin, TrueSingleAxisAngleMax, span.Slice(offset, 2));

        return buffer;
    }

    // Event 17: Delta Transform -> envelope(3) + 10 bytes
    public static byte[] SerializeDeltaTransform(ushort objectId, Vector3 deltaPosition, Quaternion deltaRotation, Vector3 deltaScale)
    {
        byte[] buffer = new byte[EnvelopeSize + 10];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.DeltaTransform, objectId, span);

        int offset = EnvelopeSize;
        PackDeltaPositionToBuffer(deltaPosition, DeltaPositionRange, span.Slice(offset, 4));
        offset += 4;
        SerializeQuaternionDropWToBuffer(deltaRotation, DeltaRotationComponentRange, span.Slice(offset, 3));
        offset += 3;
        SerializeVector3ToByteBuffer(deltaScale, -DeltaScaleRange, DeltaScaleRange, span.Slice(offset, 3));

        return buffer;
    }

    // Event 18: Delta PositionRotation -> envelope(3) + 7 bytes
    public static byte[] SerializeDeltaPositionRotation(ushort objectId, Vector3 deltaPosition, Quaternion deltaRotation)
    {
        byte[] buffer = new byte[EnvelopeSize + 7];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.DeltaPositionRotation, objectId, span);

        int offset = EnvelopeSize;
        PackDeltaPositionToBuffer(deltaPosition, DeltaPositionRange, span.Slice(offset, 4));
        offset += 4;
        SerializeQuaternionDropWToBuffer(deltaRotation, DeltaRotationComponentRange, span.Slice(offset, 3));

        return buffer;
    }

    // Event 19: Delta RotationScale -> envelope(3) + 6 bytes
    public static byte[] SerializeDeltaRotationScale(ushort objectId, Quaternion deltaRotation, Vector3 deltaScale)
    {
        byte[] buffer = new byte[EnvelopeSize + 6];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.DeltaRotationScale, objectId, span);

        int offset = EnvelopeSize;
        SerializeQuaternionDropWToBuffer(deltaRotation, DeltaRotationComponentRange, span.Slice(offset, 3));
        offset += 3;
        SerializeVector3ToByteBuffer(deltaScale, -DeltaScaleRange, DeltaScaleRange, span.Slice(offset, 3));

        return buffer;
    }

    // Event 20: Delta PositionScale -> envelope(3) + 7 bytes
    public static byte[] SerializeDeltaPositionScale(ushort objectId, Vector3 deltaPosition, Vector3 deltaScale)
    {
        byte[] buffer = new byte[EnvelopeSize + 7];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.DeltaPositionScale, objectId, span);

        int offset = EnvelopeSize;
        PackDeltaPositionToBuffer(deltaPosition, DeltaPositionRange, span.Slice(offset, 4));
        offset += 4;
        SerializeVector3ToByteBuffer(deltaScale, -DeltaScaleRange, DeltaScaleRange, span.Slice(offset, 3));

        return buffer;
    }

    // Event 21: Delta PositionRotationSingleAxis -> envelope(3) + 6 bytes
    public static byte[] SerializeDeltaPositionRotationSingleAxis(ushort objectId, Vector3 deltaPosition, byte axisId, float angleDelta)
    {
        byte[] buffer = new byte[EnvelopeSize + 6];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.DeltaPositionRotationSingleAxis, objectId, span);

        int offset = EnvelopeSize;
        PackDeltaPositionToBuffer(deltaPosition, DeltaPositionRange, span.Slice(offset, 4));
        offset += 4;
        SerializeRotationSingleAxisToBuffer(axisId, angleDelta, -DeltaSingleAxisAngleRange, DeltaSingleAxisAngleRange, span.Slice(offset, 2));

        return buffer;
    }

    // Event 22: Delta PositionUniformScale -> envelope(3) + 5 bytes
    public static byte[] SerializeDeltaPositionUniformScale(ushort objectId, Vector3 deltaPosition, float deltaUniformScale)
    {
        byte[] buffer = new byte[EnvelopeSize + 5];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.DeltaPositionUniformScale, objectId, span);

        int offset = EnvelopeSize;
        PackDeltaPositionToBuffer(deltaPosition, DeltaPositionRange, span.Slice(offset, 4));
        offset += 4;
        SerializeFloatToByteBuffer(deltaUniformScale, -DeltaScaleRange, DeltaScaleRange, span.Slice(offset, 1));

        return buffer;
    }

    // Event 23: Delta RotationUniformScale -> envelope(3) + 4 bytes
    public static byte[] SerializeDeltaRotationUniformScale(ushort objectId, Quaternion deltaRotation, float deltaUniformScale)
    {
        byte[] buffer = new byte[EnvelopeSize + 4];
        Span<byte> span = buffer;
        WriteEnvelope(EventType.DeltaRotationUniformScale, objectId, span);

        int offset = EnvelopeSize;
        SerializeQuaternionDropWToBuffer(deltaRotation, DeltaRotationComponentRange, span.Slice(offset, 3));
        offset += 3;
        SerializeFloatToByteBuffer(deltaUniformScale, -DeltaScaleRange, DeltaScaleRange, span.Slice(offset, 1));

        return buffer;
    }

    #endregion
}