using System.Buffers.Binary;
using DPLL_Ultrasonic_DAQ.Models;

namespace DPLL_Ultrasonic_DAQ.Protocol;

/// <summary>Opcode values — must match the firmware's <c>opcode.h</c> enum.</summary>
public static class Opcode
{
    public const ushort ILEGAL_OPCODE = 0x0000;
    public const ushort SET_KP = 0x0001;
    public const ushort GET_KP = 0x0002;
    public const ushort SET_KI = 0x0003;
    public const ushort GET_KI = 0x0004;
    public const ushort SET_KD = 0x0005;
    public const ushort GET_KD = 0x0006;
    public const ushort SET_CENTER_VOLTAGE = 0x0007;
    public const ushort GET_CENTER_VOLTAGE = 0x0008;
    public const ushort SET_TARGET_PHASE = 0x0009;
    public const ushort GET_TARGET_PHASE = 0x000A;
    public const ushort SET_OUTPUT_LIMITS = 0x000B;
    public const ushort GET_OUTPUT_LIMITS = 0x000C;
    public const ushort SET_MAX_SLEW = 0x000D;
    public const ushort GET_MAX_SLEW = 0x000E;
    public const ushort SET_ENABLE_LOOP = 0x000F;
    public const ushort GET_LOOP_ENABLE = 0x0010;
    public const ushort RESET_LOOP = 0x0012;
    public const ushort SHUTDOWN_LOOP = 0x0013;
    public const ushort SET_VOLTAGE = 0x0014;
    public const ushort GET_VOLTAGE = 0x0015;
    public const ushort SET_ALLOW_SEND_STREAM = 0x0017;
    public const ushort GET_ALLOW_SEND_STREAM = 0x0018;
    public const ushort STREAM_DPLL_STATUS = 0x0019;
}

/// <summary>
/// Builds and parses the firmware binary packet protocol.
///
/// Header (8 bytes):
///   [0]    start byte   0xAA
///   [1..2] opcode       uint16 LE
///   [3..4] address      uint16 LE
///   [5..6] length       uint16 LE = payloadLength + 1 (checksum byte)
///   [7]    end byte     0xBB
/// Payload: variable length + 1 trailing checksum byte.
/// Checksum = two's complement of the 8-bit sum of the payload bytes.
/// </summary>
public static class DpllProtocol
{
    public const int HeaderSize = 8;
    public const int MaxPayloadSize = 512;
    public const byte StartByte = 0xAA;
    public const byte EndByte = 0xBB;

    /// <summary>Max bytes read per serial poll (header + payload + checksum).</summary>
    public const int MaxPacketSize = HeaderSize + MaxPayloadSize + 1;

    /// <summary>
    /// Build a complete binary packet (header + payload + checksum).
    /// </summary>
    public static byte[] BuildPacket(ushort opcode, ushort address = 0, ReadOnlySpan<byte> payload = default)
    {
        int payloadLen = payload.Length;
        if (payloadLen > MaxPayloadSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), $"Payload exceeds {MaxPayloadSize} bytes.");
        }

        byte[] buffer = new byte[HeaderSize + payloadLen + 1];

        buffer[0] = StartByte;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(1, 2), opcode);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(3, 2), address);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(5, 2), (ushort)(payloadLen + 1));
        buffer[7] = EndByte;

        if (payloadLen > 0)
        {
            payload.CopyTo(buffer.AsSpan(HeaderSize, payloadLen));
        }

        // Checksum over payload bytes only (matches firmware calculate_sum).
        buffer[^1] = ComputeChecksum(payload);
        return buffer;
    }

    /// <summary>
    /// Two's-complement checksum of the given bytes, matching the firmware.
    /// </summary>
    public static byte ComputeChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        foreach (byte b in data)
        {
            sum += b;
        }
        return (byte)((sum ^ 0xFF) + 1);
    }

    /// <summary>
    /// Try to parse a complete packet from <paramref name="buffer"/> (one or more
    /// packets may be buffered). Returns the number of bytes consumed, or -1 if
    /// more data is needed. Invalid framing is resynchronized by discarding bytes.
    /// </summary>
    public static bool TryParsePacket(ReadOnlySpan<byte> buffer, out int consumed, out ushort opcode, out ushort address, out ReadOnlySpan<byte> payload)
    {
        consumed = 0;
        opcode = 0;
        address = 0;
        payload = default;

        // Resync: scan for the start byte.
        int start = buffer.IndexOf((byte)StartByte);
        if (start < 0)
        {
            consumed = buffer.Length; // nothing useful — drop all
            return false;
        }
        if (start > 0)
        {
            consumed = start; // drop leading garbage
            return false;
        }

        // Need full header before inspecting.
        if (buffer.Length < HeaderSize)
        {
            return false;
        }
        if (buffer[7] != EndByte)
        {
            consumed = 1; // bad header — drop the start byte and resync
            return false;
        }

        ushort declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(5, 2));
        if (declaredLength == 0 || declaredLength > MaxPayloadSize + 1)
        {
            consumed = 1;
            return false;
        }

        int total = HeaderSize + declaredLength;
        if (buffer.Length < total)
        {
            return false; // wait for the rest
        }

        payload = buffer.Slice(HeaderSize, declaredLength - 1);
        byte checksum = buffer[HeaderSize + declaredLength - 1];
        if (ComputeChecksum(payload) != checksum)
        {
            consumed = 1; // corrupt — drop the start byte and resync
            return false;
        }

        opcode = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(1, 2));
        address = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(3, 2));
        consumed = total;
        return true;
    }

    /// <summary>
    /// Decode the 16-byte <c>dpllStatusData</c> payload of an OPCODE_STREAM_DPLL_STATUS packet.
    /// Layout (packed, little-endian floats): freq Hz, phase ns, DAC V, lock byte, stale byte, pad[2].
    /// </summary>
    public static DpllTelemetry DecodeStatusPayload(ReadOnlySpan<byte> payload, DateTimeOffset timestamp)
    {
        var t = new DpllTelemetry
        {
            ReferenceFrequencyHz = ReadFloatLe(payload, 0),
            PhaseErrorNs = ReadFloatLe(payload, 4),
            DACVoltage_V = ReadFloatLe(payload, 8),
            LockStatus = payload.Length > 12 ? payload[12] : 0,
            PhaseStale = payload.Length > 13 ? payload[13] : 0,
            Timestamp = timestamp.ToUnixTimeSeconds() + timestamp.Millisecond / 1000.0
        };
        return t;
    }

    private static float ReadFloatLe(ReadOnlySpan<byte> data, int offset)
    {
        if (data.Length < offset + 4)
        {
            return 0f;
        }
        return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)));
    }
}
