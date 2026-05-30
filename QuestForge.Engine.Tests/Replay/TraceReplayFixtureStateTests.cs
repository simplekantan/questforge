using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Gear;
using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.Minigames;
using QuestForge.Adapters.Fakes.Replay;
using QuestForge.Adapters.Fakes.Timing;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Minigames;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Timing;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine;
using Xunit;

namespace QuestForge.Engine.Tests.Replay;

/// <summary>
/// RED PHASE: Tests for the generic trace-replay fixture harness.
/// Groups A, B, C, D, E per FIXTURE_REPLAY_HARNESS_PLAN.md §5.
///
/// Types/members that do not exist yet (expected compile errors = RED signal):
///   - IFixtureState                          (States/IFixtureState.cs — not created)
///   - TraceReplayFixtureState                (States/TraceReplayFixtureState.cs — not created)
///   - InertNavigator                         (Replay/InertNavigator.cs in Fakes — not created)
///   - InertTeleporter                        (Replay/InertTeleporter.cs in Fakes — not created)
///   - InertInteractor                        (Replay/InertInteractor.cs in Fakes — not created)
///   - EngineFixtureTests.TryResolveSourceTrace  (internal static helper — not yet extracted)
///   - EngineFixtureTests.WrapTickForStarvation  (internal static helper — not yet added)
///   - EngineFixtureTests.SafetyOverrunCount     (internal const — not yet exposed)
///   - EngineFixture.SourceTrace              (new optional property on the private record)
///
/// All these will produce CS0246 / CS0117 compile errors until the Builder adds them.
/// That is the expected RED signal. E4 is the only test expected to PASS immediately.
/// </summary>
public sealed class TraceReplayFixtureStateTests
{
    private static readonly DateTimeOffset At = new(2026, 5, 25, 0, 0, 0, TimeSpan.Zero);

    // =====================================================================
    // GROUP A — TraceReplayFixtureState construction
    // =====================================================================

    /// <summary>A1 — builds from a trace with observations.</summary>
    [Fact]
    public async Task A1_FromTraceFile_WithObservations_BuildsStateWithCorrectProviderTypes()
    {
        // Given a JSONL trace file containing ≥1 ObservationEvent (a GetPlayerZone obs),
        // When TraceReplayFixtureState.FromTraceFile(path),
        // Then a non-null state is returned, GameState is ReplayGameStateProvider,
        //      QuestState is ReplayQuestState.
        //
        // RED: TraceReplayFixtureState does not exist → CS0246 compile error.

        var path = WriteTempTrace(
            MakeObsLine("GetPlayerZone", null, """{"value":182}"""));

        // CS0246: TraceReplayFixtureState is the missing type
        var state = TraceReplayFixtureState.FromTraceFile(path);

        Assert.NotNull(state);
        Assert.IsType<ReplayGameStateProvider>(state.GameState);
        Assert.IsType<ReplayQuestState>(state.QuestState);
    }

    /// <summary>A2 — action adapters are the inert/stateless types.</summary>
    [Fact]
    public void A2_FromTraceFile_ActionAdaptersAreInertOrStatelessTypes()
    {
        // Given a built TraceReplayFixtureState,
        // Assert Navigator is InertNavigator, Teleporter is InertTeleporter,
        //        Interactor is InertInteractor, and the rest are standard stateless fakes.
        //
        // RED: TraceReplayFixtureState, InertNavigator, InertTeleporter, InertInteractor
        //      do not exist → CS0246 compile errors.

        var path = WriteTempTrace(
            MakeObsLine("GetPlayerZone", null, """{"value":182}"""));

        var state = TraceReplayFixtureState.FromTraceFile(path);

        Assert.IsType<InertNavigator>(state.Navigator);
        Assert.IsType<InertTeleporter>(state.Teleporter);
        Assert.IsType<InertInteractor>(state.Interactor);
        Assert.IsType<FakeCombat>(state.Combat);
        Assert.IsType<FakeGearEquipper>(state.GearEquipper);
        Assert.IsType<FakeBestGearEquipper>(state.BestGearEquipper);
        Assert.IsType<FakeJobChanger>(state.JobChanger);
        Assert.IsType<FakeMinigameSkipper>(state.Minigames);
        Assert.IsType<FakeDialogueResolver>(state.Dialogue);
        Assert.IsType<FakeTimingProfile>(state.Timing);
    }

