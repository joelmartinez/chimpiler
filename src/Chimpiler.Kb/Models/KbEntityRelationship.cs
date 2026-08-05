namespace Chimpiler.Kb.Models;

/// <summary>A typed relationship supported by source text or supplied by an agent harness.</summary>
public sealed record KbEntityRelationship(
    string SubjectKey,
    string Predicate,
    string ObjectKey,
    string Evidence,
    double Confidence = 1.0,
    string Provenance = "local-extraction");
