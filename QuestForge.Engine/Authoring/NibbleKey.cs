namespace QuestForge.Engine.Authoring;

public enum NibbleHalf { Low, High }

/// <summary>The specific nibble (low/high of a variable byte) a set of kills incremented.</summary>
public readonly record struct NibbleKey(int VarIndex, NibbleHalf Half);
