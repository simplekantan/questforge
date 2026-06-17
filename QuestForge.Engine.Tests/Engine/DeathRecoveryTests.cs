using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using QuestId = QuestForge.Adapters.Types.QuestId;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// RED PHASE: Engine tests for open-world death recovery teleport.
/// DEATH_RECOVERY_PLAN.md -- test scenarios D1-D6.
///
/// ALL tests will fail at runtime until Builder adds:
///   - bool _wasDeadLastTick and bool _deathRecoveryPending fields to QuestEngine
///   - dead-to-alive edge detection after playerZone read
///   - condition 4a (zone satisfied clears flag) and 4b (death-recovery teleport)
///   - flag clearing in BeginRun and sequence change
///   - flag clearing on step confirmation
/// </summary>
public sealed class DeathRecoveryTests : IDisposable
{
    private static readonly IReadOnlyDictionary<uint, uint> TestCorpusDefaults =
        new Dictionary<uint, uint> { { 1000u, 130u }, { 8u, 129u } };

    public DeathRecoveryTests() =>
        QuestForge.Engine.Travel.AetheryteZoneMap.Populate(TestCorpusDefaults);

    public void Dispose() =>
        QuestForge.Engine.Travel.AetheryteZoneMap.Populate(TestCorpusDefaults);

    // =========================================================================
    // Helpers
    // =========================================================================

    private const uint BaseQuestId = 92000;

