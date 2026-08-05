using Chimpiler.Kb.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Chimpiler.Kb.Embeddings;

/// <summary>
/// Runs a local ONNX sentence-embedding model (e.g. BAAI/bge-small-en-v1.5) with mean pooling
/// and L2 normalisation. Requires no Python and no network access once the model is installed.
/// </summary>
public sealed class OnnxEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private const int MaxSequenceLength = 512;

    private readonly InferenceSession _session;
    private readonly BertKbTokenizer _tokenizer;
    private readonly bool _needsTokenTypeIds;

    public OnnxEmbeddingProvider(string modelId, string modelPath, string vocabPath, int dimension)
    {
        Name = modelId;
        Dimension = dimension;
        _session = new InferenceSession(modelPath);
        _tokenizer = BertKbTokenizer.FromVocabFile(vocabPath);
        _needsTokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");
    }

    public string Name { get; }

    public int Dimension { get; }

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        var vectors = new List<float[]>(texts.Count);
        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            vectors.Add(Embed(text));
        }

        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }

    private float[] Embed(string text)
    {
        var tokenCount = Math.Clamp(_tokenizer.Encode(text, MaxSequenceLength).Count, 1, MaxSequenceLength);
        var (inputIds, attentionMask) = _tokenizer.EncodePadded(text, tokenCount);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, new[] { 1, tokenCount })),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, new[] { 1, tokenCount }))
        };

        if (_needsTokenTypeIds)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                "token_type_ids",
                new DenseTensor<long>(new long[tokenCount], new[] { 1, tokenCount })));
        }

        using var results = _session.Run(inputs);
        var output = results.First().AsTensor<float>();
        return Normalize(MeanPool(output, attentionMask, tokenCount));
    }

    private static float[] MeanPool(Tensor<float> hiddenStates, long[] attentionMask, int tokenCount)
    {
        var hiddenSize = hiddenStates.Dimensions[^1];
        var pooled = new float[hiddenSize];
        var counted = 0;

        for (var token = 0; token < tokenCount; token++)
        {
            if (attentionMask[token] == 0)
            {
                continue;
            }

            counted++;
            for (var h = 0; h < hiddenSize; h++)
            {
                pooled[h] += hiddenStates[0, token, h];
            }
        }

        if (counted > 0)
        {
            for (var h = 0; h < hiddenSize; h++)
            {
                pooled[h] /= counted;
            }
        }

        return pooled;
    }

    private static float[] Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector)
        {
            sum += (double)value * value;
        }

        var norm = Math.Sqrt(sum);
        if (norm <= 0)
        {
            return vector;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }

        return vector;
    }

    public void Dispose() => _session.Dispose();
}
