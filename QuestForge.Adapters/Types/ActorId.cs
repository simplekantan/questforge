namespace QuestForge.Adapters.Types;

/// <summary>
/// Live game-object identity — the specific instance of an actor in the scene.
/// Distinct from NpcId (a BNpc base data-id used in schema/dialogue).
/// </summary>
public readonly record struct ActorId(ulong Value);
