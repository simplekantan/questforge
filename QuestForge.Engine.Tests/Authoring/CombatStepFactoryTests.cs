using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

// ─────────────────────────────────────────────────────────────────────────────
// Slice A: StepFactory "combat" case tests (GWT-F1..F3).
//
// These rewrite GWT-F1'..F3' to match the §6.3 specs in COMBAT_TARGET_ATTRIBUTION_PLAN.md.
//
// The factory shape is UNCHANGED (D7) — the tests verify that the factory correctly reads
// KillCorrelatedTargets whose DataIds come from the span target-set (not kills).
//
// Factory behaviour is unchanged, so GWT-F1..F3 compile and run against the existing
// StepFactory implementation; they should be BEHAVIORALLY GREEN already (factory unchanged).
// They are included here for completeness of the PR-A test suite and to confirm no
// regression in the factory during the builder's aggregator rework.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class CombatStepFactoryTests
{
    private static readonly QuestId Quest65847 = new(65847);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ─── Snapshot builder ────────────────────────────────────────────────────

    private static GameStateSnapshot MakeAfterSnapshot(
        IReadOnlyDictionary<NibbleKey, KillCorrelation>? killCorrelatedTargets = null,
        int combatStartZone = 0,
        WorldPosition? combatStartPosition = null,
        WorldPosition? position = null) =>
        new(
            CapturedAt:         T0,
            Zone:               new ZoneId((uint)(combatStartZone > 0 ? combatStartZone : 100)),
            Position:           position ?? new WorldPosition(5, 0, 5),
            ActiveQuest:        Quest65847,
            QuestSequence:      1,
            QuestFlags:         0,
            QuestAccepted:      false,
            QuestCompleted:     false,
            LastNpcInteracted:  null,
            LastNpcPosition:    null,
            LastDialoguePrompt: null,
            LastDialogueAnswer: null,
            InventoryHash:      0,
            LastAttuned:        null)
        {
            KillCorrelatedTargets = killCorrelatedTargets,
            CombatStartZone       = combatStartZone,
            CombatStartPosition   = combatStartPosition,
        };

    private static string FormatStep(Step step)
        => step is CombatStep cs
            ? $"CombatStep Id={cs.Id}, DataIds=[{string.Join(",", cs.KillEnemyDataIds)}], Spawn={cs.Spawn}, " +
              $"Loc.Zone={cs.Location?.Zone}, Loc.Pos=({cs.Location?.Position.X},{cs.Location?.Position.Y},{cs.Location?.Position.Z}), Expect={cs.Expect}"
            : $"Unexpected step type {step.GetType().Name}";

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-F1 builds CombatStep with nibble expect, OverworldEnemies, combat-start Location.
    //
    // Snapshot: [(1,High)]=([338],3), CombatStartZone=148, CombatStartPosition=(10,0,20).
    // Build("combat","defeat-338","questVariableHigh(65847,1) >= 3", after).
    // Then:
    //   - result is CombatStep
    //   - KillEnemyDataIds == [338]
    //   - Spawn == CombatSpawn.OverworldEnemies
    //   - Location.Zone == 148
    //   - Location.Position == (10,0,20)
    //   - Expect is PredicateExpect with Predicate == "questVariableHigh(65847,1) >= 3"
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtF1_BuildCombatStep_NibbleExpect_OverworldSpawn_LocationFromCombatStart()
    {
        var after = MakeAfterSnapshot(
            killCorrelatedTargets: new Dictionary<NibbleKey, KillCorrelation>
            {
                [new NibbleKey(1, NibbleHalf.High)] = new KillCorrelation(
                    DataIds: new uint[] { 338u },
                    FinalValue: 3)
            },
            combatStartZone: 148,
            combatStartPosition: new WorldPosition(10, 0, 20));

        var step = StepFactory.Build(
            stepType: "combat",
            stepId:   "defeat-338",
            expect:   "questVariableHigh(65847,1) >= 3",
            after:    after);

        Assert.IsType<CombatStep>(step);
        var cs = (CombatStep)step;

        Assert.Equal("defeat-338", cs.Id);
        Assert.Single(cs.KillEnemyDataIds);
        Assert.Contains(338u, cs.KillEnemyDataIds);

        Assert.Equal(CombatSpawn.OverworldEnemies, cs.Spawn);

        Assert.NotNull(cs.Location);
        Assert.Equal(148, cs.Location!.Zone);
        Assert.Equal(10f, cs.Location.Position.X);
        Assert.Equal(0f,  cs.Location.Position.Y);
        Assert.Equal(20f, cs.Location.Position.Z);

        var pe = Assert.IsType<PredicateExpect>(cs.Expect);
        Assert.Equal("questVariableHigh(65847,1) >= 3", pe.Predicate);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-F2 missing CombatStartPosition falls back to player position.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtF2_MissingCombatStartPosition_FallsBackToPlayerPosition()
    {
        var after = MakeAfterSnapshot(
            killCorrelatedTargets: new Dictionary<NibbleKey, KillCorrelation>
            {
                [new NibbleKey(0, NibbleHalf.Low)] = new KillCorrelation(
                    DataIds: new uint[] { 347u },
                    FinalValue: 1)
            },
            combatStartZone: 100,
            combatStartPosition: null,
            position: new WorldPosition(5, 0, 5));

        var step = StepFactory.Build(
            stepType: "combat",
            stepId:   "defeat-347",
            expect:   "questVariableLow(65847,0) >= 1",
            after:    after);

        Assert.IsType<CombatStep>(step);
        var cs = (CombatStep)step;

        Assert.NotNull(cs.Location);
        Assert.Equal(5f, cs.Location!.Position.X);
        Assert.Equal(0f, cs.Location.Position.Y);
        Assert.Equal(5f, cs.Location.Position.Z);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-F3 single-target single-nibble snapshot → kill-set is that target only.
    //
    // Snapshot contains ONLY [(1,High)]=([338],3).
    // Then KillEnemyDataIds == [338] exactly — no residue from other keys.
    // Pins that the factory's union read over a single-key dict is exact.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtF3_SingleTargetSingleNibble_KillEnemyDataIds_IsThatTargetOnly()
    {
        var after = MakeAfterSnapshot(
            killCorrelatedTargets: new Dictionary<NibbleKey, KillCorrelation>
            {
                [new NibbleKey(1, NibbleHalf.High)] = new KillCorrelation(
                    DataIds: new uint[] { 338u },
                    FinalValue: 3)
            },
            combatStartZone: 148,
            combatStartPosition: new WorldPosition(10, 0, 20));

        var step = StepFactory.Build(
            stepType: "combat",
            stepId:   "defeat-338",
            expect:   "questVariableHigh(65847,1) >= 3",
            after:    after);

        Assert.IsType<CombatStep>(step);
        var cs = (CombatStep)step;

        // Exactly one DataId, exactly 338u — no residue
        Assert.Single(cs.KillEnemyDataIds);
        Assert.Equal(338u, cs.KillEnemyDataIds[0]);
    }
}
