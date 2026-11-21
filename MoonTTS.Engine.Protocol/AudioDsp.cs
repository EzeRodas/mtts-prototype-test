using System.Numerics;
using System.Runtime.InteropServices;

namespace MoonTTS.Engine.Protocol;

/* File Structure:
        - ApplyDeClickingInPlace(Span<byte> pcmBytes, int sampleRate, int channels) : void
        - ConvertFloatToPcm16(ReadOnlySpan<float> floatSamples, Span<byte> destinationPcmBytes) : void
*/

/* Shared digital signal processing utilities for Moon-TTS engine workers */
public static class AudioDsp
{
    /* Smooths sample boundaries (3ms fade-in, 5ms fade-out) in-place across all channels to prevent speaker clicking */
    public static void ApplyDeClickingInPlace(Span<byte> pcmBytes, int sampleRate, int channels = 1)
    {
        int ch = Math.Max(channels, 1);
        if (pcmBytes.Length < 4 * ch || sampleRate <= 0)
            return;

        Span<short> samples = MemoryMarshal.Cast<byte, short>(pcmBytes);
        int totalFrames = samples.Length / ch;
        if (totalFrames < 2)
            return;

        int fadeInFrames = Math.Min(sampleRate * 3 / 1000, totalFrames / 2);
        for (int frame = 0; frame < fadeInFrames; frame++)
        {
            float factor = (float)frame / fadeInFrames;
            int baseIdx = frame * ch;
            for (int c = 0; c < ch; c++)
                samples[baseIdx + c] = (short)(samples[baseIdx + c] * factor);
        }

        int fadeOutFrames = Math.Min(sampleRate * 5 / 1000, totalFrames / 2);
        for (int frame = 0; frame < fadeOutFrames; frame++)
        {
            float factor = (float)frame / fadeOutFrames;
            int baseIdx = (totalFrames - 1 - frame) * ch;
            for (int c = 0; c < ch; c++)
                samples[baseIdx + c] = (short)(samples[baseIdx + c] * factor);
        }
    }

    /* Converts normalized float32 samples in [-1.0, 1.0] into 16-bit signed PCM bytes using SIMD when available */
    public static void ConvertFloatToPcm16(ReadOnlySpan<float> floatSamples, Span<byte> destinationPcmBytes)
    {
        if (floatSamples.IsEmpty)
            return;

        Span<short> pcmSamples = MemoryMarshal.Cast<byte, short>(destinationPcmBytes);
        int count = Math.Min(floatSamples.Length, pcmSamples.Length);
        int i = 0;

        if (Vector.IsHardwareAccelerated && count >= Vector<short>.Count)
        {
            int floatVecSize = Vector<float>.Count;
            int shortVecSize = Vector<short>.Count;
            var minOne = new Vector<float>(-1.0f);
            var maxOne = new Vector<float>(1.0f);
            var scale = new Vector<float>(32767.0f);
            var zero = Vector<float>.Zero;
            var posHalf = new Vector<float>(0.5f);
            var negHalf = new Vector<float>(-0.5f);

            int limit = count - shortVecSize;
            for (; i <= limit; i += shortVecSize)
            {
                var vf0 = Vector.Min(Vector.Max(new Vector<float>(floatSamples.Slice(i, floatVecSize)), minOne), maxOne) * scale;
                var vf1 = Vector.Min(Vector.Max(new Vector<float>(floatSamples.Slice(i + floatVecSize, floatVecSize)), minOne), maxOne) * scale;

                /* Round to nearest integer consistently with scalar path */
                vf0 += Vector.ConditionalSelect(Vector.GreaterThanOrEqual(vf0, zero), posHalf, negHalf);
                vf1 += Vector.ConditionalSelect(Vector.GreaterThanOrEqual(vf1, zero), posHalf, negHalf);

                Vector<int> vi0 = Vector.ConvertToInt32(vf0);
                Vector<int> vi1 = Vector.ConvertToInt32(vf1);
                Vector<short> packed = Vector.Narrow(vi0, vi1);
                packed.CopyTo(pcmSamples.Slice(i, shortVecSize));
            }
        }

        for (; i < count; i++)
        {
            float sample = floatSamples[i];
            if (float.IsNaN(sample))
            {
                pcmSamples[i] = 0;
                continue;
            }
            float clamped = Math.Clamp(sample, -1.0f, 1.0f);
            pcmSamples[i] = (short)MathF.Round(clamped * 32767.0f, MidpointRounding.AwayFromZero);
        }
    }
}
