using Chimpiler.Kb.Models;

namespace Chimpiler.Kb.Abstractions;

/// <summary>Extracts locally recognizable entity mentions from chunk text.</summary>
public interface IEntityExtractor
{
    IReadOnlyList<KbEntityMention> Extract(string text);
}
