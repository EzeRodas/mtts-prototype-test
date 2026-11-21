using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MoonTTS.Engine.Protocol;

/* File Structure:
        - WriteFrameAsync(Stream stream, EngineOpcode opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) : ValueTask
        - WriteJsonFrameAsync<T>(Stream stream, EngineOpcode opcode, T message, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken) : ValueTask
        - ReadFrameAsync(Stream stream, CancellationToken cancellationToken) : ValueTask<EngineFrame?>
        - ReadJson<T>(EngineFrame frame, JsonTypeInfo<T> typeInfo) : T
*/

/* Binary framing shared by the host and engine workers.
   Header layout (8 bytes): [Magic 'M' 'T'][Opcode (1B)][Flags (1B)][Payload length (uint32, big-endian)] */
public static class EngineIpcProtocol
{
    public const byte MagicFirst = 0x4D;  /* 'M' */
    public const byte MagicSecond = 0x54; /* 'T' */
    public const int HeaderSize = 8;

    /* Safety boundary, 32mb max to prevent corrupted streams from allocating unbounded memory */
    public const int MaxPayloadSize = 32 * 1024 * 1024;

    /* Writes one frame and flushes it so the peer receives it immediately */
    public static async ValueTask WriteFrameAsync(
        Stream stream,
        EngineOpcode opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (payload.Length > MaxPayloadSize)
            throw new InvalidOperationException($"Engine IPC payload too large: {payload.Length} bytes.");

        byte[] header = ArrayPool<byte>.Shared.Rent(HeaderSize);
        try
        {
            header[0] = MagicFirst;
            header[1] = MagicSecond;
            header[2] = (byte)opcode;
            header[3] = 0;
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)payload.Length);

            await stream.WriteAsync(header.AsMemory(0, HeaderSize), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }

        if (!payload.IsEmpty)
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /* Serializes a control message with source-generated metadata and writes it as one frame */
    public static ValueTask WriteJsonFrameAsync<T>(
        Stream stream,
        EngineOpcode opcode,
        T message,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default) =>
        WriteFrameAsync(stream, opcode, JsonSerializer.SerializeToUtf8Bytes(message, typeInfo), cancellationToken);

    /* Reads one frame. Returns null when the peer closed the stream cleanly between frames */
    public static async ValueTask<EngineFrame?> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        EngineOpcode opcode;
        byte flags;
        uint payloadLength;

        byte[] header = ArrayPool<byte>.Shared.Rent(HeaderSize);
        try
        {
            int headerRead = await stream.ReadAtLeastAsync(header.AsMemory(0, HeaderSize), HeaderSize, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);

            if (headerRead == 0)
                return null;
            if (headerRead < HeaderSize)
                throw new EndOfStreamException("Engine IPC stream ended in the middle of a frame header.");

            if (header[0] != MagicFirst || header[1] != MagicSecond)
                throw new InvalidDataException("Engine IPC frame has invalid magic bytes; the stream is out of sync.");

            opcode = (EngineOpcode)header[2];
            flags = header[3];
            payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }

        if (payloadLength > MaxPayloadSize)
            throw new InvalidDataException($"Invalid engine IPC payload size: {payloadLength} bytes.");

        if (payloadLength == 0)
            return new EngineFrame(opcode, flags, ReadOnlyMemory<byte>.Empty);

        byte[] payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return new EngineFrame(opcode, flags, payload);
    }

    /* Deserializes the JSON payload of a control frame */
    public static T ReadJson<T>(EngineFrame frame, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(frame.Payload.Span, typeInfo)
        ?? throw new InvalidDataException($"Engine IPC frame {frame.Opcode} carried an empty JSON payload.");
}
