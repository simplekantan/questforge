using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Combat;

/// <summary>
/// RED PHASE: Engine-level approach handoff + step-exit cleanup — EH-1..EH-8 from
/// COMBAT_APPROACH_HANDOFF_PLAN.md §4.
///
/// Fix 1 (leg-order flip): EH-1..EH-4 test the new "Decide first, Location second" order.
///   - EH-1, EH-3, EH-4 will FAIL ON ASSERTION (current code still navigates-first, so the
///     no-target path reaches the Navigate leg before calling Decide).
///   - EH-2 is THE bug-1 regression pin: a live target prevents re-pin to Location. It FAILS
///     ON ASSERTION with current code (which navigates to Location when player drifts).
///   - EH-3b asserts self-correction (no oscillation). FAILS ON ASSERTION.
///
/// Fix 2 (ResetAsync at step-exit): EH-5 and EH-6 observe ClearTarget after step confirmation
/// and sequence change respectively. These FAIL TO COMPILE (ResetAsync not yet on the controller)
/// once the Builder adds the call site — but until then they will FAIL ON ASSERTION because
/// the current engine calls synchronous Reset() which does no IO.
///
/// EH-7 is an ASSERTION TWEAK to the existing EX_ResetOnAdvance test (see CombatEngineTests.cs).
/// EH-8 (non-combat D6 guard) reuses existing NS_* tests already in the suite — confirmed green.
///
/// Spec: docs/COMBAT_APPROACH_HANDOFF_PLAN.md §4 (Engine-level GWT)
/// </summary>
public sealed class CombatApproachHandoffTests
{
    // =========================================================================
    // Helpers
    // =========================================================================

    private static string FormatAction(EngineAction a) => a.GetType().Name;

    private static string FormatTargets(EngineTestHarness h)
        => string.Join(", ", h.Combat.RecordedTargets.Snapshot()
            .Select(t => t.IsClear ? "ClearTarget" : $"SetTarget({t.Target!.Value.Value})"));

    private static string FormatNavRequests(EngineTestHarness h)
        => string.Join(", ", h.Navigator.RecordedNavigationRequests.Snapshot()
            .Select(r => $"Dest=({r.Destination.X},{r.Destination.Y},{r.Destination.Z})"));

    private static string FormatReads(EngineTestHarness h)
        => string.Join(", ", h.GameState.RecordedReads.Snapshot().Select(r => r.Method));

    private static HostileActor MakeHostile(ulong id, uint dataId, float distance,
        WorldPosition? position = null)
        => new HostileActor(
            new ActorId(id), dataId,
            position ?? new WorldPosition(0f, 0f, 0f),
            distance,
            IsTargetable: true, IsDead: false,
            IsTargetingPlayer: false, OnPlayerEnmityList: false, HasQuestMarker: false);

