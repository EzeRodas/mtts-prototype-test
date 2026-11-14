using System.Buffers.Binary;

namespace MoonTTS.Infrastructure.Daemon.DTOs;

/* Daemon to keep model loaded on RAM for 5 minutes before unloading it.
   ONLY USED ON CLI VERSION, NOT PRESENT ON GUI VERSION */

public static class DaemonIpcProtocol
{
    /* Per user pipe name to prevent multi-user permission collissions on Linux / Windows */
    public static string PipeName => $"Moon-TTS_Daemon_{Environment.UserName}";

    /* Per user pipe name for cross-process CLI pause/stop control during active playback */
    public static string PlaybackControlPipeName => $"Moon-TTS_Playback_{Environment.UserName}";

    public const string ActionSynthesize = "synthesize";
    public const string ActionKill = "kill";
    public const string ActionStatus = "status";
    public const string ActionStop = "stop";

    /* Frame layout */
    public const int HeaderSize = 5;
    
    /* Safety boundary, 32mb max to prevent corrupted streams */
    public const int MaxPayloadSize = 32 * 1024 * 1024;

    public static async ValueTask WriteFrameAsync(
        Stream stream,
        DaemonOpcode opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[HeaderSize];
        header[0] = (byte)opcode;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1, 4), payload.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    
    public static async ValueTask<(DaemonOpcode Opcode, byte[] Payload)> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

        var opcode = (DaemonOpcode)header[0];
        int payloadLenght = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4));

        if (payloadLenght < 0 || payloadLenght > MaxPayloadSize)
        {
            throw new InvalidOperationException($"Invalid IPC frame payload size: {payloadLenght} bytes.");
        }

        if (payloadLenght == 0)
        {
            return (opcode, Array.Empty<byte>());
        }

        byte[] payload = new byte[payloadLenght];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return (opcode, payload);
    }
}
