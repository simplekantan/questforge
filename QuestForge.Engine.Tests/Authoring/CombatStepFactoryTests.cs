using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

// ─────────────────────────────────────────────────────────────────────────────
// RED PHASE — Slice 2: StepFactory "combat" case tests (GWT-F1..F2).
//
// These tests WILL FAIL because the following do not exist yet:
//   1. StepFactory.Build does not handle stepType="combat"
//   2. KillCorrelation record / GameStateSnapshot.KillCorrelatedTargets
//   3. GameStateSnapshot.CombatStartPosition / CombatStartZone
//   4. CombatStep is already in Schema — the factory case just does not exist yet
//
// Builder: add the "combat" arm to StepFactory.Build. Then F1..F2 go green.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class CombatStepFactoryTests
{
    private static readonly QuestId Quest65847 = new(65847);

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ─── Snapshot builder ────────────────────────────────────────────────────

    private static GameStateSnapshot MakeAfterSnapshot(
        IReadOnlyDictionary<int, KillCorrelation>? killCorrelatedTargets = null,
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
            // Slice-2 non-positional fields (RED until GameStateSnapshot is extended)
            KillCorrelatedTargets = killCorrelatedTargets,
            CombatStartZone       = combatStartZone,
            CombatStartPosition   = combatStartPosition,
        };

    private static string FormatStep(Step step)
        => step is CombatStep cs
            ? $"CombatStep Id={cs.Id}, DataIds=[{string.Join(",", cs.KillEnemyDataIds)}], Spawn={cs.Spawn}, Zone={cs.Zone}, Loc.Zone={cs.Location?.Zone}, Loc.Pos=({cs.Location?.Position.X},{cs.Location?.Position.Y},{cs.Location?.Position.Z}), Expect={cs.Expect}"
            : $"Unexpected step type {step.GetType().Name}";

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-F1 — builds CombatStep with union data-ids + default OverworldEnemies spawn
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_F1_BuildCombatStep_UnionDataIds_OverworldSpawn_LocationFromCombatStart()
    {
        /*
         * RED: StepFactory does not have a "combat" case; KillCorrelation / snapshot fields missing.
         *
         * Given snapshot with KillCorrelatedTargets[0]=([347,348],3),
         *       CombatStartZone=148, CombatStartPosition=(10,0,20).
         * When Build("combat", "defeat-347", "questVariable(65847,0) >= 3", after).
         * Then:
         *   - result is CombatStep (not a fallback TalkStep)
         *   - KillEnemyDataIds contains 347 and 348 (sorted/distinct)
         *   - Spawn == CombatSpawn.OverworldEnemies (D5 default)
         *   - Location.Zone == 148 (CombatStartZone)
         *   - Location.Position == (10,0,20) (CombatStartPosition)
         *   - Expect is a PredicateExpect (not null)
         */

        var after = MakeAfterSnapshot(
            killCorrelatedTargets: new Dictionary<int, KillCorrelation>
            {
                [0] = new KillCorrelation(DataIds: new uint[] { 347u, 348u }, FinalValue: 3)
            },
            combatStartZone: 148,
            combatStartPosition: new WorldPosition(10, 0, 20));

        var step = StepFactory.Build(
            stepType: "combat",
            stepId:   "defeat-347",
            expect:   "questVariable(65847,0) >= 3",
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

        Assert.IsType<PredicateExpect>(cs.Expect);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-F2 — missing CombatStartPosition falls back to player position
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_F2_MissingCombatStartPosition_FallsBackToPlayerPosition()
    {
        /*
         * RED: same missing members as GWT-F1.
         *
         * Given CombatStartPosition==null, player Position=(5,0,5).
         * When Build("combat", ...).
         * Then Location.Position == (5,0,5) (D6 fallback to after.Position).
         */

        var after = MakeAfterSnapshot(
            killCorrelatedTargets: new Dictionary<int, KillCorrelation>
            {
                [0] = new KillCorrelation(DataIds: new uint[] { 347u }, FinalValue: 1)
            },
            combatStartZone: 100,
            combatStartPosition: null,    // <-- no combat-start position captured
            position: new WorldPosition(5, 0, 5));

        var step = StepFactory.Build(
            stepType: "combat",
            stepId:   "defeat-347",
            expect:   "questVariable(65847,0) >= 1",
            after:    after);

        Assert.IsType<CombatStep>(step);
        var cs = (CombatStep)step;

        Assert.NotNull(cs.Location);
        Assert.Equal(5f, cs.Location!.Position.X);
        Assert.Equal(0f, cs.Location.Position.Y);
        Assert.Equal(5f, cs.Location.Position.Z);
    }
}
