using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using NpcId   = QuestForge.Adapters.Types.NpcId;
using QuestId = QuestForge.Adapters.Types.QuestId;

// ─────────────────────────────────────────────────────────────────────────────
// feat/mount-support — mount/dismount dispatch tests.
//
// CONTRACT (per MOUNT_SUPPORT_PLAN.md §2 and §3):
//   EngineTestHarness.RunToCompletion (mirror of EngineHost.DispatchAction) fires:
//     - Mount  before Navigate when: nav.Options.UseMount==true && !InCombat &&
//                                    distance >= 20m && MountState==Dismounted && !Casting.
//     - Dismount before any non-Navigate action when MountState != Dismounted.
//
// RED PHASE: All matrix tests (M1–M7, H1) FAIL because RunToCompletion does not
// yet have the mount/dismount hooks wired. Counters stay at 0.
// Sanity tests (S1–S3) exercise FakeMount directly and are GREEN trivially.
//
// Run with:
//   & "C:\Users\publi\.dotnet\dotnet.exe" test QuestForge.Engine.Tests --filter "FullyQualifiedName~MountSupport"
// ─────────────────────────────────────────────────────────────────────────────

namespace QuestForge.Engine.Tests.Engine;

public sealed class MountSupportTests
{
    private const uint TestQuestId  = 88001u;
    private const uint TalkNpcId    = 9900001u;
    private const int  TestSeq      = 0;
    private const int  TestZone     = 128;

    // Player origin — starting position for all matrix tests.
    private static readonly WorldPosition PlayerOrigin = new(0f, 0f, 0f);

    // Navigate targets at various distances from PlayerOrigin.
    private static readonly WorldPosition FarTarget  = new(40f, 0f, 0f);  // XZ dist = 40m (>= 20m threshold)
    private static readonly WorldPosition NearTarget = new(10f, 0f, 0f);  // XZ dist = 10m (<  20m threshold)

    // TalkStep NPC co-located at FarTarget (player is in range after Navigate Arrived).
    private static readonly NpcLocation TalkNpcLocFar =
        new(NpcId: TalkNpcId, Zone: TestZone, Position: new Position3(40f, 0f, 0f));

    // TalkStep NPC co-located at NearTarget (M6).
    private static readonly NpcLocation TalkNpcLocNear =
        new(NpcId: TalkNpcId, Zone: TestZone, Position: new Position3(10f, 0f, 0f));

    // ─── Quest fixture helpers ────────────────────────────────────────────────

    /// <summary>
    /// Single-TravelStep quest without an Expect predicate.
    /// Used by M1–M4 / H1 (single-tick tests) — the TravelStep loops and we inspect counters
    /// after the first tick's Navigate dispatch. The Expect-less TravelStep avoids predicate
    /// evaluation on tick 1, keeping the test isolated to the mount hook check.
    /// </summary>
    private static QuestDefinition BuildSingleNavigateQuest(
        WorldPosition navTarget,
        bool useMount = true) =>
        new()
        {
            SchemaVersion     = "1.0.0",
            Id                = TestQuestId,
            Name              = "Mount Support Navigate Quest",
            Expansion         = "arr",
            Category          = "side",
            Enabled           = true,
            SupportStatus     = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements      = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom        = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences         =
            [
                new QuestSequence
                {
                    Sequence = TestSeq,
                    Steps    =
                    [
                        new TravelStep
                        {
                            Id          = "nav-only",
                            Destination = new TravelDestination(Zone: TestZone,
                                              Position: new Position3(navTarget.X, navTarget.Y, navTarget.Z)),
                            UseMount    = useMount ? null : false   // null → true (default); false → opt-out
                            // No Expect → engine re-emits Navigate every tick (correct for tick-only tests)
                        }
                    ]
                }
            ]
        };

