using System.Text.RegularExpressions;
using Chimpiler.Kb.Abstractions;
using Chimpiler.Kb.Models;

namespace Chimpiler.Kb.EntityExtraction;

/// <summary>
/// Conservative local entity extraction for names, organizations, and organization initialisms.
/// It deliberately emits candidate aliases instead of asserting identity.
/// </summary>
public sealed partial class RuleBasedEntityExtractor : IEntityExtractor
{
    private static readonly HashSet<string> OrganizationSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Arts", "Association", "Bank", "Company", "Corporation", "Foundation", "Group", "Inc",
        "Institute", "Labs", "Limited", "LLC", "Ltd", "Partners", "Systems", "Studios", "University"
    };

    public IReadOnlyList<KbEntityMention> Extract(string text)
    {
        var mentions = new Dictionary<string, KbEntityMention>(StringComparer.Ordinal);

        foreach (Match match in NameOrOrganizationRegex().Matches(text))
        {
            var surface = match.Value;
            var words = surface.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var kind = OrganizationSuffixes.Contains(words[^1]) ? NodeKinds.Organization : NodeKinds.Person;
            Add(mentions, kind, surface);
        }

        foreach (Match match in InitialismRegex().Matches(text))
        {
            Add(mentions, NodeKinds.Organization, match.Value);
        }

        return mentions.Values.ToList();
    }

    private static void Add(IDictionary<string, KbEntityMention> mentions, string kind, string surface)
    {
        var normalized = string.Join(
            ' ',
            surface.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(word => word.ToLowerInvariant()));
        var key = $"{kind}:{normalized}";
        mentions.TryAdd(key, new KbEntityMention(kind, surface, key));
    }

    [GeneratedRegex(@"\b(?:[A-Z][a-z]+)(?:\s+[A-Z][a-z]+){1,3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex NameOrOrganizationRegex();

    [GeneratedRegex(@"\b[A-Z][A-Z0-9]{1,9}\b", RegexOptions.CultureInvariant)]
    private static partial Regex InitialismRegex();
}
