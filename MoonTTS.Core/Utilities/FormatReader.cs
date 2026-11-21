using System.Buffers.Binary;
using MoonTTS.Domain.Audio;

namespace MoonTTS.Core.Utilities;

/* Utility for extracting audio format from WAV header */

public class FormatReader
{
    public static AudioFormat GetFormatFromWavHeader(byte[] wavBytes)
    {
        if (wavBytes == null || wavBytes.Length < 36)
            return new AudioFormat(24000, 1, 16);
        
        short channels = BitConverter.ToInt16(wavBytes, 22);
        int sampleRate = BitConverter.ToInt32(wavBytes, 24);
        short bitDepth = BitConverter.ToInt16(wavBytes, 34);

        return new AudioFormat(sampleRate, channels, bitDepth);
    }
    
    public static AudioFormat GetFormatFromWavHeader(ReadOnlySpan<byte> wavSpan)
    {
        if (wavSpan.Length < 36)
            return new AudioFormat(24000, 1, 16);
        
        short channels = BinaryPrimitives.ReadInt16LittleEndian(wavSpan.Slice(22, 2));
        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wavSpan.Slice(24, 4));
        short bitDepth = BinaryPrimitives.ReadInt16LittleEndian(wavSpan.Slice(34, 2));

        return new AudioFormat(sampleRate, channels, bitDepth);
    }
}