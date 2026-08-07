namespace Chimpiler.Kb.Models;

/// <summary>A typed relationship supported by source text or supplied by an agent harness.</summary>
public sealed record KbEntityRelationship(
    string SubjectKey,
    string Predicate,
    string ObjectKey,
    string Evidence,
    string SourcePath,
    double Confidence = 1.0,
    string Provenance = "agent-asserted");