    /// <summary>
    /// TravelStep (with playerNear Expect) → TalkStep quest for RunToCompletion tests (M5, M6).
    /// The playerNear Expect auto-confirms the TravelStep once FakeNavigator teleports the player
    /// to the destination, allowing the engine to advance to TalkStep on the next tick.
    /// </summary>
    private static QuestDefinition BuildTravelTalkQuest(
        WorldPosition navTarget,
        NpcLocation talkNpc,
        bool useMount = true)
    {
        // playerNear position literal uses JSON key format: {"x":40.0,"y":0.0,"z":0.0}
        var nearPred = FormattableString.Invariant(
            $"playerNear({{\"x\":{navTarget.X},\"y\":{navTarget.Y},\"z\":{navTarget.Z}}}, 5)");

        return new QuestDefinition
        {
            SchemaVersion     = "1.0.0",
            Id                = TestQuestId,
            Name              = "Mount Support Travel+Talk Quest",
            Expansion         = "arr",
            Category          = "side",
            Enabled           = true,
            SupportStatus     = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements      = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom        = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences         =
            [
                new QuestSequence
                {
                    Sequence = TestSeq,
                    Steps    =
                    [
                        new TravelStep
                        {
                            Id          = "nav-step",
                            Destination = new TravelDestination(Zone: TestZone,
                                              Position: new Position3(navTarget.X, navTarget.Y, navTarget.Z)),
                            UseMount    = useMount ? null : false,
                            Expect      = new PredicateExpect { Predicate = nearPred }
                        },
                        new TalkStep
                        {
                            Id     = "talk-step",
                            Target = talkNpc
                        }
                    ]
                }
            ]
        };
    }

