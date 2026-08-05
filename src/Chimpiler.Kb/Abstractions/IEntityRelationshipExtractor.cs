using Chimpiler.Kb.Models;

namespace Chimpiler.Kb.Abstractions;

/// <summary>Extracts conservative, typed relationships from entity mentions in a chunk.</summary>
public interface IEntityRelationshipExtractor
{
    IReadOnlyList<KbEntityRelationship> Extract(string text, IReadOnlyList<KbEntityMention> entities);
}
