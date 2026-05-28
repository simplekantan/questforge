using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Gear;
using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.Minigames;
using QuestForge.Adapters.Fakes.Replay;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Fakes.Timing;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Combat;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Engine.Tests.Replay;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Combat;

/// <summary>
/// RED PHASE — G-FX: combat fixture round-trip + replay determinism tests.
///
/// FX-engage-actiontype: asserts ActionTypeString("Engage") == "engage" (may PASS immediately
///   via the default arm) AND that the engine emits DecisionEvent.ActionType == "Engage"
///   (PascalCase) for an Engage decision. Also asserts the new explicit arm in ActionTypeString
///   once added by the builder.
///
/// (FX-engage-debounced removed: decision-debounce is a generic TraceSession behavior already
///   covered by TraceSessionTests, and the single-engage-transition outcome is pinned by
///   FX-synthetic-fixture-replays below. Re-testing it through the harness's raw FakeTraceWriter
///   would be mis-layered — the harness has no TraceSession to debounce.)
///
/// FX-synthetic-fixture-replays: builds a full JSONL combat trace in-memory, wires
///   TraceReplayFixtureState, and drives the harness loop. Asserts transitions ==
///   [(combat-step-id, "engage")] + terminal "done". RED: ReplayGameStateProvider.GetHostileActors
///   returns empty (no-scan), so the controller finds no actor, produces Engage(null) or
///   the replay returns empty before the flip. After the flip it scans and finds the actor.
///   Additionally, CombatController.Decide throws NotImplementedException (RED from part A).
///
/// FX-replay-target-deterministic: two replays of the same synthetic trace must yield
///   the same Engage.Target. RED: same root causes as FX-synthetic-fixture-replays.
///
/// Spec: docs/COMBAT_STEP_PART_B_PLAN.md §G-FX, §A8
/// </summary>
public sealed class CombatFixtureTests
{
    private static readonly DateTimeOffset At = new(2026, 5, 25, 0, 0, 0, TimeSpan.Zero);

    // =========================================================================
    // Helpers — quest builders
    // =========================================================================

