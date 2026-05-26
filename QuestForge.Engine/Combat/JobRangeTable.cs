using QuestForge.Adapters.Types;

namespace QuestForge.Engine.Combat;

public enum CombatRole { Melee, PhysicalRanged, Caster, Healer, Tank, Unknown }

/// <summary>
/// Pure classifier: FFXIV ClassJob row id → combat role → attack range.
/// Keyed on JobId.Value (the ClassJob sheet row id).
/// All returned ranges are strictly less than the combat scan radius (30 m).
/// </summary>
public static class JobRangeTable
{
    public const float MeleeRange    = 3.0f;
    public const float RangedRange   = 20.0f;
    public const float FallbackRange = 3.0f;

    public static CombatRole Classify(JobId job) => job.Value switch
    {
        // Tank: GLA, MRD, PLD, WAR, DRK, GNB
        1u or 3u or 19u or 21u or 32u or 37u => CombatRole.Tank,

        // Melee DPS: PGL, LNC, MNK, DRG, ROG, NIN, SAM, RPR, VPR
        2u or 4u or 20u or 22u or 29u or 30u or 34u or 39u or 41u => CombatRole.Melee,

        // Physical Ranged: ARC, BRD, MCH, DNC
        5u or 23u or 31u or 38u => CombatRole.PhysicalRanged,

        // Caster: THM, BLM, ACN, SMN, RDM, BLU, PCT
        7u or 25u or 26u or 27u or 35u or 36u or 42u => CombatRole.Caster,

        // Healer: CNJ, WHM, SCH, AST, SGE
        6u or 24u or 28u or 33u or 40u => CombatRole.Healer,

        _ => CombatRole.Unknown
    };

    public static float AttackRange(JobId job) => Classify(job) switch
    {
        CombatRole.Tank            => MeleeRange,
        CombatRole.Melee           => MeleeRange,
        CombatRole.PhysicalRanged  => RangedRange,
        CombatRole.Caster          => RangedRange,
        CombatRole.Healer          => RangedRange,
        _                          => FallbackRange
    };
}
