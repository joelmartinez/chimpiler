using Chimpiler.Kb.Models;

namespace Chimpiler.Kb.EntityExtraction;

/// <summary>Finds conservative, explainable candidate aliases between extracted entities.</summary>
public static class EntityAliasResolver
{
    private static readonly IReadOnlyDictionary<string, string> GivenNameRoots =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bob"] = "robert",
            ["bobby"] = "robert",
            ["rob"] = "robert",
            ["bill"] = "william",
            ["will"] = "william",
            ["jim"] = "james",
            ["jimmy"] = "james",
            ["mike"] = "michael",
            ["dave"] = "david",
            ["liz"] = "elizabeth",
            ["beth"] = "elizabeth"
        };

    /// <summary>Returns a non-identity confidence for an alias candidate, or null when unrelated.</summary>
    public static double? GetCandidateConfidence(KbEntityMention mention, KbNode candidate)
    {
        if (!TryParse(candidate.Key, out var candidateKind, out var candidateName) ||
            !string.Equals(mention.Kind, candidateKind, StringComparison.Ordinal))
        {
            return null;
        }

        var mentionName = mention.Key[(mention.Kind.Length + 1)..];
        return mention.Kind switch
        {
            NodeKinds.Person when IsNicknameVariant(mentionName, candidateName) => 0.8,
            NodeKinds.Organization when IsInitialismOf(mentionName, candidateName) => 0.75,
            _ => null
        };
    }

    private static bool IsNicknameVariant(string left, string right)
    {
        var leftParts = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return leftParts.Length is >= 2 and <= 4 &&
               leftParts.Length == rightParts.Length &&
               leftParts[1..].SequenceEqual(rightParts[1..], StringComparer.Ordinal) &&
               !string.Equals(leftParts[0], rightParts[0], StringComparison.Ordinal) &&
               RootGivenName(leftParts[0]) == RootGivenName(rightParts[0]);
    }

    private static bool IsInitialismOf(string left, string right) =>
        IsInitialism(left, right) || IsInitialism(right, left);

    private static bool IsInitialism(string possibleInitialism, string possibleFullName)
    {
        if (possibleInitialism.Contains(' ') || !possibleFullName.Contains(' '))
        {
            return false;
        }

        var words = possibleFullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 2 &&
               string.Equals(
                   possibleInitialism,
                   string.Concat(words.Select(word => char.ToUpperInvariant(word[0]))).ToLowerInvariant(),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string RootGivenName(string name) =>
        GivenNameRoots.TryGetValue(name, out var root) ? root : name.ToLowerInvariant();

    private static bool TryParse(string key, out string kind, out string name)
    {
        var separator = key.IndexOf(':');
        if (separator <= 0 || separator == key.Length - 1)
        {
            kind = string.Empty;
            name = string.Empty;
            return false;
        }

        kind = key[..separator];
        name = key[(separator + 1)..];
        return true;
    }
}