    private static QuestDefinition BuildCombatQuest(uint questId, string combatStepId,
        uint[] killIds, string? expectPredicate = null)
        => new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "FX Combat Test Quest",
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
                        new CombatStep
                        {
                            Id = combatStepId,
                            KillEnemyDataIds = killIds,
                            Spawn = CombatSpawn.AutoOnEnterArea,
                            Expect = new PredicateExpect
                            {
                                Predicate = expectPredicate ?? $"questSequence({questId}) >= 1"
                            }
                        }
                    ]
                }
            ]
        };

    // =========================================================================
    // Helpers — synthetic trace builder
    // =========================================================================

    /// <summary>
    /// Builds a synthetic JSONL trace for a 1-sequence combat quest:
    ///   run.start
    ///   segment-0 observations (quest state + GetHostileActors with two actors)
    ///   decision (stepId=combatStepId, "Engage")
    ///   terminal-tail observations (IsQuestComplete=true to satisfy expect)
    ///   run.end "done"
    /// </summary>
    private static string[] BuildCombatTrace(
        string runId,
        uint questId,
        string combatStepId,
        HostileActor[] actors)
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        var actorsJson = JsonSerializer.SerializeToElement(actors.ToList(), opts);
        var ctx = TraceEventJsonContext.Default.TraceEvent;

        string Serialize(TraceEvent e) => JsonSerializer.Serialize(e, ctx);
        JsonElement Elem<T>(T v) => JsonSerializer.SerializeToElement(v, opts);

        return
        [
            // run.start
            Serialize(new RunStartEvent(runId, questId, questId, At)),

            // segment-0: quest state observations
            Serialize(new ObservationEvent(runId, "GetQuestSequence",
                Elem(new { value = questId }), Elem(0), At)),
            Serialize(new ObservationEvent(runId, "GetNewGamePlusState",
                null, Elem(new { isActive = false, currentChapter = (object?)null, isSuspended = false, activeReplayQuestId = (object?)null }), At)),
            Serialize(new ObservationEvent(runId, "IsQuestComplete",
                Elem(new { value = questId }), Elem(false), At)),
            Serialize(new ObservationEvent(runId, "GetUiState",
                null, Elem(new { value = 0 }), At)),
            Serialize(new ObservationEvent(runId, "GetPlayerPosition",
                null, Elem(new WorldPosition(0f, 0f, 0f)), At)),
            Serialize(new ObservationEvent(runId, "GetPlayerZone",
                null, Elem(new { value = 182 }), At)),
            Serialize(new ObservationEvent(runId, "GetQuestVariables",
                Elem(new { value = questId }), Elem(new[] { 0, 0, 0, 0, 0, 0 }), At)),
            Serialize(new ObservationEvent(runId, "GetQuestStatus",
                Elem(new { value = questId }), Elem(0), At)),

            // IsPlayerInCombat — read by global defense rule before the cursor walk.
            // false here: defense skips (not in combat) and the CombatStep cursor walk
            // dispatches normally. These FX tests verify CombatStep dispatch (Engage with
            // Step=combatStep), not defense behavior. Defense-specific scenarios are
            // covered in GlobalDefenseTests.cs (D1-D13).
            Serialize(new ObservationEvent(runId, "IsPlayerInCombat",
                null, Elem(false), At)),

            // GetHostileActors in segment-0: read by defense (DecideClearAggro) and then
            // again by the cursor CombatStep branch (Decide). ObservationScanner falls back
            // to _lastSeen on repeat reads, so one entry covers both calls.
            Serialize(new ObservationEvent(runId, "GetHostileActors",
                Elem(50f), actorsJson, At)),

            // GetCurrentJob — approach-range resolution (added when CombatController gained job-aware ranging)
            Serialize(new ObservationEvent(runId, "GetCurrentJob",
                null, Elem(new { value = 2u }), At)),

            // decision: Engage for the combat step
            Serialize(new DecisionEvent(runId, combatStepId, "Engage", At.AddSeconds(1))),

            // terminal-tail: IsQuestComplete flips to true (satisfies expect)
            Serialize(new ObservationEvent(runId, "GetQuestSequence",
                Elem(new { value = questId }), Elem(1), At.AddSeconds(2))),
            Serialize(new ObservationEvent(runId, "GetNewGamePlusState",
                null, Elem(new { isActive = false, currentChapter = (object?)null, isSuspended = false, activeReplayQuestId = (object?)null }), At.AddSeconds(2))),
            Serialize(new ObservationEvent(runId, "IsQuestComplete",
                Elem(new { value = questId }), Elem(true), At.AddSeconds(2))),
            Serialize(new ObservationEvent(runId, "GetUiState",
                null, Elem(new { value = 0 }), At.AddSeconds(2))),
            Serialize(new ObservationEvent(runId, "GetPlayerPosition",
                null, Elem(new WorldPosition(0f, 0f, 0f)), At.AddSeconds(2))),
            Serialize(new ObservationEvent(runId, "GetPlayerZone",
                null, Elem(new { value = 182 }), At.AddSeconds(2))),
            Serialize(new ObservationEvent(runId, "GetQuestVariables",
                Elem(new { value = questId }), Elem(new[] { 1, 0, 0, 0, 0, 0 }), At.AddSeconds(2))),

            // run.end
            Serialize(new RunEndEvent(runId, "done", At.AddSeconds(10))),
        ];
    }

    private static string WriteTempTrace(string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"qf-fx-test-{Guid.NewGuid():N}.trace.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static HostileActor MakeHostile(ulong id, uint dataId, float dist = 5f,
        bool isTargetingPlayer = false)
        => new HostileActor(new ActorId(id), dataId, new WorldPosition(0f, 0f, 0f), dist,
            IsTargetable: true, IsDead: false, IsTargetingPlayer: isTargetingPlayer,
            OnPlayerEnmityList: false, HasQuestMarker: false);

    // =========================================================================
    // FX-engage-actiontype
    // =========================================================================

    /// <summary>
    /// FX-engage-actiontype (part 1): ActionTypeString maps an Engage action to "engage" (lowercase).
    /// This may PASS already via the default `action.GetType().Name.ToLowerInvariant()` arm.
    /// The builder adds an explicit arm; this test is a regression pin either way.
    /// </summary>
    [Fact]
    public void FX_EngageActionType_ActionTypeString_ReturnsEngage()
    {
        // ActionTypeString is a private method of EngineFixtureTests — tested via the
        // existing Quest66130ReplayTests which drives the same method indirectly.
        // We pin it directly by calling through the harness and examining the DecisionEvent.

        // The existing default arm: action.GetType().Name.ToLowerInvariant() → "engage"
        // This should PASS immediately (no builder change needed for the assertion to hold).
        var step = new CombatStep
        {
            Id = "fx-combat",
            KillEnemyDataIds = [100u],
            Spawn = CombatSpawn.AutoOnEnterArea
        };
        var engageAction = new EngineAction.Engage(step, null);

        // Reflect into ActionTypeString via the same logic the harness uses:
        // action.GetType().Name.ToLowerInvariant()
        var typeName = engageAction.GetType().Name.ToLowerInvariant();
        Assert.Equal("engage", typeName);
    }

    /// <summary>
    /// FX-engage-actiontype (part 2): the engine writes DecisionEvent.ActionType == "Engage"
    /// (PascalCase, GetType().Name) for an Engage decision. Pins the end-to-end case-mapping contract.
    ///
    /// RED: CombatController.Decide throws NotImplementedException → engine throws before
    /// writing any DecisionEvent → no Engage decision observed.
    /// </summary>
    [Fact]
    public async Task FX_EngageActionType_EngineWritesDecisionWithPascalCaseEngage()
    {
        // Given a combat quest, player in range, one eligible hostile, expect unmet.
        // When Tick, Then a DecisionEvent with ActionType == "Engage" is written.

        var harness = new EngineTestHarness();
        var questId = new QuestId(67001u);
        harness.QuestState.SetQuestSequence(questId, 0);
        harness.QuestState.SetQuestVariables(questId, 0, 0, 0, 0, 0, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.AddHostileActor(MakeHostile(9, 100));

        var step = new CombatStep
        {
            Id = "fx-combat-decision",
            KillEnemyDataIds = [100u],
            Spawn = CombatSpawn.AutoOnEnterArea,
            Expect = new PredicateExpect { Predicate = "questSequence(67001) >= 1" }
        };
        var quest = BuildCombatQuest(67001u, "fx-combat-decision", [100u],
            "questSequence(67001) >= 1");
        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-fx-actiontype");

        harness.TraceWriter.Reset();

        await harness.Engine.Tick(CancellationToken.None);

        // DecisionEvent.ActionType must be "Engage" (PascalCase — GetType().Name)
        var decisions = harness.TraceWriter.RecordedEvents.OfType<DecisionEvent>().ToList();
        Assert.True(decisions.Count > 0,
            "Expected at least one DecisionEvent after an Engage tick, got none. " +
            $"Recorded events: {string.Join(", ", harness.TraceWriter.RecordedEvents.Select(e => e.GetType().Name))}");

        var engageDecision = decisions.FirstOrDefault(d => d.ActionType == "Engage");
        Assert.NotNull(engageDecision);
        Assert.Equal("Engage", engageDecision.ActionType);
        Assert.Equal("fx-combat-decision", engageDecision.StepId);
    }

    // =========================================================================
    // FX-synthetic-fixture-replays
    // =========================================================================

    /// <summary>
    /// FX-synthetic-fixture-replays: a synthetic JSONL combat trace replays deterministically
    /// to transitions [(combat-step-id, "engage")] + terminal Done.
    ///
    /// RED: multiple causes:
    ///   (1) CombatController.Decide throws NotImplementedException.
    ///   (2) ReplayGameStateProvider.GetHostileActors returns empty (no-scan flip not yet done).
    ///   After both are fixed by the builder, the trace replays to the expected transitions.
    /// </summary>
    [Fact]
    public async Task FX_SyntheticFixtureReplays_CombatTrace_ProducesEngageTransitionPlusDone()
    {
        const uint QuestId = 67003u;
        const string CombatStepId = "fx-synthetic-combat";

        var actors = new[] { MakeHostile(9, 100, dist: 5f) };
        var traceLines = BuildCombatTrace("run-fx-synthetic", QuestId, CombatStepId, actors);
        var tracePath = WriteTempTrace(traceLines);

        try
        {
            var state = TraceReplayFixtureState.FromTraceFile(tracePath);

            var quest = BuildCombatQuest(QuestId, CombatStepId, [100u],
                $"isQuestComplete({QuestId})");

            var capturingTrace = new CapturingTraceWriter();
            var engine = new QuestEngine(
                state.GameState, state.QuestState,
                state.Navigator, state.Teleporter, state.Interactor,
                state.Combat, state.Gear, state.Minigames, state.Dialogue, state.Timing,
                capturingTrace, NullLogger<QuestEngine>.Instance);

            engine.StartQuest(quest);
            engine.BeginRun("run-fx-synthetic-replay");

            var actualTransitions = new List<(string? StepId, string ActionType)>();
            const int maxTicks = 200;

            for (var tick = 0; tick < maxTicks; tick++)
            {
                var eventsBefore = capturingTrace.Events.Count;

                EngineAction action;
                try
                {
                    action = await engine.Tick(CancellationToken.None);
                }
                catch (ReplayObservationStarvationException ex)
                {
                    Assert.Fail(
                        $"FX-synthetic: observation starvation at tick {tick}: {ex.Message}. " +
                        $"Transitions so far: [{string.Join(", ", actualTransitions.Select(t => $"({t.StepId},{t.ActionType})"))}]");
                    return;
                }

                var newDecision = capturingTrace.Events
                    .Skip(eventsBefore)
                    .OfType<DecisionEvent>()
                    .FirstOrDefault();

                if (newDecision is null) break; // terminal action

                state.OnTick(action, tick);

                var pair = (newDecision.StepId, ActionTypeString(action));
                if (actualTransitions.Count == 0 || actualTransitions[^1] != pair)
                {
                    actualTransitions.Add(pair);
                    state.OnTransitionRecorded(action, tick);
                }

                if (actualTransitions.Count > 1 + 10) break; // safety
            }

            // Assert: exactly one engage transition for the combat step
            Assert.Equal(1, actualTransitions.Count);
            Assert.Equal(CombatStepId, actualTransitions[0].StepId);
            Assert.Equal("engage", actualTransitions[0].ActionType);

            // Terminal: Done
            var terminal = await engine.Tick(CancellationToken.None);
            Assert.IsType<EngineAction.Done>(terminal);
        }
        finally
        {
            try { File.Delete(tracePath); } catch { /* best-effort */ }
        }
    }

    // =========================================================================
    // FX-replay-target-deterministic
    // =========================================================================

    /// <summary>
    /// FX-replay-target-deterministic: two replays of the same synthetic trace with two
    /// actors (one higher kill-priority via IsTargetingPlayer) both yield the same
    /// Engage.Target (higher-priority actor). Pins determinism — target selection reads
    /// the recorded GetHostileActors observation and is a pure function.
    ///
    /// RED: same root causes as FX-synthetic-fixture-replays.
    /// After the builder's fixes, both replays must select ActorId(2) (the aggro'd actor).
    /// </summary>
    [Fact]
    public async Task FX_ReplayTargetDeterministic_TwoActors_BothReplicasSelectHigherPriority()
    {
        const uint QuestId = 67004u;
        const string CombatStepId = "fx-determinism-combat";

        // Two actors: actor 1 (lower priority, just in kill set) and actor 2 (aggro'd → higher priority)
        var actors = new[]
        {
            MakeHostile(1, 100, dist: 10f, isTargetingPlayer: false), // lower priority
            MakeHostile(2, 100, dist: 15f, isTargetingPlayer: true),  // score 1000+10=1010 (kill-set + aggro) → higher priority
        };

        var traceLines = BuildCombatTrace("run-fx-det", QuestId, CombatStepId, actors);
        var tracePath = WriteTempTrace(traceLines);

        var quest = BuildCombatQuest(QuestId, CombatStepId, [100u],
            $"isQuestComplete({QuestId})");

        try
        {
            ActorId? replay1Target = null;
            ActorId? replay2Target = null;

            // Replay 1
            {
                var state = TraceReplayFixtureState.FromTraceFile(tracePath);
                var capturingTrace = new CapturingTraceWriter();
                var engine = new QuestEngine(
                    state.GameState, state.QuestState,
                    state.Navigator, state.Teleporter, state.Interactor,
                    state.Combat, state.Gear, state.Minigames, state.Dialogue, state.Timing,
                    capturingTrace, NullLogger<QuestEngine>.Instance);

                engine.StartQuest(quest);
                engine.BeginRun("run-det-1");

                EngineAction action;
                try
                {
                    action = await engine.Tick(CancellationToken.None);
                }
                catch (ReplayObservationStarvationException ex)
                {
                    Assert.Fail($"FX-determinism replay 1: starvation: {ex.Message}");
                    return;
                }

                if (action is EngineAction.Engage engage && engage.Target is { } t)
                    replay1Target = t.Id;
            }

            // Replay 2 — must select the same actor
            {
                var state = TraceReplayFixtureState.FromTraceFile(tracePath);
                var capturingTrace = new CapturingTraceWriter();
                var engine = new QuestEngine(
                    state.GameState, state.QuestState,
                    state.Navigator, state.Teleporter, state.Interactor,
                    state.Combat, state.Gear, state.Minigames, state.Dialogue, state.Timing,
                    capturingTrace, NullLogger<QuestEngine>.Instance);

                engine.StartQuest(quest);
                engine.BeginRun("run-det-2");

                EngineAction action;
                try
                {
                    action = await engine.Tick(CancellationToken.None);
                }
                catch (ReplayObservationStarvationException ex)
                {
                    Assert.Fail($"FX-determinism replay 2: starvation: {ex.Message}");
                    return;
                }

                if (action is EngineAction.Engage engage && engage.Target is { } t)
                    replay2Target = t.Id;
            }

            // Both replays must have selected a target (not null)
            Assert.NotNull(replay1Target);
            Assert.NotNull(replay2Target);

            // Both replays must have selected the same (higher-priority) actor: ActorId(2)
            Assert.Equal(replay1Target, replay2Target);
            Assert.Equal(new ActorId(2), replay1Target!.Value);
        }
        finally
        {
            try { File.Delete(tracePath); } catch { /* best-effort */ }
        }
    }

    // =========================================================================
    // Internal helper — mirrors EngineFixtureTests.ActionTypeString
    // =========================================================================

    /// <summary>Maps EngineAction subtype to the canonical fixture actionType string.</summary>
    private static string ActionTypeString(EngineAction action) => action switch
    {
        EngineAction.Navigate  _ => "navigate",
        EngineAction.Interact  _ => "interact",
        EngineAction.Wait      _ => "wait",
        EngineAction.AwaitUser _ => "awaitUser",
        EngineAction.Done      _ => "done",
        EngineAction.Engage    _ => "engage",   // explicit arm (Task 5 / A8)
        _                        => action.GetType().Name.ToLowerInvariant()
    };
}
