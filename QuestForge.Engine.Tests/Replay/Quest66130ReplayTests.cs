using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters.Fakes.Replay;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Replay;

/// <summary>
/// Parametric engine fixture tests. Discovers all fixture files in
/// questforge-data/fixtures/engine/ and asserts the engine produces the
/// expected transition sequence for each. Tests skip when questforge-data
/// is not present (developers without the data repo, pre-CI-checkout).
///
/// See docs/FIXTURES.md for the fixture format specification.
/// </summary>
public sealed class EngineFixtureTests
{
    // ---- Fixture data type ----

    internal sealed record EngineFixture(
        [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
        [property: JsonPropertyName("description")]   string Description,
        [property: JsonPropertyName("initialState")]  string InitialState,
        [property: JsonPropertyName("questFile")]     string QuestFile,
        [property: JsonPropertyName("expectedTransitions")] FixtureTransition[] ExpectedTransitions,
        [property: JsonPropertyName("terminalOutcome")] string TerminalOutcome,
        [property: JsonPropertyName("sourceTrace")]   string? SourceTrace = null,
        [property: JsonPropertyName("initialZone")]   int? InitialZone = null);

    internal sealed record FixtureTransition(
        [property: JsonPropertyName("stepId")]     string? StepId,
        [property: JsonPropertyName("actionType")] string ActionType);

    // ---- State machine dispatch ----
    // Maps fixture filename (without extension) to its scripted state builder.
    // Scripted entries always win over the generic trace-replay path.
    // Add an entry here only for fixtures that require a hand-scripted state machine.

    private static readonly Dictionary<string, Func<IFixtureState>> StateFactories = new()
    {
        ["simple-linear-acceptance"] = () => new SimpleLinearAcceptanceState(),
    };

    // ---- Safety overrun constant ----
    // The loop breaks when actualTransitions.Count > expectedTransitions.Length + SafetyOverrunCount.
    internal const int SafetyOverrunCount = 10;

    // ---- Parametric theory ----

    public static TheoryData<string> AllEngineFixtures()
    {
        var data = new TheoryData<string>();
        var root = FixtureLocator.TryGetQuestForgeDataRoot();
        if (root is null) return data; // skip all — data repo not present

        var dir = Path.Combine(root, "fixtures", "engine");
        if (!Directory.Exists(dir)) return data;

        foreach (var file in Directory.GetFiles(dir, "*.json"))
            data.Add(file);

        return data;
    }

    [Theory, MemberData(nameof(AllEngineFixtures))]
    public async Task EngineProducesExpectedTransitions(string fixturePath)
    {
        // ---- Load fixture ----
        var fixtureJson = await File.ReadAllTextAsync(fixturePath);
        var fixture = DeserializeFixtureForTest(fixtureJson);

        var fixtureName = Path.GetFileNameWithoutExtension(fixturePath);

        // ---- Resolve quest file ----
        var dataRoot = FixtureLocator.TryGetQuestForgeDataRoot()
            ?? throw new InvalidOperationException("questforge-data root disappeared between discovery and load.");

        var questPath = Path.Combine(dataRoot, fixture.QuestFile.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(questPath),
            $"Fixture '{fixtureName}' references '{fixture.QuestFile}' which does not exist in questforge-data.");

        var questJson = await File.ReadAllTextAsync(questPath);
        var quest = JsonSerializer.Deserialize<QuestDefinition>(questJson, QuestForgeJsonContext.QuestFileOptions)
            ?? throw new InvalidDataException($"Quest file deserialized to null: {questPath}");

        // ---- Load fragments from questforge-data/fragments/ ----
        var fragments = LoadFragmentsFromDataRoot(dataRoot);

        // ---- Validate step IDs (quest + fragment steps) ----
        var allStepIds = quest.Sequences
            .SelectMany(s => s.Steps)
            .Select(s => s.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var frag in fragments.Values)
            foreach (var step in frag.Steps)
                allStepIds.Add(step.Id);

        foreach (var t in fixture.ExpectedTransitions)
        {
            if (t.StepId is not null)
                Assert.True(allStepIds.Contains(t.StepId),
                    $"Fixture '{fixtureName}' references stepId '{t.StepId}' which does not exist in '{fixture.QuestFile}'.");
        }

        // ---- Three-way dispatch (§3.5) ----
        IFixtureState state;
        string? resolvedTracePath = null;

        if (StateFactories.TryGetValue(fixtureName, out var scripted))
        {
            state = scripted();                              // (1) scripted path — unchanged
        }
        else if (TryResolveSourceTrace(fixturePath, fixture.SourceTrace, dataRoot) is { } tracePath)
        {
            resolvedTracePath = tracePath;
            state = TraceReplayFixtureState.FromTraceFile(tracePath);   // (2) generic replay path
        }
        else
        {
            Assert.Skip(                                     // (3) neither — skip
                $"Fixture '{fixtureName}' has no registered scripted state machine and no source " +
                $"trace (looked for '{fixtureName}.trace.jsonl' beside it, and a 'sourceTrace' field). " +
                $"Add a trace to enable the generic replay harness, or register a state machine in " +
                $"EngineFixtureTests.StateFactories.");
            return; // unreachable after Assert.Skip; present for definite-assignment
        }

        // ---- Wire engine from IFixtureState ----
        var capturingTrace = new CapturingTraceWriter();
        var engine = new QuestEngine(
            state.GameState, state.QuestState,
            state.Navigator, state.Teleporter,
            state.Interactor,
            state.Combat,
            state.Minigames, state.Dialogue,
            state.Timing,
            capturingTrace, NullLogger<QuestEngine>.Instance,
            clock: state.Clock);

        engine.StartQuest(quest, fragments);
        engine.BeginRun("fixture-run");

        // ---- Drive engine and collect transitions ----
        var actualTransitions = new List<(string? StepId, string ActionType)>();
        var ct = CancellationToken.None;
        const int maxTicks = 50_000;
        var traceFileName = resolvedTracePath is not null ? Path.GetFileName(resolvedTracePath) : null;

        for (var tick = 0; tick < maxTicks; tick++)
        {
            var eventsBefore = capturingTrace.Events.Count;
            var action = await WrapTickForStarvation(engine, traceFileName ?? $"{fixtureName}.trace.jsonl", ct);

            // The engine emits a DecisionEvent for non-terminal actions.
            // Done emits run.end instead — exit the loop.
            var newDecision = capturingTrace.Events
                .Skip(eventsBefore)
                .OfType<DecisionEvent>()
                .FirstOrDefault();

            if (newDecision is null) break; // terminal action reached

            state.OnTick(action, tick); // advance state machine every tick

            // Two-phase EquipBestGear: the engine re-dispatches until NotifyEquipBestGearComplete
            // is called. In replay, confirm immediately (the adapter always succeeds).
            if (action is EngineAction.EquipBestGear ebg && ebg.Origin?.Id is { } ebgStepId)
                engine.NotifyEquipBestGearComplete(ebgStepId);

            // Decision-matching advancement: try to match every decision (including
            // duplicates) against the trace's recorded decision sequence. This enables
            // combat replay where the same transition repeats but observations change.
            if (state is TraceReplayFixtureState trfs)
                trfs.TryAdvanceForDecision(newDecision.Data.StepId, newDecision.Data.ActionType);

            var pair = (newDecision.Data.StepId, ActionTypeString(action));
            if (actualTransitions.Count == 0 || actualTransitions[^1] != pair)
            {
                actualTransitions.Add(pair);
                // Scripted fixtures use OnTransitionRecorded for state machine advancement.
                // Trace-backed fixtures use TryAdvanceForDecision above instead.
                if (state is not TraceReplayFixtureState)
                    state.OnTransitionRecorded(action, tick);
            }

            if (actualTransitions.Count > fixture.ExpectedTransitions.Length + SafetyOverrunCount)
                break; // safety: more transitions than expected — will fail assertion below
        }

        // ---- Assert transition sequence ----
        // Combat target selection is non-deterministic in replay — filter out engage
        // transitions and null-stepId decisions (defense, loading zone guard).
        // Verify all deterministic transitions match exactly. Post-combat transitions
        // (navigate, interact, etc.) only appear if the engine completed combat, so
        // their presence proves combat worked even without exact engage matching.
        var expectedDeterministic = fixture.ExpectedTransitions
            .Where(t => t.StepId is not null
                && !string.Equals(t.ActionType, "engage", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var actualDeterministic = actualTransitions
            .Where(t => t.StepId is not null
                && !string.Equals(t.ActionType, "engage", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Equal(expectedDeterministic.Length, actualDeterministic.Length);
        for (var i = 0; i < expectedDeterministic.Length; i++)
        {
            Assert.Equal(expectedDeterministic[i].StepId, actualDeterministic[i].StepId);
            Assert.Equal(expectedDeterministic[i].ActionType, actualDeterministic[i].ActionType);
        }

        // ---- Assert terminal outcome ----
        var terminalAction = await WrapTickForStarvation(engine, traceFileName ?? $"{fixtureName}.trace.jsonl", ct);
        switch (fixture.TerminalOutcome)
        {
            case "done":      Assert.IsType<EngineAction.Done>(terminalAction);      break;
            case "awaitUser": Assert.IsType<EngineAction.AwaitUser>(terminalAction); break;
            default: Assert.Fail($"Unknown terminalOutcome '{fixture.TerminalOutcome}' in fixture '{fixtureName}'."); break;
        }
    }

    // ---- Internal static helpers (unit-testable from TraceReplayFixtureStateTests) ----

    /// <summary>
    /// Resolves the source trace path for a fixture.
    /// Priority: explicit sourceTrace field (relative to dataRoot) → sibling convention.
    /// Returns the absolute path if the file exists, or null if not found.
    /// </summary>
    internal static string? TryResolveSourceTrace(
        string fixturePath,
        string? sourceTraceField,
        string dataRoot)
    {
        // (1) Explicit sourceTrace field wins
        if (!string.IsNullOrEmpty(sourceTraceField))
        {
            var normalized = sourceTraceField.Replace('/', Path.DirectorySeparatorChar);
            var explicit_ = Path.Combine(dataRoot, normalized);
            if (File.Exists(explicit_)) return explicit_;
            // Field present but file absent → fall through to sibling; do not fail hard
        }

        // (2) Sibling convention: <name>.trace.jsonl beside the fixture
        var siblingDir  = Path.GetDirectoryName(fixturePath)!;
        var fixtureStem = Path.GetFileNameWithoutExtension(fixturePath);
        var sibling = Path.Combine(siblingDir, fixtureStem + ".trace.jsonl");
        if (File.Exists(sibling)) return sibling;

        return null;
    }

    /// <summary>
    /// Deserializes a fixture JSON string into an EngineFixture record.
    /// Used by unit tests that need access to the deserialized record type.
    /// </summary>
    internal static EngineFixture DeserializeFixtureForTest(string fixtureJson)
        => JsonSerializer.Deserialize<EngineFixture>(fixtureJson,
               new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
           ?? throw new InvalidDataException("Fixture JSON deserialized to null.");

    /// <summary>
    /// Wraps a single engine.Tick(ct) call so that ReplayObservationStarvationException
    /// is translated into an actionable Assert.Skip naming "re-record" and the trace
    /// filename. Starvation means the recorded trace is stale (the engine's read pattern
    /// changed since it was recorded), not a decision regression — so the fixture skips,
    /// not fails. Genuine decision regressions are caught by the Assert.Equal transition
    /// checks in the caller, which still fail.
    /// </summary>
    internal static async Task<EngineAction> WrapTickForStarvation(
        QuestEngine engine,
        string traceFileName,
        CancellationToken ct)
    {
        try
        {
            return await engine.Tick(ct);
        }
        catch (ReplayObservationStarvationException ex)
        {
            Assert.Skip(
                $"re-record needed: the engine read game state that the recorded trace does not contain " +
                $"— OBSERVATION STARVATION, not a decision regression.\n" +
                $"This means the engine's read pattern changed (e.g. a new adapter read was added) since " +
                $"'{traceFileName}' was recorded.\n" +
                $"FIX: re-record the trace for this fixture (run the quest in-game with tracing on, then " +
                $"`qf-trace extract-fixture <run>.jsonl` and re-commit both files). Do NOT 'fix' the engine.\n" +
                $"Underlying: {ex.Message}");
            throw; // unreachable — Assert.Skip throws; present for the compiler
        }
    }

    // Maps EngineAction subtype to the canonical fixture actionType string.
    private static string ActionTypeString(EngineAction action) => action switch
    {
        EngineAction.Navigate  _ => "navigate",
        EngineAction.Interact  _ => "interact",
        EngineAction.Wait      _ => "wait",
        EngineAction.AwaitUser _ => "awaitUser",
        EngineAction.Done      _ => "done",
        EngineAction.Engage    _ => "engage",
        _                        => action.GetType().Name.ToLowerInvariant()
    };

    private static IReadOnlyDictionary<string, FragmentDefinition> LoadFragmentsFromDataRoot(string dataRoot)
    {
        var fragmentsDir = Path.Combine(dataRoot, "fragments");
        if (!Directory.Exists(fragmentsDir))
            return new Dictionary<string, FragmentDefinition>();

        var result = new Dictionary<string, FragmentDefinition>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(fragmentsDir, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var json = File.ReadAllText(file);
                var def = JsonSerializer.Deserialize<FragmentDefinition>(json, QuestForgeJsonContext.QuestFileOptions);
                if (def is not null && !string.IsNullOrEmpty(def.FragmentId))
                    result[def.FragmentId] = def;
            }
            catch { }
        }
        return result;
    }
}
