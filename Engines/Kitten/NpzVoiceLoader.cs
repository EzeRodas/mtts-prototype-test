using System.IO.Compression;

namespace MoonTTS.Engine.Kitten;

/* File Structure:
        - Load(string npzFilePath) : Dictionary<string, float[]>
        - GetStyleVector(Dictionary<string, float[]> voices, string voiceKey, int tokenCount) : float[]
*/

/* Reads NumPy .npz voice embedding archives into in-memory float arrays */
public static class NpzVoiceLoader
{
    private const int EmbeddingDim = 256;

    /* Parses all (N, 256) .npy float matrices inside the .npz archive */
    public static Dictionary<string, float[]> Load(string npzFilePath)
    {
        var result = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(npzFilePath))
            throw new FileNotFoundException($"Voices NPZ file not found at '{npzFilePath}'.");

        using var zip = ZipFile.OpenRead(npzFilePath);
        foreach (var entry in zip.Entries)
        {
            if (!entry.Name.EndsWith(".npy", StringComparison.OrdinalIgnoreCase))
                continue;

            string key = Path.GetFileNameWithoutExtension(entry.Name);
            using var stream = entry.Open();

            byte[] buffer = new byte[entry.Length];
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (read == 0) break;
                totalRead += read;
            }

            if (buffer.Length < 10)
                continue;

            ushort headerLen = BitConverter.ToUInt16(buffer, 8);
            int dataOffset = 10 + headerLen;

            int floatCount = (buffer.Length - dataOffset) / sizeof(float);
            float[] floats = new float[floatCount];
            Buffer.BlockCopy(buffer, dataOffset, floats, 0, floatCount * sizeof(float));

            result[key] = floats;
        }

        return result;
    }

    /* Slices a 256-dimensional style vector at Clamp(tokenCount - 1, 0, packRows - 1) matching KittenTTS 2.0 */
    public static float[] GetStyleVector(Dictionary<string, float[]> voices, string voiceKey, int tokenCount)
    {
        if (!voices.TryGetValue(voiceKey, out float[]? matrix) || matrix == null || matrix.Length < EmbeddingDim)
        {
            matrix = voices.Values.FirstOrDefault();
            if (matrix == null || matrix.Length < EmbeddingDim)
                return new float[EmbeddingDim];
        }

        int maxIndex = (matrix.Length / EmbeddingDim) - 1;
        int refId = Math.Clamp(tokenCount - 1, 0, maxIndex);

        float[] style = new float[EmbeddingDim];
        Array.Copy(matrix, refId * EmbeddingDim, style, 0, EmbeddingDim);
        return style;
    }
}
