using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

// ─────────────────────────────────────────────────────────────────────────────
// Slice A (RED): StepFactory "combat" case nibble-level tests (GWT-F1'..F3').
//
// DELETED (byte-level, obsolete):
//   GWT-F1  — KillCorrelatedTargets keyed by int/varIndex (replaced by NibbleKey)
//   GWT-F2  — same key change; also the expect string changed from questVariable→nibble
//   (GWT-F2 kept for fallback position test, renamed F2')
//
// NEW:
//   GWT-F3' — single dominant NibbleKey snapshot → KillEnemyDataIds is that key's DataIds only.
//
// These compile-fail on:
//   NibbleKey, NibbleHalf, IReadOnlyDictionary<NibbleKey, KillCorrelation>
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
    // GWT-F1' — builds CombatStep with nibble expect, OverworldEnemies, combat-start Location.
    //
    // Snapshot: [(0,Low)]=([347,348],3), CombatStartZone=148, CombatStartPosition=(10,0,20).
    // Build("combat","defeat-347","questVariableLow(65847,0) >= 3", after).
    // Then:
    //   - result is CombatStep
    //   - KillEnemyDataIds contains 347 and 348 (sorted/distinct)
    //   - Spawn == CombatSpawn.OverworldEnemies
    //   - Location.Zone == 148
    //   - Location.Position == (10,0,20)
    //   - Expect is PredicateExpect with Predicate=="questVariableLow(65847,0) >= 3"
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtF1_BuildCombatStep_NibbleExpect_OverworldSpawn_LocationFromCombatStart()
    {
        var after = MakeAfterSnapshot(
            killCorrelatedTargets: new Dictionary<NibbleKey, KillCorrelation>
            {
                [new NibbleKey(0, NibbleHalf.Low)] = new KillCorrelation(
                    DataIds: new uint[] { 347u, 348u },
                    FinalValue: 3)
            },
            combatStartZone: 148,
            combatStartPosition: new WorldPosition(10, 0, 20));

        var step = StepFactory.Build(
            stepType: "combat",
            stepId:   "defeat-347",
            expect:   "questVariableLow(65847,0) >= 3",
            after:    after);

        Assert.IsType<CombatStep>(step);
        var cs = (CombatStep)step;

        Assert.Equal("defeat-347", cs.Id);
        Assert.Contains(347u, cs.KillEnemyDataIds);
        Assert.Contains(348u, cs.KillEnemyDataIds);

        Assert.Equal(CombatSpawn.OverworldEnemies, cs.Spawn);

        Assert.NotNull(cs.Location);
        Assert.Equal(148, cs.Location!.Zone);
        Assert.Equal(10f, cs.Location.Position.X);
        Assert.Equal(0f,  cs.Location.Position.Y);
        Assert.Equal(20f, cs.Location.Position.Z);

        var pe = Assert.IsType<PredicateExpect>(cs.Expect);
        Assert.Equal("questVariableLow(65847,0) >= 3", pe.Predicate);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-F2' — missing CombatStartPosition falls back to player position.
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
    // GWT-F3' — single dominant NibbleKey → KillEnemyDataIds is that key's DataIds only.
    //
    // The inference/extractor passes a snapshot with ONLY the dominant key (per-record
    // scoping). The factory's union read over a single-key dict is exact: no residue.
    //
    // Snapshot: ONLY [(0,Low)]=([347],3).
    // Then KillEnemyDataIds == [347] (not any other DataId, not an empty list).
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtF3_SingleDominantNibbleKey_KillEnemyDataIds_IsThatKeysDataIdsOnly()
    {
        var after = MakeAfterSnapshot(
            killCorrelatedTargets: new Dictionary<NibbleKey, KillCorrelation>
            {
                [new NibbleKey(0, NibbleHalf.Low)] = new KillCorrelation(
                    DataIds: new uint[] { 347u },
                    FinalValue: 3)
            },
            combatStartZone: 148,
            combatStartPosition: new WorldPosition(10, 0, 20));

        var step = StepFactory.Build(
            stepType: "combat",
            stepId:   "defeat-347",
            expect:   "questVariableLow(65847,0) >= 3",
            after:    after);

        Assert.IsType<CombatStep>(step);
        var cs = (CombatStep)step;

        // Exactly one DataId, exactly 347u — no residue from other keys
        Assert.Single(cs.KillEnemyDataIds);
        Assert.Equal(347u, cs.KillEnemyDataIds[0]);
    }
}