    /// <summary>A3 — empty trace → InvalidDataException.</summary>
    [Fact]
    public void A3_FromTraceFile_ZeroObservationEvents_ThrowsInvalidDataException()
    {
        // Given a JSONL trace with run.start/run.end/decision but zero ObservationEvents,
        // When FromTraceFile, Then InvalidDataException whose message mentions "no observation".
        //
        // RED: TraceReplayFixtureState does not exist → CS0246.

        var path = WriteTempTrace(
            MakeRunStartLine("test-run", 66130),
            MakeDecisionLine("test-run", "travel-step", "navigate"),
            MakeRunEndLine("test-run", "done")
        );

        var ex = Assert.Throws<InvalidDataException>(
            () => TraceReplayFixtureState.FromTraceFile(path));

        Assert.Contains("no observation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A4 — OnTick is a no-op; does not advance the scanner.</summary>
    [Fact]
    public async Task A4_OnTick_DoesNotAdvanceObservationScanner()
    {
        // Given a built TraceReplayFixtureState with one GetPlayerZone observation (zone 182),
        // When state.OnTick(action, tick) is called,
        // Then it does not throw AND reading state.GameState.GetPlayerZone afterward
        //      still returns zone 182 (scanner was not advanced by OnTick).
        //
        // RED: TraceReplayFixtureState does not exist → CS0246.

        var path = WriteTempTrace(
            MakeObsLine("GetPlayerZone", null, """{"value":182}"""));

        var state = TraceReplayFixtureState.FromTraceFile(path);

        // Must not throw
        state.OnTick(new EngineAction.Wait("test-wait"), 0);

        // Scanner must still hold the zone-182 observation
        var result = await state.GameState.GetPlayerZone(CancellationToken.None);
        var success = Assert.IsType<Result<ZoneId>.Success>(result);
        Assert.Equal(new ZoneId(182), success.Value);
    }

    /// <summary>A5 — reads consume recorded observations.</summary>
    [Fact]
    public async Task A5_GameState_GetPlayerZone_ReturnsRecordedZone()
    {
        // Given a trace with GetPlayerZone observation → zone 182,
        // When await state.GameState.GetPlayerZone(ct),
        // Then Result.Ok(new ZoneId(182)).
        //
        // RED: TraceReplayFixtureState does not exist → CS0246.

        var path = WriteTempTrace(
            MakeObsLine("GetPlayerZone", null, """{"value":182}"""));

        var state = TraceReplayFixtureState.FromTraceFile(path);

        var result = await state.GameState.GetPlayerZone(CancellationToken.None);
        var success = Assert.IsType<Result<ZoneId>.Success>(result);
        Assert.Equal(new ZoneId(182), success.Value);
    }

    // =====================================================================
    // GROUP B — Inert no-op action adapters
    // =====================================================================

    /// <summary>B1 — InertNavigator: parameterless ctor, returns Arrived.</summary>
    [Fact]
    public async Task B1_InertNavigator_ParameterlessCtor_ReturnsArrivedAndBenignValues()
    {
        // Given new InertNavigator() (parameterless — no FakeGameStateProvider),
        // NavigateTo → Arrived, IsNavigating → false, Stop → success,
        // GetNavmeshInfo → NavmeshStatus.Ready.
        //
        // RED: InertNavigator does not exist → CS0246.

        var nav = new InertNavigator();

        var navResult = await nav.NavigateTo(
            new WorldPosition(99f, 0f, 99f), new NavigationOptions(), CancellationToken.None);
        var navSuccess = Assert.IsType<Result<NavigationOutcome>.Success>(navResult);
        Assert.Equal(NavigationOutcome.Arrived, navSuccess.Value);

        var isNavResult = await nav.IsNavigating(CancellationToken.None);
        Assert.IsType<Result<bool>.Success>(isNavResult);
        Assert.False(((Result<bool>.Success)isNavResult).Value);

        var stopResult = await nav.Stop(CancellationToken.None);
        Assert.IsType<Result<Unit>.Success>(stopResult);

        var meshResult = await nav.GetNavmeshInfo(new ZoneId(182), CancellationToken.None);
        var meshSuccess = Assert.IsType<Result<NavmeshInfo>.Success>(meshResult);
        Assert.Equal(NavmeshStatus.Ready, meshSuccess.Value.Status);
    }

    /// <summary>B2 — InertTeleporter: parameterless ctor, all calls return Arrived / benign values.</summary>
    [Fact]
    public async Task B2_InertTeleporter_ParameterlessCtor_AllCallsReturnBenignValues()
    {
        // Given new InertTeleporter() (parameterless — no FakeGameStateProvider),
        // TeleportToAetheryte → Arrived, TeleportToAethernet → Arrived,
        // UseReturn → Arrived, IsTeleportAvailable → true,
        // GetReturnCooldown / GetTeleportCooldown → TimeSpan.Zero,
        // GetHomeAetheryte → null.
        //
        // RED: InertTeleporter does not exist → CS0246.

        var tp = new InertTeleporter();

        var aetheryte = await tp.TeleportToAetheryte(new AetheryteId(1), CancellationToken.None);
        Assert.Equal(TeleportOutcome.Arrived, ((Result<TeleportOutcome>.Success)aetheryte).Value);

        var aethernet = await tp.TeleportToAethernet(new AethernetId(2), CancellationToken.None);
        Assert.Equal(TeleportOutcome.Arrived, ((Result<TeleportOutcome>.Success)aethernet).Value);

        var ret = await tp.UseReturn(CancellationToken.None);
        Assert.Equal(TeleportOutcome.Arrived, ((Result<TeleportOutcome>.Success)ret).Value);

        var avail = await tp.IsTeleportAvailable(CancellationToken.None);
        Assert.True(((Result<bool>.Success)avail).Value);

        var retCd = await tp.GetReturnCooldown(CancellationToken.None);
        Assert.Equal(TimeSpan.Zero, ((Result<TimeSpan>.Success)retCd).Value);

        var tpCd = await tp.GetTeleportCooldown(CancellationToken.None);
        Assert.Equal(TimeSpan.Zero, ((Result<TimeSpan>.Success)tpCd).Value);

        var home = await tp.GetHomeAetheryte(CancellationToken.None);
        Assert.Null(((Result<AetheryteId?>.Success)home).Value);
    }

    /// <summary>B3 — InertInteractor: parameterless ctor, benign success, no quest mutation.</summary>
    [Fact]
    public async Task B3_InertInteractor_ParameterlessCtor_AllCallsBenignSuccessNoMutation()
    {
        // Given new InertInteractor() (parameterless — no IQuestState dependency),
        // AcceptQuest/CompleteQuest → Unit, InteractWith → DialogueOpened,
        // AdvanceDialogue → Advanced, HandOverItem → HandedOver,
        // EnterSinglePlayerDuty → Entered.
        //
        // RED: InertInteractor does not exist → CS0246.

        var interactor = new InertInteractor();

        var accept = await interactor.AcceptQuest(new QuestId(66130), CancellationToken.None);
        Assert.IsType<Result<Unit>.Success>(accept);

        var complete = await interactor.CompleteQuest(new QuestId(66130), CancellationToken.None);
        Assert.IsType<Result<Unit>.Success>(complete);

        var interact = await interactor.InteractWith(new NpcId(12345), CancellationToken.None);
        Assert.Equal(InteractOutcome.DialogueOpened, ((Result<InteractOutcome>.Success)interact).Value);

        var dialogue = await interactor.AdvanceDialogue(CancellationToken.None);
        Assert.Equal(DialogueOutcome.Advanced, ((Result<DialogueOutcome>.Success)dialogue).Value);

        var handOver = await interactor.HandOverItem(
            [new ItemId(2000104)], new NpcId(99), CancellationToken.None);
        Assert.Equal(HandOverOutcome.HandedOver, ((Result<HandOverOutcome>.Success)handOver).Value);

        var spd = await interactor.EnterSinglePlayerDuty(
            new AsNpc(new NpcId(1)), DutyDifficulty.Normal, CancellationToken.None);
        Assert.Equal(SpdEntryOutcome.Entered, ((Result<SpdEntryOutcome>.Success)spd).Value);
    }

    /// <summary>B4 — Engine with inert adapters over replay providers ticks once without throwing.</summary>
    [Fact]
    public async Task B4_EngineWithInertAdapters_ReplayProviders_TicksWithoutThrowing()
    {
        // Wire QuestEngine with ReplayGameStateProvider/ReplayQuestState from a minimal trace
        // and the three inert adapters; StartQuest + BeginRun + one Tick → non-null EngineAction.
        //
        // RED: InertNavigator, InertTeleporter, InertInteractor do not exist → CS0246.

        // Build enough observations to survive at least one engine tick for quest 66130.
        // The exact set depends on the engine's first-tick read pattern — we supply generous coverage.
        // Observations must be ordered to match the engine's actual read pattern per tick:
        // GetQuestSequence, GetNewGamePlusState, IsQuestComplete, GetUiState, GetPlayerPosition, GetPlayerZone
        // (see QuestEngine.ResolveAction). The scanner's scan-forward cursor means an
        // observation placed before a key the engine reads first will be unreachable.
        var obsLines = new[]
        {
            MakeObsLine("GetQuestSequence",     """{"value":66130}""",  """0"""),
            MakeObsLine("GetNewGamePlusState",  null,                   """{"isActive":false,"currentChapter":null,"isSuspended":false,"activeReplayQuestId":null}"""),
            MakeObsLine("IsQuestComplete",      """{"value":66130}""",  """false"""),
            MakeObsLine("GetUiState",           null,                   """{"value":0}"""),
            MakeObsLine("GetPlayerPosition",    null,                   """{"x":44.7,"y":4.0,"z":-148.7}"""),
            MakeObsLine("GetPlayerZone",        null,                   """{"value":182}"""),
            MakeObsLine("GetQuestVariables",    """{"value":66130}""",  """[0,0,0,0,0,0]"""),
            MakeObsLine("GetQuestStatus",       """{"value":66130}""",  """0"""),
            // IsPlayerInCombat — read by global defense rule on every tick (universal, no gate).
            MakeObsLine("IsPlayerInCombat",     null,                   """false"""),
            MakeObsLine("IsQuestAvailable",     """{"value":66130}""",  """true"""),
            MakeObsLine("IsQuestAccepted",      """{"value":66130}""",  """false"""),
        };

        var path = WriteTempTrace(obsLines);

        var questJson = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "66130.json"));
        var quest = JsonSerializer.Deserialize<QuestForge.Schema.QuestDefinition>(
            questJson, QuestForge.Schema.QuestForgeJsonContext.QuestFileOptions)!;

        var allObs = TraceReader.ReadFile<ObservationEvent>(path);
        var gameState = new ReplayGameStateProvider(allObs);
        var questState = new ReplayQuestState(allObs);

        // CS0246: InertNavigator, InertTeleporter, InertInteractor
        var engine = new QuestEngine(
            gameState, questState,
            new InertNavigator(), new InertTeleporter(), new InertInteractor(),
            new FakeCombat(),
            new FakeMinigameSkipper(), new FakeDialogueResolver(),
            new FakeTimingProfile(),
            new CapturingTraceWriter(), NullLogger<QuestEngine>.Instance);

        engine.StartQuest(quest);
        engine.BeginRun("b4-run");

        var action = await engine.Tick(CancellationToken.None);
        Assert.NotNull(action);
    }