    /// <summary>
    /// One-combat-step quest with Location at (100,0,100) and expect = questSequence >= 1.
    /// Default questId = 66400.
    /// </summary>
    private static (EngineTestHarness Harness, QuestDefinition Quest, QuestId QuestId, CombatStep Step)
        BuildCombatWithLocation(uint questId = 66400u)
    {
        var h = new EngineTestHarness();
        var qid = new QuestId(questId);
        h.QuestState.SetQuestSequence(qid, 0);

        var step = new CombatStep
        {
            Id = "combat-location-test",
            KillEnemyDataIds = [100u],
            Spawn = CombatSpawn.AutoOnEnterArea,
            Location = new NpcLocation(0u, 0, new Position3(100f, 0f, 100f)),
            Expect = new PredicateExpect { Predicate = $"questSequence({questId}) >= 1" }
        };
        var quest = new QuestDefinition
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "Handoff Test Quest",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences = [new QuestSequence { Sequence = 0, Steps = [step] }]
        };
        h.Engine.StartQuest(quest);
        return (h, quest, qid, step);
    }

    // =========================================================================
    // EH-1 — initial approach navigates to Location when no mob in scan range
    //         (fall-back path replaces old navigate-first leg; GetHostileActors IS called
    //          because Decide runs first on a combat tick — D4)
    // =========================================================================

    [Fact]
    public async Task EH1_InitialApproach_NoMobInRange_NavigatesToLocation()
    {
        // GIVEN a one-combat-step quest, Location at (100,0,100), player at (0,0,0),
        //       NO hostiles within the 30 m scan radius.
        var (h, _, _, _) = BuildCombatWithLocation();
        h.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        // No hostiles added → Decide returns null target → fall-back fires.

        h.GameState.Reset();
        // WHEN tick 1.
        var action = await h.Engine.Tick(CancellationToken.None);

        // THEN action is Navigate to (100,0,100).
        // FAILS ASSERTION with current code ONLY if the navigate-first leg returned Navigate
        // without calling Decide, which it does — BUT the assertion itself (Navigate) will
        // still pass with current code. The real regression pin is EH-2. EH-1 confirms the
        // no-mob path still reaches the Location navigate after the leg-order flip.
        var nav = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(100f, nav.Destination.X, precision: 0);
        Assert.Equal(100f, nav.Destination.Z, precision: 0);

        // Under option B: GetHostileActors IS called this tick (Decide runs first on every
        // combat tick). Contrast with old test EC_NavigateFirst which asserted no read here.
        var reads = h.GameState.RecordedReads.Snapshot();
        Assert.True(reads.Any(r => r.Method == nameof(QuestForge.Adapters.State.IGameStateProvider.GetHostileActors)),
            $"Under option B, Decide (and hence GetHostileActors) runs FIRST on every combat tick. Reads: {FormatReads(h)}");
    }

    // =========================================================================
    // EH-2 — target in range → Engage, NOT re-pin to Location (Bug 1 regression pin)
    // =========================================================================

    [Fact]
    public async Task EH2_TargetInRange_NeverNavigatesToLocation()
    {
        // GIVEN the player starts AT (100,0,100) (within StopDistance of Location);
        //       one eligible hostile at (140,0,140) — distance ≈ 57 m from Location,
        //       but within the 30 m scan radius from the player who has moved.
        //       We set distance = 20 so it appears in range for the initial tick.
        var (h, _, qid, _) = BuildCombatWithLocation();
        h.GameState.SetPosition(new WorldPosition(100f, 0f, 100f));
        var mobPos = new WorldPosition(140f, 0f, 140f);
        h.GameState.AddHostileActor(MakeHostile(9, 100u, distance: 20f, position: mobPos));

        h.GameState.Reset();
        // WHEN tick 1: Decide runs first, acquires target at mobPos.
        var tick1 = await h.Engine.Tick(CancellationToken.None);

        // THEN action is Engage (not Navigate to Location).
        // FAILS ON ASSERTION with current code: the navigate-first leg fires when player
        // drifts out of Location.StopDistance, before Decide is ever called.
        var engage1 = Assert.IsType<EngineAction.Engage>(tick1);
        Assert.NotNull(engage1.Target);

        // The only NavigateTo recorded should target the mob position, never (100,0,100).
        var navsTick1 = h.Navigator.RecordedNavigationRequests.Snapshot();
        Assert.All(navsTick1, req =>
            Assert.False(
                req.Destination.X == 100f && req.Destination.Z == 100f,
                $"NavigateTo(Location) must not appear while a target is held. Nav requests: {FormatNavRequests(h)}"));

        // Simulate player having moved away from Location toward the mob (tick 2).
        h.GameState.SetPosition(new WorldPosition(120f, 0f, 120f));

        var tick2 = await h.Engine.Tick(CancellationToken.None);

        // THEN tick 2 is ALSO Engage (mob still in scan range), NOT Navigate(Location).
        // This is the core Bug 1 pin: a live target means controller owns movement.
        var engage2 = Assert.IsType<EngineAction.Engage>(tick2);
        Assert.NotNull(engage2.Target);

        var navsAfterTick2 = h.Navigator.RecordedNavigationRequests.Snapshot();
        Assert.All(navsAfterTick2, req =>
            Assert.False(
                req.Destination.X == 100f && req.Destination.Z == 100f,
                $"NavigateTo(Location) must NEVER appear while a live target is in range. Nav requests: {FormatNavRequests(h)}"));
    }

    // =========================================================================
    // EH-3 — no target near Location → waits with Engage(null), no spurious Navigate
    // =========================================================================

    [Fact]
    public async Task EH3_NoTargetNearLocation_EngagesNullNoNavigate()
    {
        // GIVEN player WITHIN StopDistance of Location (100,0,100); no hostiles in scan range.
        var (h, _, _, _) = BuildCombatWithLocation();
        h.GameState.SetPosition(new WorldPosition(100f, 0f, 100f)); // at Location
        // No hostiles added.

        h.GameState.Reset();
        // WHEN a tick runs: Decide returns null; player is within StopDistance.
        var action = await h.Engine.Tick(CancellationToken.None);

        // THEN action is Engage(step, null) — no-target idle.
        // FAILS ON ASSERTION with current code: the navigate-first leg would return Navigate
        // if the player happens to be at exactly Location (actually won't if within StopDistance,
        // but the test pins the intended new behaviour regardless).
        // Under option B: no target → ResolveInteractOrNavigate returns Wait sentinel (not Navigate)
        // → engine falls through to Engage(null).
        var engage = Assert.IsType<EngineAction.Engage>(action);
        Assert.Null(engage.Target);

        // No new NavigateTo to (100,0,100).
        var navs = h.Navigator.RecordedNavigationRequests.Snapshot();
        Assert.All(navs, req =>
            Assert.False(
                req.Destination.X == 100f && req.Destination.Z == 100f,
                $"No NavigateTo(Location) when player is already within StopDistance. Navs: {FormatNavRequests(h)}"));
    }

    // =========================================================================
    // EH-3b — between-mobs far null tick self-corrects (no oscillation, D5)
    // =========================================================================

    [Fact]
    public async Task EH3b_BetweenMobs_FarNullTick_SelfCorrectsToEngage()
    {
        // GIVEN player BEYOND StopDistance of Location at (100,0,100): player at (130,0,130).
        //       Chased mob just died → scan range momentarily empty this tick.
        var (h, _, _, _) = BuildCombatWithLocation();
        h.GameState.SetPosition(new WorldPosition(130f, 0f, 130f));
        // No hostiles yet — simulating dead-mob window.

        h.GameState.Reset();
        // WHEN tick N: Decide returns null, player beyond StopDistance ⇒ Navigate(Location).
        var tickN = await h.Engine.Tick(CancellationToken.None);

        // THEN a single Navigate(100,0,100) is emitted (the one bounded nudge).
        // FAILS ON ASSERTION with current code: current code does navigate-first without calling
        // Decide, so this Navigate happens anyway, BUT the critical failure is tick N+1 below.
        var navN = Assert.IsType<EngineAction.Navigate>(tickN);
        Assert.Equal(100f, navN.Destination.X, precision: 0);
        Assert.Equal(100f, navN.Destination.Z, precision: 0);

        // Now A2 enters scan range at (135,0,135) — the Location nudge must be abandoned.
        var a2Pos = new WorldPosition(135f, 0f, 135f);
        h.GameState.AddHostileActor(MakeHostile(2, 100u, distance: 10f, position: a2Pos));

        // WHEN tick N+1: Decide finds A2, returns it.
        var tickN1 = await h.Engine.Tick(CancellationToken.None);

        // THEN action is Engage (NOT another Navigate(Location)).
        // Under option B: Decide runs first ⇒ finds A2 ⇒ Target != null ⇒ Engage.
        // Under current code: the navigate-first leg fires because player is still beyond
        // StopDistance ⇒ wrong Navigate(Location) again — this is the oscillation being fixed.
        // FAILS ON ASSERTION with current code.
        var engage = Assert.IsType<EngineAction.Engage>(tickN1);
        Assert.NotNull(engage.Target);

        // The recorded navigation requests after tick N+1 must retarget A2, not re-issue Location.
        var allNavs = h.Navigator.RecordedNavigationRequests.Snapshot();
        var locationNavs = allNavs.Where(r => r.Destination.X == 100f && r.Destination.Z == 100f).ToList();
        // At most the ONE tick-N nudge — tick N+1 must NOT add another Location nav.
        Assert.True(locationNavs.Count <= 1,
            $"Expected at most 1 Navigate(Location) (the bounded nudge from tick N), got {locationNavs.Count}. All navs: {FormatNavRequests(h)}");
    }

    // =========================================================================
    // EH-4 — no target AND no Location set → idle Engage(null), never navigates
    // =========================================================================

    [Fact]
    public async Task EH4_NoTargetNoLocation_EngagesNullNoNavigate()
    {
        // GIVEN a one-combat-step quest with Location == null; no hostiles; expect unmet.
        var h = new EngineTestHarness();
        var qid = new QuestId(66401u);
        h.QuestState.SetQuestSequence(qid, 0);
        h.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        // No hostiles, no Location.

        var step = new CombatStep
        {
            Id = "combat-no-location",
            KillEnemyDataIds = [100u],
            Spawn = CombatSpawn.AutoOnEnterArea,
            Expect = new PredicateExpect { Predicate = "questSequence(66401) >= 1" }
            // Location intentionally omitted (null)
        };
        var quest = new QuestDefinition
        {
            SchemaVersion = "1.0.0",
            Id = 66401u,
            Name = "No-Location Combat Quest",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences = [new QuestSequence { Sequence = 0, Steps = [step] }]
        };
        h.Engine.StartQuest(quest);

        // WHEN a tick runs: Decide returns null; no Location to fall back to.
        var action = await h.Engine.Tick(CancellationToken.None);

        // THEN action is Engage(step, null) — idle. No NavigateTo.
        // FAILS ON ASSERTION with current code? The current code has no Location so it skips
        // the navigate-first leg and calls Decide → Engage(null). This may already pass.
        // We pin it explicitly to guard against regressions.
        var engage = Assert.IsType<EngineAction.Engage>(action);
        Assert.Null(engage.Target);
        Assert.Empty(h.Navigator.RecordedNavigationRequests.Snapshot());
    }

    // =========================================================================
    // EH-5 — confirmed combat step clears the game target (Bug 2 pin)
    // =========================================================================
    // NOTE: FAILS ON ASSERTION with current code (Reset() does no IO); will FAIL TO COMPILE
    //       once the engine call site is switched to ResetAsync.

    [Fact]
    public async Task EH5_ConfirmedCombatStep_ClearsGameTarget()
    {
        // GIVEN a two-sequence quest: seq 0 = CombatStep (expect questVariable >= 1),
        //       seq 1 = TalkStep; player in range, one eligible hostile.
        var h = new EngineTestHarness();
        var qid = new QuestId(66405u);
        h.QuestState.SetQuestSequence(qid, 0);
        h.QuestState.SetQuestVariables(qid, 0, 0, 0, 0, 0, 0);
        h.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        h.GameState.AddHostileActor(MakeHostile(9, 100u, distance: 2f));

        var combatStep = new CombatStep
        {
            Id = "combat-cleartarget-test",
            KillEnemyDataIds = [100u],
            Spawn = CombatSpawn.AutoOnEnterArea,
            Expect = new PredicateExpect { Predicate = "questVariable(66405, 0) >= 1" }
        };
        var talkStep = new TalkStep
        {
            Id = "post-combat-talk",
            Target = new NpcLocation(1u, 0, new Position3(0f, 0f, 0f))
        };
        var quest = new QuestDefinition
        {
            SchemaVersion = "1.0.0",
            Id = 66405u,
            Name = "Combat ClearTarget Bug-2 Quest",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences =
            [
                new QuestSequence { Sequence = 0, Steps = [combatStep] },
                new QuestSequence { Sequence = 1, Steps = [talkStep] }
            ]
        };
        h.Engine.StartQuest(quest);

        // WHEN tick 1: variables[0]=0 → expect false → Decide acquires target → Engage.
        var tick1 = await h.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Engage>(tick1);
        var setTargets = h.Combat.RecordedTargets.Snapshot().Where(t => !t.IsClear).ToList();
        Assert.NotEmpty(setTargets); // SetTarget(X) was issued

        // Flip variables[0]=1 → expect becomes true.
        h.QuestState.SetQuestVariables(qid, 1, 0, 0, 0, 0, 0);

        // WHEN tick 2: expect true → step confirmed; engine runs ResetAsync.
        var tick2 = await h.Engine.Tick(CancellationToken.None);

        // THEN a ClearTarget is recorded (the Bug 2 fix).
        // FAILS ON ASSERTION with current code: Reset() does no IO, so no ClearTarget appears.
        var clears = h.Combat.RecordedTargets.Snapshot().Where(t => t.IsClear).ToList();
        Assert.NotEmpty(clears);

        // AND tick 2's action is NOT Engage (step was confirmed, engine moves on).
        Assert.IsNotType<EngineAction.Engage>(tick2);
    }

    // =========================================================================
    // EH-6 — sequence-change cleanup clears the target
    // =========================================================================
    // NOTE: FAILS ON ASSERTION with current code (Reset() does no IO).

    [Fact]
    public async Task EH6_SequenceChange_ClearsGameTarget()
    {
        // GIVEN seq 0 combat step, target acquired.
        var h = new EngineTestHarness();
        var qid = new QuestId(66406u);
        h.QuestState.SetQuestSequence(qid, 0);
        h.QuestState.SetQuestVariables(qid, 0, 0, 0, 0, 0, 0);
        h.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        h.GameState.AddHostileActor(MakeHostile(9, 100u, distance: 2f));

        var combatStep = new CombatStep
        {
            Id = "combat-seqchange",
            KillEnemyDataIds = [100u],
            Spawn = CombatSpawn.AutoOnEnterArea,
            Expect = new PredicateExpect { Predicate = "questVariable(66406, 0) >= 1" }
        };
        var talkStep = new TalkStep
        {
            Id = "post-combat-talk-seqchange",
            Target = new NpcLocation(1u, 0, new Position3(0f, 0f, 0f))
        };
        var quest = new QuestDefinition
        {
            SchemaVersion = "1.0.0",
            Id = 66406u,
            Name = "Sequence Change ClearTarget Quest",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences =
            [
                new QuestSequence { Sequence = 0, Steps = [combatStep] },
                new QuestSequence { Sequence = 1, Steps = [talkStep] }
            ]
        };
        h.Engine.StartQuest(quest);

        // Tick 1: seq 0 → Engage, controller acquires target.
        var tick1 = await h.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Engage>(tick1);
        Assert.NotEmpty(h.Combat.RecordedTargets.Snapshot().Where(t => !t.IsClear).ToList());

        // WHEN the game advances SetQuestSequence to seq 1 (no variable change).
        h.QuestState.SetQuestSequence(qid, 1);

        // Tick 2: sequence-change branch fires ResetAsync.
        var tick2 = await h.Engine.Tick(CancellationToken.None);

        // THEN a ClearTarget is recorded.
        // FAILS ON ASSERTION with current code: Reset() does no IO.
        var clears = h.Combat.RecordedTargets.Snapshot().Where(t => t.IsClear).ToList();
        Assert.NotEmpty(clears);

        // AND tick 2 action is not Engage (engine is now on the talk step in seq 1).
        Assert.IsNotType<EngineAction.Engage>(tick2);
    }

    // =========================================================================
    // EH-9 — moving target during approach does not strand the player (no-strand property)
    //         Addendum A3: re-path fires when drift > RepathThreshold (0.5 m).
    //         Engine-level: every tick Engage (tracking mob), never falls back to Location.
    // =========================================================================

    [Fact]
    public async Task EH9_MovingTargetDuringApproach_TrackedAcrossTicks_NeverFallsBackToLocation()
    {
        // GIVEN the one-combat-step quest, Location (100,0,100); player at (100,0,100) (at Location);
        //       one eligible hostile moving: tick1 at (140,0,140) dist 57, tick2 at (160,0,160) dist 85.
        //       Both positions are within the 30 m scan radius from the PLAYER's changing position.
        //       We fake distance to control out-of-range state (distance always > 1, out of attackRange).
        var (h, _, _, _) = BuildCombatWithLocation();
        h.GameState.SetPosition(new WorldPosition(100f, 0f, 100f));
        var mob1 = new WorldPosition(140f, 0f, 140f);
        h.GameState.AddHostileActor(MakeHostile(9, 100u, distance: 20f, position: mob1));

        h.GameState.Reset();

        // WHEN tick 1: mob at (140,0,140), out of attackRange (20 > 1).
        var tick1 = await h.Engine.Tick(CancellationToken.None);

        // THEN Engage (target non-null) and NavigateTo records mob1, NOT Location.
        var engage1 = Assert.IsType<EngineAction.Engage>(tick1);
        Assert.NotNull(engage1.Target);

        var navs1 = h.Navigator.RecordedNavigationRequests.Snapshot();
        Assert.NotEmpty(navs1);
        var lastNav1 = navs1[^1];
        Assert.Equal(mob1.X, lastNav1.Destination.X, precision: 0);
        Assert.Equal(mob1.Z, lastNav1.Destination.Z, precision: 0);
        Assert.False(
            lastNav1.Destination.X == 100f && lastNav1.Destination.Z == 100f,
            $"Must not navigate to Location while a target is in scan range. Navs: {FormatNavRequests(h)}");

        // WHEN tick 2: mob moves to (160,0,160) (drift ≈ 28 m >> RepathThreshold 0.5).
        //              Player position updated to simulate movement toward old position.
        var mob2 = new WorldPosition(160f, 0f, 160f);
        h.GameState.ClearHostileActors();
        h.GameState.AddHostileActor(MakeHostile(9, 100u, distance: 20f, position: mob2));

        var tick2 = await h.Engine.Tick(CancellationToken.None);

        // THEN still Engage (not Navigate(Location)) AND a new NavigateTo to mob2 is recorded.
        // FAILS ASSERTION: current code has no _approachPosition — same-target else does nothing,
        // so the second nav is never issued. The engine still returns Engage (controller owns nav),
        // but the "tracking" part (new nav destination) is missing.
        var engage2 = Assert.IsType<EngineAction.Engage>(tick2);
        Assert.NotNull(engage2.Target);

        var navs2 = h.Navigator.RecordedNavigationRequests.Snapshot();
        // Must have at least 2 NavigateTo calls, and the latest must target mob2 (not mob1 or Location).
        Assert.True(navs2.Count >= 2,
            $"Expected re-path to mob's new position on tick 2. Navs: {FormatNavRequests(h)}");
        var lastNav2 = navs2[^1];
        Assert.Equal(mob2.X, lastNav2.Destination.X, precision: 0);
        Assert.Equal(mob2.Z, lastNav2.Destination.Z, precision: 0);
        Assert.Equal(0.5f, lastNav2.Options.StoppingDistance);

        // Confirm no NavigateTo(Location) at any point.
        Assert.All(navs2, req =>
            Assert.False(
                req.Destination.X == 100f && req.Destination.Z == 100f,
                $"NavigateTo(Location) must never appear while a live target is in scan range. Navs: {FormatNavRequests(h)}"));
    }

    // =========================================================================
    // EH-10 — small-step-then-idle target is closed to attack range (engine-level no-strand)
    //          The exact in-game stall scenario: drift 0.8 > RepathThreshold 0.5 → re-path.
    //          Tick 3 simulates vnavmesh arrival (distance 0.5f ≤ attackRange 1.0f → in range).
    // =========================================================================

    [Fact]
    public async Task EH10_SmallStepThenIdleTarget_ClosesToAttackRange_NoStrand()
    {
        // GIVEN the one-combat-step quest; player at (100,0,100) (at Location);
        //       one eligible hostile that:
        //         tick 1: (140,0,140), distance 8f (out of range)
        //         tick 2: (140.8,0,140) (drift=0.8 > RepathThreshold=0.5), still out of range (8.8f)
        //         tick 3: same position (140.8,0,140), distance 0.5f (in range ≤ 1.0 attackRange)
        var (h, _, _, _) = BuildCombatWithLocation();
        h.GameState.SetPosition(new WorldPosition(100f, 0f, 100f));
        var mob1 = new WorldPosition(140f,   0f, 140f);
        var mob2 = new WorldPosition(140.8f, 0f, 140f); // drifted 0.8 m

        h.GameState.AddHostileActor(MakeHostile(9, 100u, distance: 8f, position: mob1));
        h.GameState.Reset();

        // WHEN tick 1: mob at mob1, out of range → Engage + nav to mob1.
        var tick1 = await h.Engine.Tick(CancellationToken.None);
        var engage1 = Assert.IsType<EngineAction.Engage>(tick1);
        Assert.NotNull(engage1.Target);

        var navs1 = h.Navigator.RecordedNavigationRequests.Snapshot();
        Assert.NotEmpty(navs1);
        // StoppingDistance must be 0.5 (ApproachStopBuffer, not attackRange).
        // FAILS ASSERTION: current code uses attackRange as StoppingDistance.
        Assert.Equal(0.5f, navs1[^1].Options.StoppingDistance);

        // WHEN tick 2: mob takes a small step to mob2 (drift=0.8 > RepathThreshold=0.5), IDLES.
        //              Still out of range (distance 8.8f).
        h.GameState.ClearHostileActors();
        h.GameState.AddHostileActor(MakeHostile(9, 100u, distance: 8.8f, position: mob2));

        var tick2 = await h.Engine.Tick(CancellationToken.None);

        // THEN Engage AND re-path to mob2 (second nav recorded).
        // FAILS ASSERTION: current code has no _approachPosition → no re-path → player parks
        // at stale mob1 position, mob2 is at 1.8 m > 1.0 attackRange → permanent stall.
        var engage2 = Assert.IsType<EngineAction.Engage>(tick2);
        Assert.NotNull(engage2.Target);

        var navs2 = h.Navigator.RecordedNavigationRequests.Snapshot();
        Assert.True(navs2.Count >= 2,
            $"Expected re-path to mob's new position on tick 2. Navs: {FormatNavRequests(h)}");
        var lastNav2 = navs2[^1];
        Assert.Equal(mob2.X, lastNav2.Destination.X, precision: 1);
        Assert.Equal(mob2.Z, lastNav2.Destination.Z, precision: 1);

        // WHEN tick 3: mob idles at mob2; vnavmesh arrived 0.5m short → distance 0.5f (in range).
        h.GameState.ClearHostileActors();
        h.GameState.AddHostileActor(MakeHostile(9, 100u, distance: 0.5f, position: mob2));

        var tick3 = await h.Engine.Tick(CancellationToken.None);

        // THEN tick 3 is still Engage (controller in-range path). No third NavigateTo issued.
        // No NavigateTo(Location) at any tick.
        Assert.IsType<EngineAction.Engage>(tick3);
        var navs3 = h.Navigator.RecordedNavigationRequests.Snapshot();
        Assert.Equal(navs2.Count, navs3.Count); // count unchanged since tick 2 (no third nav)

        Assert.All(navs3, req =>
            Assert.False(
                req.Destination.X == 100f && req.Destination.Z == 100f,
                $"NavigateTo(Location) must never appear while a live target is tracked. Navs: {FormatNavRequests(h)}"));
    }

    // =========================================================================
    // EH-8 — non-combat step tick never reads GetHostileActors (D4/D6 regression)
    //         (EH-8 is a named guard; full coverage lives in CombatNonStarvationTests.cs)
    // =========================================================================

    [Fact]
    public async Task EH8_NonCombatStepTick_NeverReadsGetHostileActors()
    {
        // This test must pass immediately. It is a named guard for D4/D6.
        // The underlying behaviour is already covered by NS_* tests.

        var h = new EngineTestHarness();
        var qid = new QuestId(66408u);
        h.QuestState.SetQuestSequence(qid, 0);
        h.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var quest = new QuestDefinition
        {
            SchemaVersion = "1.0.0",
            Id = 66408u,
            Name = "D6 Guard Quest",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences =
            [
                new QuestSequence
                {
                    Sequence = 0,
                    Steps =
                    [
                        new TalkStep
                        {
                            Id = "d6-talk",
                            Target = new NpcLocation(1000u, 0, new Position3(0f, 0f, 0f))
                        }
                    ]
                }
            ]
        };
        h.Engine.StartQuest(quest);
        h.GameState.Reset();

        await h.Engine.Tick(CancellationToken.None);

        var reads = h.GameState.RecordedReads.Snapshot();
        Assert.False(reads.Any(r => r.Method == nameof(QuestForge.Adapters.State.IGameStateProvider.GetHostileActors)),
            $"GetHostileActors must never be called on a non-combat tick. Reads: {FormatReads(h)}");
    }
}