    /// <summary>
    /// Builds a minimal 1-sequence quest with the given steps.
    /// Mirrors ResumePointTests.BuildQuest.
    /// </summary>
    private static QuestDefinition BuildQuest(uint questId, params Step[] steps) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "Death Recovery Test Quest",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences =
            [
                new QuestSequence { Sequence = 0, Steps = steps }
            ]
        };

    /// <summary>
    /// A TalkStep with RequiredZone set but NO ResumePointFragmentId.
    /// NPC is far from player's default position -- will always Navigate unless already near.
    /// Expect is always false so the step is never auto-confirmed.
    /// </summary>
    private static TalkStep TalkInZone(string id, string requiredZone) => new()
    {
        Id = id,
        Target = new NpcLocation(12345u, (int)uint.Parse(requiredZone), new Position3(999f, 0f, 999f)),
        RequiredZone = requiredZone,
        Expect = new PredicateExpect { Predicate = "0 == 1" }  // never auto-confirms
    };

    private static string FormatAction(EngineAction action) => action switch
    {
        EngineAction.Navigate nav => $"Navigate({nav.Destination.X},{nav.Destination.Y},{nav.Destination.Z})",
        EngineAction.Teleport tp => $"Teleport(Dest={tp.Destination.Value})",
        EngineAction.Interact interact => $"Interact(NpcId={interact.Target.Value})",
        EngineAction.Wait wait => $"Wait({wait.Reason})",
        EngineAction.AwaitUser au => $"AwaitUser({au.Reason})",
        EngineAction.Done => "Done",
        _ => action.GetType().Name
    };

    // =========================================================================
    // D1 -- Open-world death warp: engine teleports back to step's required zone
    // =========================================================================

    [Fact]
    public async Task D1_OpenWorldDeathWarp_EngineTeleportsBackToStepRequiredZone()
    {
        /*
         * Given:
         *   - A TalkStep with RequiredZone = "130" (zone 130 maps to aetheryte 1000)
         *   - Player starts in zone 130, position (0,0,0)
         *   - Quest sequence 0 active
         *
         * When:
         *   - Tick 1: normal dispatch (Navigate toward NPC at 999,0,999)
         *   - Set dead=true (player dies)
         *   - Tick 2: engine reads dead=true; _wasDeadLastTick becomes true
         *   - Set dead=false, zone=129 (player revives at home aetheryte in wrong zone)
         *   - Tick 3: dead-to-alive transition; InstanceKind.None -> _deathRecoveryPending = true;
         *             zone 130 maps to aetheryte 1000 -> condition 4b fires
         *
         * Then:
         *   - Tick 3 returns EngineAction.Teleport with Destination.Value == 1000
         *
         * RED: Will fail at runtime until Builder implements _wasDeadLastTick/_deathRecoveryPending
         *      edge detection and condition 4b in the step loop.
         */

        // Arrange
        const uint questId = BaseQuestId + 1;
        var step = TalkInZone("talk-d1", "130");
        var quest = BuildQuest(questId, step);

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(130));       // correct zone initially
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-d1");

        // Tick 1: normal dispatch -- Navigate toward NPC at (999,0,999)
        var action1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(action1);

        // Player dies
        harness.GameState.SetDead(true);

        // Tick 2: engine sees dead=true; _wasDeadLastTick becomes true
        // (action while dead is unspecified -- could be Wait, Navigate, etc.)
        await harness.Engine.Tick(CancellationToken.None);

        // Player revives at home aetheryte in wrong zone
        harness.GameState.SetDead(false);
        harness.GameState.SetZone(new ZoneId(129));

        // Tick 3: dead-to-alive transition -> _deathRecoveryPending = true;
        //         RequiredZone "130" != playerZone 129, aetheryte 1000 mapped -> Teleport
        var action3 = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var teleport = Assert.IsType<EngineAction.Teleport>(action3);
        Assert.Equal(1000u, teleport.Destination.Value);
    }

    // =========================================================================
    // D2 -- Zone has no aetheryte: falls through to normal Navigate
    // =========================================================================

    [Fact]
    public async Task D2_ZoneHasNoAetheryte_FallsThroughToNormalNavigate()
    {
        /*
         * Given:
         *   - A TalkStep with RequiredZone = "128" (zone 128 has NO aetheryte in default map)
         *   - Player starts in zone 128, position (0,0,0)
         *   - Quest sequence 0 active
         *
         * When:
         *   - Tick 1: normal dispatch (Navigate toward NPC)
         *   - Set dead=true
         *   - Tick 2: engine reads dead=true
         *   - Set dead=false, zone=129 (death warp to wrong zone)
         *   - Tick 3: dead-to-alive transition -> _deathRecoveryPending = true;
         *             RequiredZone "128" but TryGetAetheryteByZone(128) returns false
         *
         * Then:
         *   - Tick 3 returns EngineAction.Navigate (falls through), NOT EngineAction.Teleport
         *
         * RED: Will fail at runtime until Builder implements conditions 4a/4b.
         *      Without the death recovery logic, the engine will Navigate as usual -- so this
         *      test may accidentally pass before implementation. The key validation is that
         *      the Navigate is NOT a Teleport, which only matters once the feature exists and
         *      other tests (D1) pass.
         */

        // Arrange
        const uint questId = BaseQuestId + 2;
        var step = TalkInZone("talk-d2", "128");
        var quest = BuildQuest(questId, step);

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(128));       // correct zone initially
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-d2");

        // Tick 1: normal dispatch
        var action1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(action1);

        // Player dies
        harness.GameState.SetDead(true);

        // Tick 2: engine sees dead=true
        await harness.Engine.Tick(CancellationToken.None);

        // Player revives at wrong zone
        harness.GameState.SetDead(false);
        harness.GameState.SetZone(new ZoneId(129));

        // Tick 3: death recovery pending but no aetheryte for zone 128 -> fall through
        var action3 = await harness.Engine.Tick(CancellationToken.None);

        // Assert -- Navigate, NOT Teleport
        Assert.IsType<EngineAction.Navigate>(action3);
        Assert.IsNotType<EngineAction.Teleport>(action3);
    }

    // =========================================================================
    // D3 -- Never died: wrong zone does NOT trigger death teleport
    // =========================================================================

    [Fact]
    public async Task D3_NeverDied_WrongZoneDoesNotTriggerDeathTeleport()
    {
        /*
         * Given:
         *   - A TalkStep with RequiredZone = "130" (has aetheryte 1000)
         *   - Player starts in zone 129 (wrong zone), never dies
         *   - Quest sequence 0 active
         *
         * When:
         *   - Tick 1: player is in wrong zone but _deathRecoveryPending is false (no death)
         *
         * Then:
         *   - Tick 1 returns EngineAction.Navigate (normal dispatch), NOT EngineAction.Teleport
         *   - Confirms zone mismatch alone is insufficient; the death flag must be set
         *
         * RED: This test validates the negative case. Without death recovery logic, the
         *      engine already Navigates. Once death recovery exists, this test ensures the
         *      teleport ONLY fires after a death, not on any zone mismatch.
         */

        // Arrange
        const uint questId = BaseQuestId + 3;
        var step = TalkInZone("talk-d3", "130");
        var quest = BuildQuest(questId, step);

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(129));       // wrong zone, never dies
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-d3");

        // Tick 1: no death -> no flag -> normal dispatch
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert -- Navigate, NOT Teleport
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.True(navigate.Destination == new WorldPosition(999f, 0f, 999f),
            $"Expected Navigate toward NPC at (999,0,999) but got {FormatAction(action)}. " +
            "Zone mismatch without a prior death must NOT trigger death-recovery Teleport.");
    }

    // =========================================================================
    // D4 -- Death in instance (Dungeon): flag NOT set, no teleport
    // =========================================================================

    [Fact]
    public async Task D4_DeathInDungeon_FlagNotSet_NoTeleport()
    {
        /*
         * Given:
         *   - A TalkStep with RequiredZone = "130" (has aetheryte 1000)
         *   - Player starts in zone 130
         *   - Quest sequence 0 active
         *
         * When:
         *   - Tick 1: normal dispatch
         *   - Set dead=true, instanceKind=Dungeon (player dies inside dungeon)
         *   - Tick 2: engine reads dead=true; _wasDeadLastTick=true
         *   - Set dead=false (player revives inside dungeon; instanceKind still Dungeon)
         *   - Tick 3: dead-to-alive transition; GetCurrentInstanceKind returns Dungeon -> flag NOT set
         *   - Set zone=129, instanceKind=None (player exits dungeon to open world, wrong zone)
         *   - Tick 4: no dead-to-alive transition (was alive last tick too) -> flag still not set
         *
         * Then:
         *   - Tick 4 returns EngineAction.Navigate, NOT EngineAction.Teleport
         *
         * RED: Will fail at runtime until Builder implements the InstanceKind gate in the
         *      dead-to-alive edge detection (DR3).
         */

        // Arrange
        const uint questId = BaseQuestId + 4;
        var step = TalkInZone("talk-d4", "130");
        var quest = BuildQuest(questId, step);

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(130));       // correct zone initially
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-d4");

        // Tick 1: normal dispatch
        var action1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(action1);

        // Player dies inside dungeon
        harness.GameState.SetDead(true);
        harness.GameState.SetInstanceKind(InstanceKind.Dungeon);

        // Tick 2: engine sees dead=true; _wasDeadLastTick becomes true
        await harness.Engine.Tick(CancellationToken.None);

        // Player revives inside dungeon (instanceKind still Dungeon)
        harness.GameState.SetDead(false);
        // instanceKind remains Dungeon -- the player revives inside the instance

        // Tick 3: dead-to-alive transition fires, but InstanceKind is Dungeon -> flag NOT set
        await harness.Engine.Tick(CancellationToken.None);

        // Player exits dungeon to open world in wrong zone
        harness.GameState.SetZone(new ZoneId(129));
        harness.GameState.SetInstanceKind(InstanceKind.None);

        // Tick 4: no dead-to-alive transition (was alive last tick); flag never set
        var action4 = await harness.Engine.Tick(CancellationToken.None);

        // Assert -- Navigate (normal dispatch), NOT Teleport
        Assert.IsType<EngineAction.Navigate>(action4);
        Assert.IsNotType<EngineAction.Teleport>(action4);
    }

    // =========================================================================
    // D5 -- Flag clears when zone becomes satisfied (no repeated teleports)
    // =========================================================================

    [Fact]
    public async Task D5_FlagClearsWhenZoneSatisfied_NoRepeatedTeleports()
    {
        /*
         * Given:
         *   - A TalkStep with RequiredZone = "130" (has aetheryte 1000)
         *   - Player starts in zone 130
         *   - Quest sequence 0 active
         *
         * When:
         *   - Tick 1: normal dispatch
         *   - Set dead=true
         *   - Tick 2: engine reads dead=true
         *   - Set dead=false, zone=129 (death warp)
         *   - Tick 3: transition fires -> _deathRecoveryPending = true; wrong zone -> Teleport
         *   - Set zone=130 (player arrives via teleport)
         *   - Tick 4: condition 4a fires (zone satisfied) -> _deathRecoveryPending = false
         *
         * Then:
         *   - Tick 4 returns EngineAction.Navigate (normal dispatch toward NPC), NOT Teleport
         *   - Confirms the flag is cleared and does not cause repeated teleports
         *
         * RED: Will fail at runtime until Builder implements both conditions 4a and 4b.
         */

        // Arrange
        const uint questId = BaseQuestId + 5;
        var step = TalkInZone("talk-d5", "130");
        var quest = BuildQuest(questId, step);

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(130));       // correct zone initially
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-d5");

        // Tick 1: normal dispatch
        var action1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(action1);

        // Player dies
        harness.GameState.SetDead(true);

        // Tick 2: engine sees dead=true
        await harness.Engine.Tick(CancellationToken.None);

        // Player revives at wrong zone
        harness.GameState.SetDead(false);
        harness.GameState.SetZone(new ZoneId(129));

        // Tick 3: death recovery fires -> Teleport(1000)
        var action3 = await harness.Engine.Tick(CancellationToken.None);
        var teleport = Assert.IsType<EngineAction.Teleport>(action3);
        Assert.Equal(1000u, teleport.Destination.Value);

        // Player arrives at correct zone via teleport
        harness.GameState.SetZone(new ZoneId(130));

        // Tick 4: zone satisfied -> flag cleared -> normal dispatch
        var action4 = await harness.Engine.Tick(CancellationToken.None);

        // Assert -- Navigate (normal), NOT another Teleport
        var navigate = Assert.IsType<EngineAction.Navigate>(action4);
        Assert.True(navigate.Destination == new WorldPosition(999f, 0f, 999f),
            $"Expected Navigate toward NPC at (999,0,999) but got {FormatAction(action4)}. " +
            "After arriving in the correct zone, _deathRecoveryPending must clear (condition 4a). " +
            "Repeated teleports indicate the flag was not cleared.");
    }

    // =========================================================================
    // D6 -- BeginRun clears death recovery state
    // =========================================================================

    [Fact]
    public async Task D6_BeginRunClearsDeathRecoveryState()
    {
        /*
         * Given:
         *   - A TalkStep with RequiredZone = "130" (has aetheryte 1000)
         *   - Player starts in zone 130
         *   - Quest sequence 0 active
         *
         * When:
         *   - Tick 1: normal dispatch
         *   - Set dead=true
         *   - Tick 2: engine reads dead=true (sets _wasDeadLastTick = true)
         *   - Call BeginRun("run-2") -- new run clears both flags
         *   - Set dead=false, zone=129 (death warp conditions, but flags cleared by BeginRun)
         *   - Tick 3 (new run): _wasDeadLastTick was cleared to false, so no dead-to-alive
         *     transition detected even though player is now alive in zone 129
         *
         * Then:
         *   - Tick 3 returns EngineAction.Navigate (normal dispatch), NOT EngineAction.Teleport
         *   - Confirms BeginRun clears both _wasDeadLastTick and _deathRecoveryPending
         *
         * RED: Will fail at runtime until Builder clears both flags in BeginRun (DR4).
         */

        // Arrange
        const uint questId = BaseQuestId + 6;
        var step = TalkInZone("talk-d6", "130");
        var quest = BuildQuest(questId, step);

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(130));       // correct zone initially
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-d6-1");

        // Tick 1: normal dispatch
        var action1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(action1);

        // Player dies
        harness.GameState.SetDead(true);

        // Tick 2: engine sees dead=true (_wasDeadLastTick becomes true)
        await harness.Engine.Tick(CancellationToken.None);

        // Start new run -- must clear both flags
        harness.Engine.BeginRun("run-d6-2");

        // Set up death warp conditions (dead=false, wrong zone)
        // but flags were cleared by BeginRun so no transition should be detected
        harness.GameState.SetDead(false);
        harness.GameState.SetZone(new ZoneId(129));

        // Tick 3 (new run): _wasDeadLastTick was cleared -> no dead-to-alive edge -> no flag
        var action3 = await harness.Engine.Tick(CancellationToken.None);

        // Assert -- Navigate (normal dispatch), NOT Teleport
        Assert.IsType<EngineAction.Navigate>(action3);
        Assert.IsNotType<EngineAction.Teleport>(action3);
    }
}