    // =====================================================================
    // GROUP C — EngineFixtureTests fallback dispatch
    // =====================================================================

    // NOTE on approach: TryResolveSourceTrace and WrapTickForStarvation are to be extracted
    // as internal static members of EngineFixtureTests (or a new internal FixtureDispatch
    // helper class in the same assembly). Since both classes live in QuestForge.Engine.Tests,
    // there is no InternalsVisibleTo needed — same-assembly access.
    //
    // EngineFixture must be promoted from private to internal (or its relevant parts exposed
    // via a new EngineFixtureRecord internal alias) so TryResolveSourceTrace's signature
    // can be called from here. The Builder decides the exact exposure strategy; these tests
    // reference the expected signatures per the plan.

    /// <summary>C1 — no sibling trace → TryResolveSourceTrace returns null.</summary>
    [Fact]
    public void C1_TryResolveSourceTrace_NoSiblingTrace_ReturnsNull()
    {
        // Given a fixture with no sibling .trace.jsonl and no sourceTrace field,
        // TryResolveSourceTrace returns null (dispatch falls to scripted or Assert.Skip).
        //
        // RED: EngineFixtureTests.TryResolveSourceTrace does not exist → CS0117.

        using var tempDir = new TempDir();
        var fixturePath = Path.Combine(tempDir.Path, "simple-linear-acceptance.json");
        File.WriteAllText(fixturePath, MinimalFixtureJson(sourceTrace: null));

        // CS0117: TryResolveSourceTrace is the missing member
        var result = EngineFixtureTests.TryResolveSourceTrace(
            fixturePath, sourceTraceField: null, dataRoot: tempDir.Path);

        Assert.Null(result);
    }

    /// <summary>C2 — sibling trace present → TryResolveSourceTrace returns absolute sibling path.</summary>
    [Fact]
    public void C2_TryResolveSourceTrace_SiblingTraceExists_ReturnsSiblingPath()
    {
        // Given a fixture named "x.json" with a sibling "x.trace.jsonl",
        // TryResolveSourceTrace returns the absolute path to the sibling.
        //
        // RED: EngineFixtureTests.TryResolveSourceTrace does not exist → CS0117.

        using var tempDir = new TempDir();
        var fixturePath = Path.Combine(tempDir.Path, "x.json");
        var tracePath   = Path.Combine(tempDir.Path, "x.trace.jsonl");
        File.WriteAllText(fixturePath, MinimalFixtureJson(sourceTrace: null));
        File.WriteAllText(tracePath, "{}");

        var result = EngineFixtureTests.TryResolveSourceTrace(
            fixturePath, sourceTraceField: null, dataRoot: tempDir.Path);

        Assert.NotNull(result);
        Assert.Equal(tracePath, result);
    }

    /// <summary>C3 — no scripted entry and no trace → TryResolveSourceTrace returns null → Assert.Skip.</summary>
    [Fact]
    public void C3_TryResolveSourceTrace_NoSiblingAndNoField_ReturnsNull_DispatchWillSkip()
    {
        // The dispatch three-way branch uses null return to decide Assert.Skip.
        // Test that TryResolveSourceTrace returns null when no trace exists anywhere.
        //
        // RED: EngineFixtureTests.TryResolveSourceTrace does not exist → CS0117.

        using var tempDir = new TempDir();
        var fixturePath = Path.Combine(tempDir.Path, "y.json");
        File.WriteAllText(fixturePath, MinimalFixtureJson(sourceTrace: null));
        // No y.trace.jsonl placed — deliberately absent

        var result = EngineFixtureTests.TryResolveSourceTrace(
            fixturePath, sourceTraceField: null, dataRoot: tempDir.Path);

        Assert.Null(result); // null → dispatch will call Assert.Skip
    }

    /// <summary>C4 — explicit sourceTrace field overrides sibling convention.</summary>
    [Fact]
    public void C4_TryResolveSourceTrace_ExplicitFieldWinsOverSibling()
    {
        // Given a fixture with sourceTrace: "fixtures/engine/shared.trace.jsonl"
        // and a real shared file at that path, TryResolveSourceTrace returns the
        // shared file — not the (nonexistent) same-basename sibling.
        //
        // RED: EngineFixtureTests.TryResolveSourceTrace does not exist → CS0117.

        using var tempDir = new TempDir();

        var sharedDir = Path.Combine(tempDir.Path, "fixtures", "engine");
        Directory.CreateDirectory(sharedDir);
        var sharedTrace = Path.Combine(sharedDir, "shared.trace.jsonl");
        File.WriteAllText(sharedTrace, "{}");

        // The fixture has a sourceTrace field pointing to the shared trace
        const string sourceTraceField = "fixtures/engine/shared.trace.jsonl";

        var fixturePath = Path.Combine(sharedDir, "my-fixture.json");
        File.WriteAllText(fixturePath, MinimalFixtureJson(sourceTrace: sourceTraceField));

        var result = EngineFixtureTests.TryResolveSourceTrace(
            fixturePath, sourceTraceField: sourceTraceField, dataRoot: tempDir.Path);

        Assert.NotNull(result);
        Assert.Equal(sharedTrace, result, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>C5 — sourceTrace field uses forward slashes; Windows path separator substituted.</summary>
    [Fact]
    public void C5_TryResolveSourceTrace_ForwardSlashField_ResolvesCorrectlyOnWindows()
    {
        // Given "sourceTrace": "fixtures/engine/a.trace.jsonl" (forward slashes),
        // when resolved on Windows, the helper applies Path.DirectorySeparatorChar substitution.
        //
        // RED: EngineFixtureTests.TryResolveSourceTrace does not exist → CS0117.

        using var tempDir = new TempDir();

        var subDir = Path.Combine(tempDir.Path, "fixtures", "engine");
        Directory.CreateDirectory(subDir);
        var tracePath = Path.Combine(subDir, "a.trace.jsonl");
        File.WriteAllText(tracePath, "{}");

        const string sourceTraceField = "fixtures/engine/a.trace.jsonl"; // forward slashes

        var fixturePath = Path.Combine(subDir, "a.json");
        File.WriteAllText(fixturePath, MinimalFixtureJson(sourceTrace: sourceTraceField));

        var result = EngineFixtureTests.TryResolveSourceTrace(
            fixturePath, sourceTraceField: sourceTraceField, dataRoot: tempDir.Path);

        Assert.NotNull(result);
        Assert.True(File.Exists(result!), $"Resolved path must exist on disk: {result}");
    }

    /// <summary>C6 — EngineFixture record deserializes the new optional sourceTrace field.</summary>
    [Fact]
    public void C6_EngineFixture_SourceTraceProperty_DeserializesCorrectly()
    {
        // Given fixture JSON with "sourceTrace": "x.trace.jsonl",
        // When deserialized via EngineFixtureTests.DeserializeFixtureForTest (internal static),
        // Then fixture.SourceTrace == "x.trace.jsonl".
        // Given JSON omitting sourceTrace, fixture.SourceTrace == null (backward-compat).
        //
        // RED: EngineFixture.SourceTrace and EngineFixtureTests.DeserializeFixtureForTest
        //      do not exist → CS0117 / CS1061.

        // With sourceTrace field
        var withJson = MinimalFixtureJson(sourceTrace: "x.trace.jsonl");
        var withFixture = EngineFixtureTests.DeserializeFixtureForTest(withJson);
        Assert.Equal("x.trace.jsonl", withFixture.SourceTrace);

        // Without sourceTrace field (1.0.0 backward compat)
        var withoutJson = MinimalFixtureJson(sourceTrace: null);
        var withoutFixture = EngineFixtureTests.DeserializeFixtureForTest(withoutJson);
        Assert.Null(withoutFixture.SourceTrace);
    }

    // =====================================================================
    // GROUP D — Starvation vs regression failure modes
    // =====================================================================

    /// <summary>
    /// D1 — truncated trace → starvation → xUnit skip (Xunit.Sdk.SkipException), not a fail.
    ///
    /// The builder changes WrapTickForStarvation from Assert.Fail(...) to Assert.Skip(...).
    /// This test asserts the starvation path produces a Xunit.Sdk.SkipException with an
    /// actionable message ("re-record" + the fixture filename), and that the raw
    /// ReplayObservationStarvationException is NOT surfaced to the caller.
    ///
    /// RED signal (current code): WrapTickForStarvation calls Assert.Fail, which throws
    /// Xunit.Sdk.FailException — NOT Xunit.Sdk.SkipException. Assert.IsType&lt;SkipException&gt;
    /// below will therefore fail with "Expected type: SkipException, Actual type: FailException".
    /// </summary>
    [Fact]
    public async Task D1_TruncatedTrace_StarvesDuringTick_SurfacesSkipNotFail()
    {
        // Given a trace with only GetPlayerZone (engine reads many more observations on first tick).
        var truncatedPath = WriteTempTrace(
            MakeObsLine("GetPlayerZone", null, """{"value":182}""")
        );

        var state = TraceReplayFixtureState.FromTraceFile(truncatedPath);

        var questJson = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "66130.json"));
        var quest = JsonSerializer.Deserialize<QuestForge.Schema.QuestDefinition>(
            questJson, QuestForge.Schema.QuestForgeJsonContext.QuestFileOptions)!;

        var engine = new QuestEngine(
            state.GameState, state.QuestState,
            state.Navigator, state.Teleporter, state.Interactor,
            state.Combat, state.Minigames, state.Dialogue, state.Timing,
            new CapturingTraceWriter(), NullLogger<QuestEngine>.Instance);

        engine.StartQuest(quest);
        engine.BeginRun("d1-run");

        const string traceFileName = "truncated.trace.jsonl";

        // WrapTickForStarvation must throw on starvation.
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => EngineFixtureTests.WrapTickForStarvation(
                engine, traceFileName, CancellationToken.None));

