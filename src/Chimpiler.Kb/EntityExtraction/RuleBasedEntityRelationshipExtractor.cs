using System.Text.RegularExpressions;
using Chimpiler.Kb.Abstractions;
using Chimpiler.Kb.Models;

namespace Chimpiler.Kb.EntityExtraction;

/// <summary>Extracts explicit two-entity action statements without inferring unstated facts.</summary>
public sealed partial class RuleBasedEntityRelationshipExtractor : IEntityRelationshipExtractor
{
    public IReadOnlyList<KbEntityRelationship> Extract(string text, IReadOnlyList<KbEntityMention> entities)
    {
        var relationships = new List<KbEntityRelationship>();
        foreach (Match sentenceMatch in SentenceRegex().Matches(text))
        {
            var sentence = sentenceMatch.Value;
            var sentenceEntities = entities
                .Where(entity => sentence.Contains(entity.Surface, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var subject in sentenceEntities)
            {
                var subjectIndex = sentence.IndexOf(subject.Surface, StringComparison.OrdinalIgnoreCase);
                var action = ActionRegex().Match(sentence, subjectIndex + subject.Surface.Length);
                if (!action.Success)
                {
                    continue;
                }

                foreach (var target in sentenceEntities.Where(entity => entity.Key != subject.Key))
                {
                    var targetIndex = sentence.IndexOf(target.Surface, action.Index + action.Length, StringComparison.OrdinalIgnoreCase);
                    if (targetIndex <= action.Index)
                    {
                        continue;
                    }

                    relationships.Add(new KbEntityRelationship(
                        subject.Key,
                        NormalizePredicate(action.Value),
                        target.Key,
                        sentence.Trim()));
                }
            }
        }

        return relationships
            .DistinctBy(relationship => (relationship.SubjectKey, relationship.Predicate, relationship.ObjectKey, relationship.Evidence))
            .ToList();
    }

    private static string NormalizePredicate(string value) =>
        value.Trim().ToLowerInvariant().Replace(' ', '-');

    [GeneratedRegex(@"[^.!?\r\n]+[.!?]?", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceRegex();

    [GeneratedRegex(@"\b(approved|authorized|appointed|assigned|acquired|announced|built|cancelled|created|denied|funded|hired|launched|licensed|managed|merged|partnered|promoted|purchased|released|reported|requested|signed|sold|transferred|updated|worked with)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActionRegex();
}
