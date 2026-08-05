namespace Chimpiler.Kb.Abstractions;

/// <summary>Converts text to model token ids without requiring Python.</summary>
public interface IKbTokenizer
{
    int CountTokens(string text);

    IReadOnlyList<int> Encode(string text, int maxTokens);
}
