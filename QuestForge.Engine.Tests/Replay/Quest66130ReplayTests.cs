using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Gear;
using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.Minigames;
using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Fakes.Timing;
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

    private sealed record EngineFixture(
        [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
        [property: JsonPropertyName("description")]   string Description,
        [property: JsonPropertyName("initialState")]  string InitialState,
        [property: JsonPropertyName("questFile")]     string QuestFile,
        [property: JsonPropertyName("expectedTransitions")] FixtureTransition[] ExpectedTransitions,
        [property: JsonPropertyName("terminalOutcome")] string TerminalOutcome);

    private sealed record FixtureTransition(
        [property: JsonPropertyName("stepId")]     string? StepId,
        [property: JsonPropertyName("actionType")] string ActionType);

    // ---- State machine dispatch ----
    // Maps fixture filename (without extension) to its scripted state builder.
    // Add an entry here when adding a new fixture type.

    private static readonly Dictionary<string, Func<SimpleLinearAcceptanceState>> StateFactories = new()
    {
        ["simple-linear-acceptance"] = () => new SimpleLinearAcceptanceState(),
    };

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
        var fixture = JsonSerializer.Deserialize<EngineFixture>(fixtureJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"Fixture deserialized to null: {fixturePath}");

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

        // ---- Validate step IDs ----
        var allStepIds = quest.Sequences
            .SelectMany(s => s.Steps)
            .Select(s => s.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var t in fixture.ExpectedTransitions)
        {
            if (t.StepId is not null)
                Assert.True(allStepIds.Contains(t.StepId),
                    $"Fixture '{fixtureName}' references stepId '{t.StepId}' which does not exist in '{fixture.QuestFile}'.");
        }

        // ---- Get state machine ----
        if (!StateFactories.TryGetValue(fixtureName, out var stateFactory))
            Assert.Skip($"No state machine registered for fixture '{fixtureName}'. Add an entry to EngineFixtureTests.StateFactories.");

        var state = stateFactory();

        // ---- Wire engine ----
        var capturingTrace = new CapturingTraceWriter();
        var engine = new QuestEngine(
            state.GameState, state.QuestState,
            state.Navigator, new FakeTeleporter(state.GameState),
            new FakeInteractor(state.GameState, state.QuestState),
            new FakeCombat(), new FakeGearManager(),
            new FakeMinigameSkipper(), new FakeDialogueResolver(),
            new FakeTimingProfile(),
            capturingTrace, NullLogger<QuestEngine>.Instance);

        engine.StartQuest(quest);
        engine.BeginRun("fixture-run");

        // ---- Drive engine and collect transitions ----
        var actualTransitions = new List<(string? StepId, string ActionType)>();
        var ct = CancellationToken.None;
        const int maxTicks = 50_000;

        for (var tick = 0; tick < maxTicks; tick++)
        {
            var eventsBefore = capturingTrace.Events.Count;
            var action = await engine.Tick(ct);

            // The engine emits a DecisionEvent for non-terminal actions.
            // Done emits run.end instead — exit the loop.
            var newDecision = capturingTrace.Events
                .Skip(eventsBefore)
                .OfType<DecisionEvent>()
                .FirstOrDefault();

            if (newDecision is null) break; // terminal action reached

            state.OnTick(action, tick); // advance state machine

            var pair = (newDecision.StepId, ActionTypeString(action));
            if (actualTransitions.Count == 0 || actualTransitions[^1] != pair)
                actualTransitions.Add(pair);

            if (actualTransitions.Count > fixture.ExpectedTransitions.Length + 10)
                break; // safety: more transitions than expected — will fail assertion below
        }

        // ---- Assert transition sequence ----
        Assert.Equal(fixture.ExpectedTransitions.Length, actualTransitions.Count);
        for (var i = 0; i < fixture.ExpectedTransitions.Length; i++)
        {
            var expected = fixture.ExpectedTransitions[i];
            var actual = actualTransitions[i];
            Assert.Equal(expected.StepId, actual.StepId);
            Assert.Equal(expected.ActionType, actual.ActionType);
        }

        // ---- Assert terminal outcome ----
        var terminalAction = await engine.Tick(ct);
        switch (fixture.TerminalOutcome)
        {
            case "done":      Assert.IsType<EngineAction.Done>(terminalAction);      break;
            case "awaitUser": Assert.IsType<EngineAction.AwaitUser>(terminalAction); break;
            default: Assert.Fail($"Unknown terminalOutcome '{fixture.TerminalOutcome}' in fixture '{fixtureName}'."); break;
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
        _                        => action.GetType().Name.ToLowerInvariant()
    };
}
