using Chimpiler.Kb.Abstractions;
using Microsoft.ML.Tokenizers;

namespace Chimpiler.Kb.Embeddings;

/// <summary>WordPiece tokenizer loaded from a model's vocab.txt. Pure .NET, no Python required.</summary>
public sealed class BertKbTokenizer : IKbTokenizer
{
    private readonly BertTokenizer _tokenizer;

    private BertKbTokenizer(BertTokenizer tokenizer) => _tokenizer = tokenizer;

    /// <summary>Creates a tokenizer from a WordPiece vocabulary file.</summary>
    public static BertKbTokenizer FromVocabFile(string vocabPath)
    {
        using var stream = File.OpenRead(vocabPath);
        return new BertKbTokenizer(BertTokenizer.Create(stream));
    }

    public int CountTokens(string text) => _tokenizer.CountTokens(text);

    public IReadOnlyList<int> Encode(string text, int maxTokens)
    {
        var ids = _tokenizer.EncodeToIds(text, maxTokenCount: maxTokens, out _, out _);
        return ids;
    }

    /// <summary>Encodes text into padded input ids and an attention mask of length <paramref name="maxTokens"/>.</summary>
    public (long[] InputIds, long[] AttentionMask) EncodePadded(string text, int maxTokens)
    {
        var ids = Encode(text, maxTokens);
        var inputIds = new long[maxTokens];
        var attentionMask = new long[maxTokens];

        for (var i = 0; i < ids.Count; i++)
        {
            inputIds[i] = ids[i];
            attentionMask[i] = 1;
        }

        return (inputIds, attentionMask);
    }
}