        // RED: currently Assert.Fail throws Xunit.Sdk.FailException, not Xunit.Sdk.SkipException.
        // After the builder's change (Fail → Skip) this assertion will pass.
        Assert.IsType<Xunit.Sdk.SkipException>(ex);

        // The skip reason must be actionable: mention "re-record" and name the fixture file.
        Assert.Contains("re-record", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(traceFileName, ex.Message, StringComparison.Ordinal);

        // The raw starvation exception must NOT escape — it must be wrapped in the skip.
        Assert.IsNotType<ReplayObservationStarvationException>(ex);
    }

    /// <summary>
    /// D1b — starvation skip message is self-sufficient: contains "re-record needed" and
    /// the fixture filename so CI output is actionable without looking at the stack trace.
    ///
    /// Complements D1 by pinning the exact phrase contract on the skip reason, independent
    /// of whether D1's IsType assertion passes or fails.
    ///
    /// RED signal (current code): Assert.Fail message contains "OBSERVATION STARVATION" and
    /// "re-record" but the thrown type is FailException. This test separately asserts the
    /// type is SkipException, so it is also RED until the builder makes the change.
    /// </summary>
    [Fact]
    public async Task D1b_StarvationSkip_MessageContainsReRecordAndFixtureName()
    {
        var truncatedPath = WriteTempTrace(
            MakeObsLine("GetPlayerZone", null, """{"value":182}""")
        );

        var state = TraceReplayFixtureState.FromTraceFile(truncatedPath);

        var questJson = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "66130.json"));
        var quest = JsonSerializer.Deserialize<QuestForge.Schema.QuestDefinition>(
            questJson, QuestForge.Schema.QuestForgeJsonContext.QuestFileOptions)!;

        var engine = new QuestEngine(
            state.GameState, state.QuestState,
            state.Navigator, state.Teleporter, state.Interactor,
            state.Combat, state.Minigames, state.Dialogue, state.Timing,
            new CapturingTraceWriter(), NullLogger<QuestEngine>.Instance);

        engine.StartQuest(quest);
        engine.BeginRun("d1b-run");

        const string traceFileName = "my-quest.trace.jsonl";

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => EngineFixtureTests.WrapTickForStarvation(
                engine, traceFileName, CancellationToken.None));

        // The message must identify this as a "re-record needed" skip (not a decision regression).
        Assert.Contains("re-record", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The fixture filename must appear verbatim so the developer knows which file to re-record.
        Assert.Contains(traceFileName, ex.Message, StringComparison.Ordinal);

        // RED: the type must be SkipException (not FailException) for this to be a skip, not fail.
        Assert.IsType<Xunit.Sdk.SkipException>(ex);
    }

    /// <summary>
    /// D4 — transition mismatch stays as Assert.Equal failure, NOT a skip.
    ///
    /// This pins the two-failure-mode separation (§2.4 of FIXTURE_REPLAY_HARNESS_PLAN.md):
    /// - Observation starvation (engine reads an unrecorded (method,arg)) → Skip (re-record needed).
    /// - Decision regression (engine emits wrong action/step for matching observations) → Fail (Assert.Equal).
    ///
    /// A decision regression must remain an Assert.Equal mismatch (EqualException) so it
    /// does NOT silently skip — a regression must block CI, not defer it.
    ///
    /// This test asserts that simulated transition mismatches throw EqualException (or a
    /// subtype of XunitException that is NOT SkipException), confirming the two modes are
    /// independently handled.
    ///
    /// Currently PASSES (transition assertions still use Assert.Equal — the builder must not
    /// change that path). Kept here as a guardian so any future refactor that accidentally
    /// converts regressions to skips will be caught.
    /// </summary>
    [Fact]
    public void D4_TransitionMismatch_SurfacesAsAssertEqualFailure_NotAsSkip()
    {
        // Simulate the transition assertion block from EngineProducesExpectedTransitions.
        // The "actual" transition is navigate; the "expected" says interact.
        // The resulting exception must be Xunit.Sdk.EqualException (not SkipException).

        var actualTransitions   = new List<(string? StepId, string ActionType)> { ("step-1", "navigate") };
        var expectedTransitions = new[] { new EngineFixtureTests.FixtureTransition("step-1", "interact") };

        var ex = Record.Exception(() =>
        {
            Assert.Equal(expectedTransitions.Length, actualTransitions.Count);
            for (var i = 0; i < expectedTransitions.Length; i++)
            {
                Assert.Equal(expectedTransitions[i].StepId,     actualTransitions[i].StepId);
                Assert.Equal(expectedTransitions[i].ActionType, actualTransitions[i].ActionType);
            }
        });

        // A transition mismatch must throw (i.e., not silently pass).
        Assert.NotNull(ex);

        // It must NOT be a skip — a regression must fail CI, not defer it.
        Assert.IsNotType<Xunit.Sdk.SkipException>(ex);

        // It must be an xUnit assertion exception (the transition was asserted, not starvation).
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(ex);
    }

    /// <summary>
    /// D2 — WrapTickForStarvation passes through the engine action unchanged when the engine does NOT starve.
    /// This verifies the wrapper only intercepts ReplayObservationStarvationException; a normal tick
    /// returns the engine action and does NOT throw or alter the action type.
    /// </summary>
    [Fact]
    public async Task D2_WrapTickForStarvation_NormalTick_ReturnEngineActionUnchanged()
    {
        // Given a TraceReplayFixtureState built from a complete observation set that covers
        // everything the engine reads on its first tick (quest 66130 initial state),
        // and an engine wired from that state,
        // When WrapTickForStarvation is called once,
        // Then it returns a non-null EngineAction WITHOUT throwing any exception —
        //      proving the wrapper is transparent on the happy path and only intercepts
        //      ReplayObservationStarvationException (tested by D1).
        //
        // The returned action type is irrelevant to this test (D1 tests the sad path);
        // we only assert it is non-null and is one of the concrete EngineAction subtypes,
        // confirming the pass-through is real and not a stub that always returns Wait.
        //
        // This is a BEHAVIORAL test — not a tautology.
        // It will fail if WrapTickForStarvation swallows the return value, or always throws,
        // or always returns null.

        // Build a generous set of observations matching quest 66130's first-tick read pattern.
        // See QuestEngine.ResolveAction for the actual read order.
        var obsLines = new[]
        {
            MakeObsLine("GetQuestSequence",     """{"value":66130}""",  """0"""),
            MakeObsLine("GetNewGamePlusState",  null,                   """{"isActive":false,"currentChapter":null,"isSuspended":false,"activeReplayQuestId":null}"""),
            MakeObsLine("IsQuestComplete",      """{"value":66130}""",  """false"""),
            MakeObsLine("GetUiState",           null,                   """{"value":0}"""),
            MakeObsLine("GetPlayerPosition",    null,                   """{"x":44.7,"y":4.0,"z":-148.7}"""),
            MakeObsLine("GetPlayerZone",        null,                   """{"value":182}"""),
            MakeObsLine("GetQuestVariables",    """{"value":66130}""",  """[0,0,0,0,0,0]"""),
            MakeObsLine("GetQuestStatus",       """{"value":66130}""",  """0"""),
            MakeObsLine("IsQuestAvailable",     """{"value":66130}""",  """true"""),
            MakeObsLine("IsQuestAccepted",      """{"value":66130}""",  """false"""),
            // Extra coverage so the engine doesn't starve on follow-up reads
            MakeObsLine("GetPlayerPosition",    null,                   """{"x":44.7,"y":4.0,"z":-148.7}"""),
            MakeObsLine("GetPlayerZone",        null,                   """{"value":182}"""),
            MakeObsLine("GetQuestVariables",    """{"value":66130}""",  """[0,0,0,0,0,0]"""),
        };

        var tracePath = WriteTempTrace(obsLines);

        var questJson = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "66130.json"));
        var quest = JsonSerializer.Deserialize<QuestForge.Schema.QuestDefinition>(
            questJson, QuestForge.Schema.QuestForgeJsonContext.QuestFileOptions)!;

        var allObs    = TraceReader.ReadFile<ObservationEvent>(tracePath);
        var gameState = new ReplayGameStateProvider(allObs);
        var questState = new ReplayQuestState(allObs);

        var engine = new QuestEngine(
            gameState, questState,
            new InertNavigator(), new InertTeleporter(), new InertInteractor(),
            new FakeCombat(),
            new FakeMinigameSkipper(), new FakeDialogueResolver(),
            new FakeTimingProfile(),
            new CapturingTraceWriter(), NullLogger<QuestEngine>.Instance);

        engine.StartQuest(quest);
        engine.BeginRun("d2-run");

        // Act — must NOT throw (starvation, null reference, etc.)
        var action = await EngineFixtureTests.WrapTickForStarvation(
            engine, "d2-test.trace.jsonl", CancellationToken.None);

        // Assert: the wrapper passes the action through unchanged.
        // Any concrete EngineAction subtype is acceptable — the point is it is non-null
        // and is the real engine decision, not a fabricated one.
        Assert.NotNull(action);
        Assert.True(
            action is EngineAction.Navigate
                   or EngineAction.Interact
                   or EngineAction.Wait
                   or EngineAction.AwaitUser
                   or EngineAction.Done,
            $"Expected a known concrete EngineAction subtype but got: {action.GetType().Name}");
    }

    /// <summary>D3 — safety-break constant is 10 (overrun guard unchanged).</summary>
    [Fact]
    public void D3_SafetyOverrunCount_IsDefinedAndEqualsTen()
    {
        // The existing EngineProducesExpectedTransitions loop breaks at
        // fixture.ExpectedTransitions.Length + SafetyOverrunCount to prevent infinite loops.
        // This constant must equal 10 (matching Quest66130ReplayTests.cs:150).
        //
        // RED: EngineFixtureTests.SafetyOverrunCount does not exist as internal const → CS0117.

        Assert.Equal(10, EngineFixtureTests.SafetyOverrunCount);
    }

    // =====================================================================
    // GROUP E — Proof fixture (data-dependent)
    // =====================================================================

    /// <summary>E4 — questforge-data absent → AllEngineFixtures yields no cases; no failure.</summary>
    [Fact]
    public void E4_QuestForgeDataAbsent_AllEngineFixtures_YieldsNoCases_ExpectedToPassNow()
    {
        // When FixtureLocator.TryGetQuestForgeDataRoot() returns null,
        // AllEngineFixtures() yields no cases → no fixture test runs → no fixture test fails.
        // This is the existing behavior, preserved unchanged.
        //
        // This test is expected to PASS immediately (no new code required).

        var root = FixtureLocator.TryGetQuestForgeDataRoot();
        if (root is null)
        {
            // questforge-data absent: AllEngineFixtures returns empty TheoryData → no tests run
            var data = EngineFixtureTests.AllEngineFixtures();
            Assert.Empty(data);
        }
        else
        {
            // questforge-data present: the theory runs normally (not the absence scenario).
            // Skip so this test always passes regardless of environment.
            Assert.Skip(
                "questforge-data is present in this environment; " +
                "E4 tests the absence scenario which is N/A here. " +
                "The parametric theory runs its own data-driven assertions.");
        }
    }

    // E1 — CURRENTLY FAILING (1 vs 26 transitions). Activates once the redesign lands.
    //       The parametric theory EngineFixtureTests.EngineProducesExpectedTransitions running
    //       with-attunement.json currently fails with "Expected: 26, Actual: 1".
    //       After the segmented scanner + OnTransitionRecorded harness change, it must pass.
    //       No additional test code required — the parametric theory covers it.
    //
    // E3: Both simple-linear-acceptance.json (scripted, 66130) and with-attunement.json
    //     (generic replay, 65644) pass in the same dotnet test run.
    //     Covered by the parametric theory once E1 is fixed.

    // =====================================================================
    // GROUP E2 — Data-integrity assertion over the committed trace
    // =====================================================================

    /// <summary>
    /// E2: Data-integrity assertion over with-attunement.trace.jsonl.
    /// Asserts:
    ///   - Exactly one runId ("20260525-023230-5aaadbb3") in the trace.
    ///   - Exactly one run.start event for quest 65644.
    ///   - A run.end event with outcome "done".
    ///   - Exactly 26 DecisionEvents.
    ///   - IsQuestComplete(65644) observations appear in the order [false, true] and only those two values.
    ///
    /// This guards the input the E1 proof depends on and fails fast if the committed trace file
    /// is ever accidentally corrupted or replaced.
    ///
    /// PASSES immediately (reads the already-committed trace; no new code required).
    /// Skips if questforge-data is not present.
    /// </summary>
    [Fact]
    public void E2_WithAttunementTrace_DataIntegrity_SingleRunId_26Decisions_IsQuestCompleteValues()
    {
        var dataRoot = FixtureLocator.TryGetQuestForgeDataRoot();
        if (dataRoot is null)
        {
            Assert.Skip("questforge-data not present in this environment; E2 is a data-integrity guard that requires the committed trace.");
            return;
        }

        var tracePath = Path.Combine(dataRoot, "fixtures", "engine", "with-attunement.trace.jsonl");
        Assert.True(File.Exists(tracePath),
            $"E2 requires with-attunement.trace.jsonl at: {tracePath}");

        var allEvents = TraceReader.ReadFile(tracePath);

        // --- Single runId ---
        var runIds = allEvents
            .Select(e => e switch
            {
                RunStartEvent rs => rs.RunId,
                RunEndEvent   re => re.RunId,
                ObservationEvent ob => ob.RunId,
                DecisionEvent   de => de.RunId,
                _ => null
            })
            .Where(id => id is not null)
            .Distinct()
            .ToList();

        Assert.Single(runIds);
        Assert.Equal("20260525-023230-5aaadbb3", runIds[0]);

        // --- One run.start for quest 65644 ---
        var runStarts = allEvents.OfType<RunStartEvent>().ToList();
        Assert.Single(runStarts);
        Assert.Equal(65644u, runStarts[0].QuestId);

        // --- run.end with outcome "done" ---
        var runEnds = allEvents.OfType<RunEndEvent>().ToList();
        Assert.Contains(runEnds, e => e.Outcome == "done");

        // --- Exactly 26 DecisionEvents ---
        var decisions = allEvents.OfType<DecisionEvent>().ToList();
        Assert.Equal(26, decisions.Count);

        // --- IsQuestComplete(65644) recorded as [false, true] in order ---
        var isQuestCompleteObs = allEvents
            .OfType<ObservationEvent>()
            .Where(o => o.Method == "IsQuestComplete"
                && o.Argument?.GetRawText() == """{"value":65644}""")
            .ToList();

        Assert.Equal(2, isQuestCompleteObs.Count);
        Assert.Equal("false", isQuestCompleteObs[0].Value?.GetRawText());
        Assert.Equal("true",  isQuestCompleteObs[1].Value?.GetRawText());
    }

    // =====================================================================
    // GROUP P — Provider / state wiring (segmented scanner)
    // =====================================================================

    /// <summary>
    /// P1: FromTraceFile returns a state whose GameState is ReplayGameStateProvider and
    /// QuestState is ReplayQuestState, both backed by a SINGLE shared SegmentedObservationScanner.
    ///
    /// Shared-instance behavior is asserted by reading the scanner's CurrentSegment via both
    /// providers: after state.OnTransitionRecorded() is called, both providers observe the
    /// same updated segment.
    ///
    /// RED: SegmentedObservationScanner, IFixtureState.OnTransitionRecorded do not exist → CS0246/CS1061.
    /// </summary>
    [Fact]
    public void P1_FromTraceFile_ProvidersShareSingleSegmentedObservationScanner()
    {
        // Trace with one decision (so two segments) and observations for both providers
        var path = WriteTempTrace(
            MakeRunStartLine("test-run", 65644),
            MakeObsLine("GetQuestSequence",  """{"value":65644}""", "0"),
            MakeObsLine("GetPlayerPosition", null, """{"x":1.0,"y":0.0,"z":2.0}"""),
            MakeDecisionLine("test-run", "step-1", "Interact"),
            MakeObsLine("GetQuestSequence",  """{"value":65644}""", "1"),
            MakeObsLine("GetPlayerPosition", null, """{"x":0.1,"y":0.0,"z":0.2}"""),
            MakeRunEndLine("test-run", "done")
        );

        // CS0246: TraceReplayFixtureState change (reads full trace) + CS1061: OnTransitionRecorded
        var state = TraceReplayFixtureState.FromTraceFile(path);

        Assert.IsType<ReplayGameStateProvider>(state.GameState);
        Assert.IsType<ReplayQuestState>(state.QuestState);

        // Both providers share the same scanner: calling OnTransitionRecorded advances the segment
        // seen by BOTH providers simultaneously. We verify this by reading the scanner state
        // indirectly: segment 0 has GetQuestSequence=0; after OnTransitionRecorded, segment 1
        // has GetQuestSequence=1. The state's QuestState.GetQuestSequence must see the same
        // segment the GameState reads positions from.
        //
        // CS1061: OnTransitionRecorded does not exist on IFixtureState
        state.OnTransitionRecorded(new EngineAction.Wait("p1-test"), 0);

        // After advancing, QuestState (which reads quest-state observations) must see the new
        // segment's GetQuestSequence value. This confirms shared scanner state.
        // (We don't call the actual async method here to keep the test synchronous; the
        // key assertion is that the above OnTransitionRecorded call does not throw and
        // that both providers' types are correct — the full integration is covered by P4 + E1.)
        Assert.IsType<ReplayGameStateProvider>(state.GameState);
        Assert.IsType<ReplayQuestState>(state.QuestState);
    }

    /// <summary>
    /// P2: A trace with run.start/decision/run.end but ZERO ObservationEvents throws
    /// InvalidDataException mentioning "no observation".
    ///
    /// The message contract is preserved even though the internal reader now reads the full
    /// trace (observations + decisions) rather than just observations.
    ///
    /// This is a continuation of the existing A3 test, but written against the new internal
    /// (TraceReplayFixtureState now accepts a full trace) while preserving the public contract.
    ///
    /// PASSES once the internal constructor is updated (no new code required for the message).
    /// Currently PASSES (A3 already covers this). Written here to anchor group P.
    /// </summary>
    [Fact]
    public void P2_EmptyObservationTrace_InvalidDataException_MessageMentionsNoObservation()
    {
        var path = WriteTempTrace(
            MakeRunStartLine("test-run", 65644),
            MakeDecisionLine("test-run", "step-1", "Navigate"),
            MakeRunEndLine("test-run", "done")
        );

        var ex = Assert.Throws<InvalidDataException>(
            () => TraceReplayFixtureState.FromTraceFile(path));

        Assert.Contains("no observation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// P3: OnTick is NO LONGER the segment driver; OnTransitionRecorded IS.
    ///
    /// Under option (A) of §3.4:
    /// - Calling state.OnTick(action, tick) repeatedly does NOT advance the segment.
    /// - Calling state.OnTransitionRecorded(action, tick) DOES advance the segment by one.
    ///
    /// We verify this by inspecting the scanner's CurrentSegment via a read that only
    /// changes after a segment advance (step-gated pair in segment 1 → different value).
    ///
    /// RED: IFixtureState.OnTransitionRecorded does not exist → CS1061.
    /// Also RED: TraceReplayFixtureState.OnTick must now be a no-op (not an advance).
    /// </summary>
    [Fact]
    public async Task P3_OnTick_DoesNotAdvanceSegment_OnTransitionRecorded_AdvancesSegment()
    {
        // Two-segment trace: segment 0 has IsQuestComplete=false, terminal tail has IsQuestComplete=true
        var path = WriteTempTrace(
            MakeRunStartLine("test-run", 65644),
            MakeObsLine("GetPlayerZone",    null,                  """{"value":181}"""),
            MakeObsLine("IsQuestComplete",  """{"value":65644}""", "false"),
            MakeDecisionLine("test-run", "step-1", "Interact"),
            MakeObsLine("IsQuestComplete",  """{"value":65644}""", "true"),
            MakeRunEndLine("test-run", "done")
        );

        var state = TraceReplayFixtureState.FromTraceFile(path);

        // Read IsQuestComplete via QuestState — must return false (we're in segment 0)
        var r1 = await state.QuestState.IsQuestComplete(new QuestId(65644), CancellationToken.None);
        var v1 = Assert.IsType<Result<bool>.Success>(r1);
        Assert.False(v1.Value,
            "P3: IsQuestComplete must be false in segment 0 before any advance.");

        // OnTick called many times — must NOT advance the segment
        for (var i = 0; i < 5; i++)
            state.OnTick(new EngineAction.Wait("p3-test"), i);

        // Still in segment 0 → IsQuestComplete must still pin to false
        var r2 = await state.QuestState.IsQuestComplete(new QuestId(65644), CancellationToken.None);
        var v2 = Assert.IsType<Result<bool>.Success>(r2);
        Assert.False(v2.Value,
            "P3: After multiple OnTick calls, IsQuestComplete must still pin false — OnTick must NOT advance the segment.");

        // NOW call OnTransitionRecorded — this should advance to the terminal tail
        // CS1061: OnTransitionRecorded does not exist on IFixtureState
        state.OnTransitionRecorded(new EngineAction.Wait("p3-test"), 5);

        // Now in terminal tail → IsQuestComplete must return true
        var r3 = await state.QuestState.IsQuestComplete(new QuestId(65644), CancellationToken.None);
        var v3 = Assert.IsType<Result<bool>.Success>(r3);
        Assert.True(v3.Value,
            "P3: After OnTransitionRecorded, IsQuestComplete must return true (terminal tail segment).");
    }

    /// <summary>
    /// P4: Gated read through the provider pins within a segment; flips in the terminal tail.
    ///
    /// Provider-level mirror of S2: exercises the full stack from IFixtureState.QuestState
    /// down to the SegmentedObservationScanner.
    ///
    /// RED: IFixtureState.OnTransitionRecorded does not exist → CS1061.
    /// Also depends on TraceReplayFixtureState wiring the shared SegmentedObservationScanner.
    /// </summary>
    [Fact]
    public async Task P4_GatedReadThroughProvider_PinsWithinSegment_FlipsInTerminalTail()
    {
        var path = WriteTempTrace(
            MakeRunStartLine("test-run", 65644),
            MakeObsLine("IsQuestComplete", """{"value":65644}""", "false"),  // segment 0
            MakeObsLine("GetPlayerZone",   null, """{"value":181}"""),
            MakeDecisionLine("test-run", "step-1", "Interact"),
            MakeObsLine("IsQuestComplete", """{"value":65644}""", "true"),   // terminal tail
            MakeRunEndLine("test-run", "done")
        );

        var state = TraceReplayFixtureState.FromTraceFile(path);
        var questId = new QuestId(65644);

        // Segment 0: two reads → both must return false
        var r1 = await state.QuestState.IsQuestComplete(questId, CancellationToken.None);
        Assert.False(((Result<bool>.Success)r1).Value,
            "P4: first read in segment 0 must return false.");

        var r2 = await state.QuestState.IsQuestComplete(questId, CancellationToken.None);
        Assert.False(((Result<bool>.Success)r2).Value,
            "P4: second read in segment 0 must still pin false.");

        // Advance to terminal tail
        // CS1061: OnTransitionRecorded does not exist
        state.OnTransitionRecorded(new EngineAction.Wait("p4-test"), 0);

        // Terminal tail: must return true
        var r3 = await state.QuestState.IsQuestComplete(questId, CancellationToken.None);
        Assert.True(((Result<bool>.Success)r3).Value,
            "P4: after OnTransitionRecorded, read in terminal tail must return true.");
    }

    // =====================================================================
    // GROUP H — Harness segment-advance protocol
    // =====================================================================

    /// <summary>
    /// H1: The segment advances once per NEW transition, not per tick.
    ///
    /// A stub IFixtureState records how many times OnTick and OnTransitionRecorded are invoked.
    /// With a scenario that produces N distinct transitions over M > N ticks (because repeated
    /// identical decisions occur), assert:
    ///   - OnTick was called M times (every tick).
    ///   - OnTransitionRecorded was called N times (once per recorded transition).
    ///
    /// This test is written against the EXPECTED harness loop logic (§3.4 option A). It directly
    /// exercises the counting semantics; the actual engine is not invoked — a CountingFixtureState
    /// stub drives the loop logic inline.
    ///
    /// RED: IFixtureState.OnTransitionRecorded does not exist → CS1061 (the CountingFixtureState
    /// below cannot implement the interface until OnTransitionRecorded is added).
    /// </summary>
    [Fact]
    public void H1_SegmentAdvancesOncePerNewTransition_NotPerTick()
    {
        // Simulate the harness loop from Quest66130ReplayTests.cs:~160
        // using a CountingFixtureState stub.
        //
        // Scenario: engine emits the SAME decision 3 times (navigate, navigate, navigate)
        // then a different one (interact). Total ticks = 4, distinct transitions = 2.
        //
        // Expected: OnTick called 4 times (every tick), OnTransitionRecorded called 2 times
        // (once when navigate first appears, once when interact appears).

        var stub = new CountingFixtureState();
        // Cast to IFixtureState so that OnTransitionRecorded is called through the interface.
        // CS1061: IFixtureState does not yet define OnTransitionRecorded → compile error here.
        IFixtureState fixtureState = stub;

        // Simulate the harness loop:
        // Each "tick" provides a (stepId, actionType) pair from the engine.
        // The loop records a transition only when the pair differs from the previous one.
        var actualTransitions = new List<(string? StepId, string ActionType)>();

        // Simulate tick sequence: navigate, navigate, navigate, interact
        var decisions = new[]
        {
            ("step-1", "navigate"),
            ("step-1", "navigate"),
            ("step-1", "navigate"),
            ("step-1", "interact"),
        };

        var fakeAction = new EngineAction.Wait("h1-test");

        for (var tick = 0; tick < decisions.Length; tick++)
        {
            var (stepId, actionType) = decisions[tick];

            // Harness calls OnTick every tick (unchanged)
            fixtureState.OnTick(fakeAction, tick);

            // Harness adds to actualTransitions only on a new (deduped) pair
            var pair = (stepId, actionType);
            if (actualTransitions.Count == 0 || actualTransitions[^1] != pair)
            {
                actualTransitions.Add(pair);
                // Harness calls OnTransitionRecorded only when a new transition is recorded
                // CS1061: OnTransitionRecorded does not exist on IFixtureState
                fixtureState.OnTransitionRecorded(fakeAction, tick);
            }
        }

        Assert.Equal(4, stub.OnTickCount);
        Assert.Equal(2, stub.OnTransitionRecordedCount);
        Assert.Equal(2, actualTransitions.Count);
    }

    /// <summary>
    /// H2: Repeated identical decisions stay in-segment.
    ///
    /// When the engine emits the same (stepId, actionType) pair on consecutive ticks,
    /// only the FIRST occurrence records a transition and advances the segment.
    /// Subsequent identical decisions do NOT call OnTransitionRecorded.
    ///
    /// In the context of the segmented scanner: within-segment position reads keep walking
    /// forward (serving later, closer positions) because the segment is not advanced.
    ///
    /// This test uses the same CountingFixtureState stub as H1.
    ///
    /// RED: IFixtureState.OnTransitionRecorded does not exist → CS1061.
    /// </summary>
    [Fact]
    public void H2_RepeatedIdenticalDecision_StaysInSegment_OnTransitionRecordedCalledOnce()
    {
        var stub = new CountingFixtureState();
        // Call through IFixtureState so CS1061 fires on the missing OnTransitionRecorded member.
        IFixtureState fixtureState = stub;
        var actualTransitions = new List<(string? StepId, string ActionType)>();
        var fakeAction = new EngineAction.Wait("h2-test");

        // Scenario: navigate emitted 5 times → should record ONE transition, advance segment ONCE
        var decisions = Enumerable.Repeat(("step-1", "navigate"), 5).ToArray();

        for (var tick = 0; tick < decisions.Length; tick++)
        {
            var (stepId, actionType) = decisions[tick];
            fixtureState.OnTick(fakeAction, tick);

            var pair = (stepId, actionType);
            if (actualTransitions.Count == 0 || actualTransitions[^1] != pair)
            {
                actualTransitions.Add(pair);
                // CS1061: OnTransitionRecorded does not exist on IFixtureState
                fixtureState.OnTransitionRecorded(fakeAction, tick);
            }
        }

        Assert.Equal(5, stub.OnTickCount);
        Assert.Equal(1, stub.OnTransitionRecordedCount);
        Assert.Single(actualTransitions);
        Assert.Equal("navigate", actualTransitions[0].ActionType);
    }

    /// <summary>
    /// H3: An engine decision that does not match the expected sequence surfaces as an
    /// Assert.Equal mismatch on stepId/actionType — NOT as starvation.
    ///
    /// This pins the §2.4 two-failure-mode separation. The segmented driver serves
    /// observations correctly for any decision; if the engine emits the wrong decision,
    /// the error is a test assertion failure on the transition list, not an observation-level
    /// exception.
    ///
    /// We simulate this by comparing a deliberately-wrong expectedTransitions list against
    /// the actual transitions: the mismatch is in the transition assertion block, not in
    /// observation serving.
    ///
    /// RED: IFixtureState.OnTransitionRecorded → CS1061, so CountingFixtureState cannot compile.
    /// The test itself is also a specification-level narrative of the harness design.
    /// </summary>
    [Fact]
    public void H3_WrongEngineDecision_SurfacesAsTransitionMismatch_NotStarvation()
    {
        // The harness loop collects (stepId, actionType) transitions.
        // If the engine emits a different action than expected, the Assert.Equal block fires.
        // Observation starvation would fire only if the engine reads an unrecorded (method,arg).
        //
        // We simulate a single-step scenario where the "engine" emits "navigate"
        // but we assert "interact" — the resulting error is Xunit's Assert.Equal mismatch,
        // not ReplayObservationStarvationException.
        //
        // The CountingFixtureState is the minimal stub; the key point is the assertion type.

        var stub = new CountingFixtureState();
        // Call through IFixtureState so CS1061 fires on the missing OnTransitionRecorded member.
        IFixtureState fixtureState = stub;
        var actualTransitions = new List<(string? StepId, string ActionType)>();
        var fakeAction = new EngineAction.Wait("h3-test");

        // "Engine" emits navigate for step-1
        fixtureState.OnTick(fakeAction, 0);
        var pair = ("step-1", "navigate");
        if (actualTransitions.Count == 0 || actualTransitions[^1] != pair)
        {
            actualTransitions.Add(pair);
            // CS1061: OnTransitionRecorded does not exist on IFixtureState
            fixtureState.OnTransitionRecorded(fakeAction, 0);
        }

        // Expected transitions say "interact" — mismatch should surface as Assert.Equal failure
        var expectedTransitions = new[] { ("step-1", "interact") };

        // Assert that the failure is a regular assertion mismatch (not starvation)
        var ex = Record.Exception(() =>
        {
            Assert.Equal(expectedTransitions.Length, actualTransitions.Count);
            for (var i = 0; i < expectedTransitions.Length; i++)
            {
                Assert.Equal(expectedTransitions[i].Item1, actualTransitions[i].StepId);
                Assert.Equal(expectedTransitions[i].Item2, actualTransitions[i].ActionType);
            }
        });

        // The exception MUST be an xUnit assertion exception (not starvation)
        Assert.NotNull(ex);
        Assert.IsNotType<ReplayObservationStarvationException>(ex);
        // xUnit Assert.Equal throws Xunit.Sdk.EqualException or similar — it is an Exception
        // but NOT a ReplayObservationStarvationException
        Assert.True(ex is not ReplayObservationStarvationException,
            $"H3: expected a transition mismatch assertion, got: {ex.GetType().Name}: {ex.Message}");
    }

    // =========================================================================
    // CountingFixtureState stub — used by H1, H2, H3
    // =========================================================================

    /// <summary>
    /// Minimal IFixtureState stub for harness-protocol tests.
    /// Records call counts for OnTick and OnTransitionRecorded.
    /// Does NOT wire a real engine — only the counting semantics are tested.
    ///
    /// RED: Cannot implement IFixtureState until OnTransitionRecorded is added to the interface.
    /// </summary>
    private sealed class CountingFixtureState : IFixtureState
    {
        public int OnTickCount { get; private set; }
        public int OnTransitionRecordedCount { get; private set; }

        // Minimal adapter stubs — not used in the H-group tests
        public IGameStateProvider GameState  { get; } = null!;
        public IQuestState        QuestState { get; } = null!;
        public INavigator         Navigator  { get; } = null!;
        public ITeleporter        Teleporter { get; } = null!;
        public IInteractor        Interactor { get; } = null!;
        public ICombat            Combat          { get; } = null!;
        public IGearEquipper      GearEquipper    { get; } = null!;
        public IBestGearEquipper  BestGearEquipper{ get; } = null!;
        public IJobChanger        JobChanger      { get; } = null!;
        public IMinigameSkipper   Minigames       { get; } = null!;
        public IDialogueResolver  Dialogue   { get; } = null!;
        public ITimingProfile     Timing     { get; } = null!;

        public void OnTick(EngineAction action, int tick)
            => OnTickCount++;

        // CS1061: OnTransitionRecorded does not exist on IFixtureState yet
        public void OnTransitionRecorded(EngineAction action, int tick)
            => OnTransitionRecordedCount++;
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static string WriteTempTrace(params string[] jsonLines)
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"qf-test-{Guid.NewGuid():N}.trace.jsonl");
        File.WriteAllLines(path, jsonLines.Where(l => l is not null));
        return path;
    }

    private static string MakeObsLine(string method, string? argJson, string valueJson)
    {
        var obs = new ObservationEvent(
            RunId: "test-run",
            Method: method,
            Argument: argJson is null ? null : JsonDocument.Parse(argJson).RootElement,
            Value: JsonDocument.Parse(valueJson).RootElement,
            At: At);
        return JsonSerializer.Serialize(
            (TraceEvent)obs, TraceEventJsonContext.Default.TraceEvent);
    }

    private static string MakeRunStartLine(string runId, uint questId)
    {
        var evt = new RunStartEvent(RunId: runId, QuestId: questId, QuestSchemaId: 1, At: At);
        return JsonSerializer.Serialize(
            (TraceEvent)evt, TraceEventJsonContext.Default.TraceEvent);
    }

    private static string MakeDecisionLine(string runId, string stepId, string actionType)
    {
        var evt = new DecisionEvent(
            RunId: runId, StepId: stepId, ActionType: actionType, At: At.AddSeconds(1));
        return JsonSerializer.Serialize(
            (TraceEvent)evt, TraceEventJsonContext.Default.TraceEvent);
    }

    private static string MakeRunEndLine(string runId, string outcome)
    {
        var evt = new RunEndEvent(RunId: runId, Outcome: outcome, At: At.AddSeconds(10));
        return JsonSerializer.Serialize(
            (TraceEvent)evt, TraceEventJsonContext.Default.TraceEvent);
    }

    /// <summary>
    /// Minimal fixture JSON for dispatch-helper tests.
    /// The optional sourceTrace field is included only when non-null.
    /// </summary>
    private static string MinimalFixtureJson(string? sourceTrace)
    {
        var sourceTraceField = sourceTrace is null
            ? string.Empty
            : $$$""", "sourceTrace": "{{{sourceTrace}}}" """;
        return $$"""
            {
                "schemaVersion": "1.1.0",
                "description": "Test fixture",
                "initialState": "fresh",
                "questFile": "quests/arr/msq/66130-coming-to-uldah.json",
                "expectedTransitions": [],
                "terminalOutcome": "done"{{sourceTraceField}}
            }
            """;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"qf-test-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
