using System.Buffers.Binary;

namespace Chimpiler.Kb.Storage;

/// <summary>Encodes and decodes embedding vectors as little-endian float32 blobs.</summary>
public static class VectorCodec
{
    public static byte[] Encode(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    public static float[] Decode(byte[] bytes, int dimension)
    {
        var vector = new float[dimension];
        for (var i = 0; i < dimension; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        }

        return vector;
    }

    public static double Norm(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector)
        {
            sum += (double)value * value;
        }

        return Math.Sqrt(sum);
    }

    /// <summary>Cosine similarity using pre-computed norms; returns 0 when either vector is degenerate.</summary>
    public static double CosineSimilarity(float[] left, double leftNorm, float[] right, double rightNorm)
    {
        if (leftNorm <= 0 || rightNorm <= 0 || left.Length != right.Length)
        {
            return 0;
        }

        double dot = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += (double)left[i] * right[i];
        }

        return dot / (leftNorm * rightNorm);
    }
}