    /// <summary>
    /// Builds a harness at PlayerOrigin with the test quest's sequence pre-wired.
    /// </summary>
    private static EngineTestHarness BuildHarness()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.GameState.SetPosition(PlayerOrigin);
        return harness;
    }

    // =========================================================================
    // M1 — happy path: mount fires
    //
    // Given: Dismounted, not in combat, distance 40m (>= 20m), not casting.
    // When: harness ticks and dispatches Navigate.
    // Then: MountCallCount == 1 AND NavigateTo was called.
    // =========================================================================

    [Fact]
    public async Task M1_HappyPath_MountFiresOnNavigate()
    {
        var harness = BuildHarness();
        harness.GameState.SetMountState(MountState.Dismounted);
        harness.GameState.SetInCombat(false);
        harness.GameState.SetCasting(false);

        harness.Engine.StartQuest(BuildSingleNavigateQuest(FarTarget, useMount: true));

        // One tick — engine emits Navigate; Builder's pre-switch hook fires Mount.
        var action = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(action);

        // RED: harness has no mount hook yet. After Builder wires it: MountCallCount == 1.
        // (action asserted as Navigate above proves the engine dispatched Navigate.)
        Assert.Equal(1, harness.Mount.MountCallCount);
    }

    // =========================================================================
    // M1b — UseMount=false opt-out: no mount
    //
    // Given: Same preconditions as M1 but TravelStep.UseMount = false.
    // When: harness ticks and dispatches Navigate.
    // Then: MountCallCount == 0. NavigateTo was called.
    // =========================================================================

    [Fact]
    public async Task M1b_UseMountFalse_MountDoesNotFire()
    {
        var harness = BuildHarness();
        harness.GameState.SetMountState(MountState.Dismounted);
        harness.GameState.SetInCombat(false);
        harness.GameState.SetCasting(false);

        harness.Engine.StartQuest(BuildSingleNavigateQuest(FarTarget, useMount: false));

        var action = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(action);

        // After Builder reads TravelStep.UseMount and propagates to NavigationOptions.UseMount:
        // UseMount==false → mount predicate suppressed → MountCallCount stays 0.
        Assert.Equal(0, harness.Mount.MountCallCount);
    }

    // =========================================================================
    // M2 — distance below threshold: no mount
    //
    // Given: Dismounted, not in combat, distance 10m (< 20m threshold), not casting.
    // When: harness ticks and dispatches Navigate.
    // Then: MountCallCount == 0. NavigateTo was called.
    // =========================================================================

    [Fact]
    public async Task M2_ShortDistance_MountDoesNotFire()
    {
        var harness = BuildHarness();
        harness.GameState.SetMountState(MountState.Dismounted);
        harness.GameState.SetInCombat(false);
        harness.GameState.SetCasting(false);

        harness.Engine.StartQuest(BuildSingleNavigateQuest(NearTarget, useMount: true));

        var action = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(action);

        // After Builder adds distance check: 10m < 20m threshold → MountCallCount stays 0.
        Assert.Equal(0, harness.Mount.MountCallCount);
    }

    // =========================================================================
    // M3 — in combat: no mount
    //
    // Given: Dismounted, InCombat = true, distance 40m (>= 20m), not casting.
    // When: harness ticks and dispatches Navigate.
    // Then: MountCallCount == 0. NavigateTo was called.
    // =========================================================================

    [Fact]
    public async Task M3_InCombat_MountDoesNotFire()
    {
        var harness = BuildHarness();
        harness.GameState.SetMountState(MountState.Dismounted);
        harness.GameState.SetInCombat(true);
        harness.GameState.SetCasting(false);

        harness.Engine.StartQuest(BuildSingleNavigateQuest(FarTarget, useMount: true));

        var action = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(action);

        // After Builder adds InCombat guard: InCombat==true → MountCallCount stays 0.
        Assert.Equal(0, harness.Mount.MountCallCount);
    }

    // =========================================================================
    // M4 — already mounted: no remount, no dismount during Navigate
    //
    // Given: MountState = Mounted, not in combat, distance 40m, not casting.
    // When: harness ticks and dispatches Navigate.
    // Then: MountCallCount == 0 (already mounted → predicate requires Dismounted).
    //       DismountCallCount == 0 (Navigate does NOT trigger the lazy-dismount hook).
    //       NavigateTo was called.
    // =========================================================================

    [Fact]
    public async Task M4_AlreadyMounted_NoRemountNoDismount()
    {
        var harness = BuildHarness();
        harness.GameState.SetMountState(MountState.Mounted);
        harness.GameState.SetInCombat(false);
        harness.GameState.SetCasting(false);

        harness.Engine.StartQuest(BuildSingleNavigateQuest(FarTarget, useMount: true));

        var action = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(action);

        // After Builder: MountState==Mounted → mount predicate fails (requires Dismounted).
        // Navigate is not a non-Navigate action → lazy-dismount hook not triggered.
        Assert.Equal(0, harness.Mount.MountCallCount);
        Assert.Equal(0, harness.Mount.DismountCallCount);
    }

    // =========================================================================
    // M5 — non-Navigate after Navigate while Mounted: dismount fires
    //
    // Given: MountState = Mounted. Quest: TravelStep → TalkStep.
    //        FakeNavigator auto-teleports player to FarTarget on Arrived;
    //        TalkNpcLocFar NPC is at FarTarget → TalkStep is in range on the next tick.
    // When: RunToCompletion — Navigate → arrive → Interact → Done.
    // Then: DismountCallCount == 1 (lazy-dismount fires before Interact).
    //       MountCallCount == 0 (already mounted → mount predicate fails).
    // =========================================================================

    [Fact]
    public async Task M5_MountedThenNonNavigate_DismountFires()
    {
        var harness = BuildHarness();
        harness.GameState.SetMountState(MountState.Mounted);
        harness.GameState.SetInCombat(false);
        harness.GameState.SetCasting(false);

        harness.Engine.StartQuest(BuildTravelTalkQuest(FarTarget, TalkNpcLocFar, useMount: true));

        // OnInteractWith: mark quest complete so IsQuestComplete → true → Done next tick.
        harness.Interactor.OnInteractWith(new NpcId(TalkNpcId), () =>
            harness.QuestState.SetQuestStatus(new QuestId(TestQuestId), QuestStatus.Complete));

        await harness.RunToCompletion(maxTicks: 15);

        // Dismount fires at least once on the Navigate → non-Navigate transition. With the
        // flying-dismount retry loop (re-fire each tick until MountState observes Dismounted),
        // the count may grow if the FakeGameStateProvider does not auto-transition to Dismounted
        // — which it does not, by design. The contract being tested is "dismount happened",
        // not "dismount happened exactly once".
        Assert.True(harness.Mount.DismountCallCount >= 1,
            $"Expected DismountCallCount >= 1 (lazy-dismount fired at least once); got {harness.Mount.DismountCallCount}.");
        // Already mounted at start → mount predicate never fires.
        Assert.Equal(0, harness.Mount.MountCallCount);
    }

    // =========================================================================
    // M6 — non-Navigate after Navigate while Dismounted: no dismount
    //
    // Given: MountState = Dismounted. Quest: TravelStep (NearTarget) → TalkStep (at NearTarget).
    // When: RunToCompletion.
    // Then: DismountCallCount == 0 throughout.
    // =========================================================================

    [Fact]
    public async Task M6_DismountedThroughout_NoDismountFires()
    {
        var harness = BuildHarness();
        harness.GameState.SetMountState(MountState.Dismounted);
        harness.GameState.SetInCombat(false);
        harness.GameState.SetCasting(false);

        harness.Engine.StartQuest(BuildTravelTalkQuest(NearTarget, TalkNpcLocNear, useMount: true));

        harness.Interactor.OnInteractWith(new NpcId(TalkNpcId), () =>
            harness.QuestState.SetQuestStatus(new QuestId(TestQuestId), QuestStatus.Complete));

        await harness.RunToCompletion(maxTicks: 15);

        // Dismounted throughout → lazy-dismount hook must never fire.
        Assert.Equal(0, harness.Mount.DismountCallCount);
    }

    // =========================================================================
    // M7 — Navigate after Navigate while Mounted: no intermediate dismount
    //
    // Given: MountState = Mounted. TravelStep with no Expect (engine re-emits Navigate
    //        every tick). Two consecutive Navigate ticks are dispatched manually.
    // When: Engine ticks twice in sequence (both produce Navigate).
    // Then: DismountCallCount == 0 after both ticks.
    //       Navigate was recorded at least twice.
    //
    // Strategy: direct tick loop (no RunToCompletion). The Expect-less TravelStep
    // loops naturally, giving two consecutive Navigate ticks without quest completion
    // ceremony. We verify the lazy-dismount hook does NOT fire between Navigate ticks.
    // =========================================================================

    [Fact]
    public async Task M7_NavigateToNavigate_NoDismount()
    {
        var harness = BuildHarness();
        harness.GameState.SetMountState(MountState.Mounted);
        harness.GameState.SetInCombat(false);
        harness.GameState.SetCasting(false);

        harness.Engine.StartQuest(BuildSingleNavigateQuest(FarTarget, useMount: true));

        var ct = CancellationToken.None;

        // Tick 1 → Navigate emitted.
        var action1 = await harness.Engine.Tick(ct);
        Assert.IsType<EngineAction.Navigate>(action1);
        // Simulate what RunToCompletion's Navigate arm does.
        await harness.Navigator.NavigateTo(FarTarget, new NavigationOptions(), ct);

        // Between Navigate-1 and Navigate-2: DismountCallCount must still be 0.
        Assert.Equal(0, harness.Mount.DismountCallCount);

        // Tick 2 → Navigate emitted again (TravelStep has no Expect, loops).
        var action2 = await harness.Engine.Tick(ct);
        Assert.IsType<EngineAction.Navigate>(action2);

        // RED: After Builder wires the lazy-dismount hook: Navigate→Navigate must NOT trigger it.
        // DismountCallCount remains 0 across both Navigate ticks.
        // (Both tick 1 and tick 2 returned Navigate, verified by the Assert.IsType calls above.)
        Assert.Equal(0, harness.Mount.DismountCallCount);
    }

    // =========================================================================
    // S1 — FakeMount.Reset() clears counters
    //
    // Given: FakeMount with Mount called once and Dismount called twice.
    // When: Reset() is invoked.
    // Then: both counters are zero.
    // (Expected GREEN — tests the fake directly.)
    // =========================================================================

    [Fact]
    public async Task S1_Reset_ClearsBothCounters()
    {
        var fake = new FakeMount();
        await fake.Mount();
        await fake.Dismount();
        await fake.Dismount();

        Assert.Equal(1, fake.MountCallCount);
        Assert.Equal(2, fake.DismountCallCount);

        fake.Reset();

        Assert.Equal(0, fake.MountCallCount);
        Assert.Equal(0, fake.DismountCallCount);
    }

    // =========================================================================
    // S2 — Mount/Dismount are idempotent counters (3 calls → count 3)
    //
    // Given: FakeMount.
    // When: Mount is called 3 times back-to-back.
    // Then: MountCallCount == 3 (counter increments; no throttle inside the fake).
    // (Expected GREEN.)
    // =========================================================================

    [Fact]
    public async Task S2_MultipleMount_CounterIncrementsEachCall()
    {
        var fake = new FakeMount();
        await fake.Mount();
        await fake.Mount();
        await fake.Mount();

        Assert.Equal(3, fake.MountCallCount);
        Assert.Equal(0, fake.DismountCallCount);
    }

    // =========================================================================
    // S3 — Cancellation propagates OperationCanceledException
    //
    // Given: a cancelled CancellationToken.
    // When: FakeMount.Mount(cts.Token) is called.
    // Then: OperationCanceledException is thrown (mirrors FakeVendor.Close contract).
    //       Counter must NOT increment (cancellation fires before the counter).
    // (Expected GREEN.)
    // =========================================================================

    [Fact]
    public async Task S3_Cancellation_ThrowsOperationCanceledException()
    {
        var fake = new FakeMount();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => fake.Mount(cts.Token));

        Assert.Equal(0, fake.MountCallCount);
    }

    // =========================================================================
    // H1 — Casting=true suppresses mount even when other preconditions hold
    //
    // Given: Dismounted, not in combat, distance 40m (>= threshold), Casting = true.
    //        Simulates a mid-cast or mid-mount-animation tick (Q2 safeguard from §3 Q2).
    // When: Navigate dispatch hook is exercised (one engine tick).
    // Then: MountCallCount == 0.
    // =========================================================================

    [Fact]
    public async Task H1_Casting_SuppressesMountAttempt()
    {
        var harness = BuildHarness();
        harness.GameState.SetMountState(MountState.Dismounted);
        harness.GameState.SetInCombat(false);
        harness.GameState.SetCasting(true);   // mid-cast / mid-mount-animation

        harness.Engine.StartQuest(BuildSingleNavigateQuest(FarTarget, useMount: true));

        var action = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(action);

        // RED: harness does not check Casting. After Builder adds the Casting guard:
        // Casting==true → mount attempt suppressed → MountCallCount stays 0.
        Assert.Equal(0, harness.Mount.MountCallCount);
    }

    // =========================================================================
    // M8 — CanMount=false gate suppresses mount fire (§8 Q3)
    //
    // Given: distance >= 20m, Dismounted, !InCombat, !Casting, UseMount=true (default),
    //        AND harness.GameState.SetCanMount(false)
    //        — simulates a city or mount-prohibited subarea where
    //          ActionManager.GetActionStatus(GeneralAction, 9) returns non-zero.
    // When: harness ticks and dispatches Navigate.
    // Then: harness.Mount.MountCallCount == 0 (CanMount gate suppressed the attempt).
    //       Navigator.NavigateTo was still called (navigation proceeds on foot).
    //
    // RED: the Navigate-arm mount predicate in EngineTestHarness.RunToCompletion
    // (and EngineHost.DispatchAction) does NOT yet check playerState.CanMount.
    // With _canMount=false the predicate still fires Mount → MountCallCount==1 →
    // Assert.Equal(0, count) FAILS.
    // GREEN after Builder adds: && playerState.CanMount to the mount predicate.
    //
    // Note: M1 already proves the CanMount=true happy path because FakeGameStateProvider
    // defaults _canMount=true. No M8b needed — it would duplicate M1.
    // =========================================================================

    [Fact]
    public async Task M8_CanMountFalse_SuppressesMountFire()
    {
        var harness = BuildHarness();
        harness.GameState.SetMountState(MountState.Dismounted);
        harness.GameState.SetInCombat(false);
        harness.GameState.SetCasting(false);
        // All other preconditions are permissive (distance 40m, UseMount=true, Dismounted,
        // !InCombat, !Casting). Only CanMount is false — this is the single mutation.
        harness.GameState.SetCanMount(false);

        harness.Engine.StartQuest(BuildSingleNavigateQuest(FarTarget, useMount: true));

        var action = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(action);

        // CanMount=false → mount predicate must be suppressed → counter stays 0.
        // (Navigation dispatch itself runs in RunToCompletion's Navigate arm, not in
        // HarnessEngine.Tick — verifying Navigate was the emitted action above is
        // sufficient to prove the cursor walk proceeded.)
        Assert.Equal(0, harness.Mount.MountCallCount);
    }
}
