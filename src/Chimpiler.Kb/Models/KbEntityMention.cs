namespace Chimpiler.Kb.Models;

/// <summary>A locally extracted entity mention with its normalized graph key.</summary>
public sealed record KbEntityMention(string Kind, string Surface, string Key);
