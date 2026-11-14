using System.Numerics;
using System.Runtime.InteropServices;

namespace MoonTTS.Engine.Protocol;

/* File Structure:
        - ApplyDeClickingInPlace(Span<byte> pcmBytes, int sampleRate) : void
        - ConvertFloatToPcm16(ReadOnlySpan<float> floatSamples, Span<byte> destinationPcmBytes) : void
*/

/* Shared digital signal processing utilities for Moon-TTS engine workers */
public static class AudioDsp
{
    /* Smooths sample boundaries (3ms fade-in, 5ms fade-out) in-place to prevent speaker clicking */
    public static void ApplyDeClickingInPlace(Span<byte> pcmBytes, int sampleRate)
    {
        if (pcmBytes.Length < 4 || sampleRate <= 0)
            return;

        Span<short> samples = MemoryMarshal.Cast<byte, short>(pcmBytes);

        int fadeInSamples = Math.Min(sampleRate * 3 / 1000, samples.Length / 2);
        for (int i = 0; i < fadeInSamples; i++)
        {
            float factor = (float)i / fadeInSamples;
            samples[i] = (short)(samples[i] * factor);
        }

        int fadeOutSamples = Math.Min(sampleRate * 5 / 1000, samples.Length / 2);
        for (int i = 0; i < fadeOutSamples; i++)
        {
            float factor = (float)i / fadeOutSamples;
            int idx = samples.Length - 1 - i;
            samples[idx] = (short)(samples[idx] * factor);
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

            int limit = count - shortVecSize;
            for (; i <= limit; i += shortVecSize)
            {
                var vf0 = Vector.Min(Vector.Max(new Vector<float>(floatSamples.Slice(i, floatVecSize)), minOne), maxOne) * scale;
                var vf1 = Vector.Min(Vector.Max(new Vector<float>(floatSamples.Slice(i + floatVecSize, floatVecSize)), minOne), maxOne) * scale;

                Vector<int> vi0 = Vector.ConvertToInt32(vf0);
                Vector<int> vi1 = Vector.ConvertToInt32(vf1);
                Vector<short> packed = Vector.Narrow(vi0, vi1);
                packed.CopyTo(pcmSamples.Slice(i, shortVecSize));
            }
        }

        for (; i < count; i++)
        {
            float clamped = Math.Clamp(floatSamples[i], -1.0f, 1.0f);
            pcmSamples[i] = (short)Math.Round(clamped * 32767.0f);
        }
    }
}
