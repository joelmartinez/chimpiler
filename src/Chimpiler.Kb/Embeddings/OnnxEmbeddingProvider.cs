using Chimpiler.Kb.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Chimpiler.Kb.Embeddings;

/// <summary>
/// Runs a local ONNX sentence-embedding model with its model-specific tokenizer, pooling,
/// sequence limit, and document/query prefixes. Requires no Python once the model is installed.
/// </summary>
public sealed class OnnxEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private const int MaximumBatchSize = 32;

    private readonly InferenceSession _session;
    private readonly BertKbTokenizer _tokenizer;
    private readonly bool _needsTokenTypeIds;
    private readonly EmbeddingModelDescriptor _model;

    public OnnxEmbeddingProvider(EmbeddingModelDescriptor model, string modelPath, string vocabPath)
    {
        _model = model;
        Name = model.Id;
        Dimension = model.Dimension;
        _session = new InferenceSession(modelPath);
        _tokenizer = BertKbTokenizer.FromVocabFile(vocabPath);
        _needsTokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");
    }

    public string Name { get; }

    public int Dimension { get; }

    public IKbTokenizer Tokenizer => _tokenizer;

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        => EmbedDocumentsAsync(texts, cancellationToken);

    public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        EmbedAsyncCore(texts, _model.DocumentPrefix, cancellationToken);

    public Task<IReadOnlyList<float[]>> EmbedQueriesAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        EmbedAsyncCore(texts, _model.QueryPrefix, cancellationToken);

    private Task<IReadOnlyList<float[]>> EmbedAsyncCore(
        IReadOnlyList<string> texts,
        string? prefix,
        CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<float[]>>(Array.Empty<float[]>());
        }

        var encoded = texts
            .Select(text => _tokenizer.Encode(ApplyPrefix(prefix, text), _model.MaximumSequenceLength))
            .ToList();
        var vectors = new List<float[]>(texts.Count);
        foreach (var batch in encoded.Chunk(MaximumBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            vectors.AddRange(EmbedBatch(batch));
        }

        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }

    private IReadOnlyList<float[]> EmbedBatch(IReadOnlyList<IReadOnlyList<int>> encoded)
    {
        var sequenceLength = Math.Max(1, encoded.Max(ids => ids.Count));
        var inputIds = new long[encoded.Count * sequenceLength];
        var attentionMask = new long[encoded.Count * sequenceLength];

        for (var batch = 0; batch < encoded.Count; batch++)
        {
            for (var token = 0; token < encoded[batch].Count; token++)
            {
                var offset = (batch * sequenceLength) + token;
                inputIds[offset] = encoded[batch][token];
                attentionMask[offset] = 1;
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, new[] { encoded.Count, sequenceLength })),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, new[] { encoded.Count, sequenceLength }))
        };

        if (_needsTokenTypeIds)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                "token_type_ids",
                new DenseTensor<long>(new long[encoded.Count * sequenceLength], new[] { encoded.Count, sequenceLength })));
        }

        using var results = _session.Run(inputs);
        var output = results.First().AsTensor<float>();
        var vectors = new List<float[]>(encoded.Count);
        for (var batch = 0; batch < encoded.Count; batch++)
        {
            vectors.Add(Normalize(_model.Pooling switch
            {
                EmbeddingPooling.Cls => ClsPool(output, batch),
                EmbeddingPooling.Mean => MeanPool(output, attentionMask, batch, sequenceLength),
                _ => throw new InvalidOperationException($"Unsupported pooling mode '{_model.Pooling}'.")
            }));
        }

        return vectors;
    }

    private static string ApplyPrefix(string? prefix, string text) =>
        string.IsNullOrEmpty(prefix) ? text : prefix + text;

    private static float[] ClsPool(Tensor<float> hiddenStates, int batch)
    {
        var hiddenSize = hiddenStates.Dimensions[^1];
        var pooled = new float[hiddenSize];
        for (var h = 0; h < hiddenSize; h++)
        {
            pooled[h] = hiddenStates[batch, 0, h];
        }

        return pooled;
    }

    private static float[] MeanPool(Tensor<float> hiddenStates, long[] attentionMask, int batch, int sequenceLength)
    {
        var hiddenSize = hiddenStates.Dimensions[^1];
        var pooled = new float[hiddenSize];
        var counted = 0;

        for (var token = 0; token < sequenceLength; token++)
        {
            if (attentionMask[(batch * sequenceLength) + token] == 0)
            {
                continue;
            }

            counted++;
            for (var h = 0; h < hiddenSize; h++)
            {
                pooled[h] += hiddenStates[batch, token, h];
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
