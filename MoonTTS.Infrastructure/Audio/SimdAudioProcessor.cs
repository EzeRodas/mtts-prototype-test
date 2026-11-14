using System.Numerics;
using System.Runtime.InteropServices;

namespace MoonTTS.Infrastructure.Audio;

/* File Structure:
        - ScaleVolumeInPlace(Span<byte> pcmBytes, float volume) : void
*/

/* Audio digital signal processing routines operating directly on raw PCM memory spans */
public static class SimdAudioProcessor
{
    /* Scales 16-bit PCM samples by a volume factor in-place using hardware SIMD vectors when available */
    public static void ScaleVolumeInPlace(Span<byte> pcmBytes, float volume)
    {
        if (pcmBytes.IsEmpty || Math.Abs(volume - 1.0f) < 0.001f)
            return;

        float clampedVol = Math.Clamp(volume, 0f, 2.0f);
        Span<short> samples = MemoryMarshal.Cast<byte, short>(pcmBytes);
        int i = 0;

        if (Vector.IsHardwareAccelerated && samples.Length >= Vector<short>.Count)
        {
            int vecSize = Vector<short>.Count;
            int q15Scale = (int)MathF.Round(clampedVol * 32768.0f);
            var scaleVec = new Vector<int>(q15Scale);
            var minShort = new Vector<int>(short.MinValue);
            var maxShort = new Vector<int>(short.MaxValue);

            int limit = samples.Length - vecSize;
            for (; i <= limit; i += vecSize)
            {
                var vec = new Vector<short>(samples.Slice(i, vecSize));
                Vector.Widen(vec, out Vector<int> low, out Vector<int> high);

                low = Vector.Min(Vector.Max(Vector.ShiftRightArithmetic(low * scaleVec, 15), minShort), maxShort);
                high = Vector.Min(Vector.Max(Vector.ShiftRightArithmetic(high * scaleVec, 15), minShort), maxShort);

                Vector<short> narrowed = Vector.Narrow(low, high);
                narrowed.CopyTo(samples.Slice(i, vecSize));
            }
        }

        for (; i < samples.Length; i++)
        {
            samples[i] = (short)Math.Clamp((int)(samples[i] * clampedVol), short.MinValue, short.MaxValue);
        }
    }
}