using System.Text.Json;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using QuestId = QuestForge.Adapters.Types.QuestId;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// RED PHASE: Tests for fragment execution in QuestEngine.
///
/// ALL tests fail because QuestEngine.StartQuest does not yet have a `fragments` parameter:
///
///   public void StartQuest(
///       QuestDefinition quest,
///       IReadOnlyDictionary&lt;string, FragmentDefinition&gt;? fragments = null)
///
/// The engine must expand FragmentStep entries in-place before the step evaluation loop
/// runs, substituting parameter placeholders (${name}) in string-typed expect predicates
/// and validating that all referenced fragments and required parameters are present.
///
/// BUILDER GUIDANCE â€” implementation checklist:
///   1. Add the `fragments` parameter to StartQuest (optional, default null).
///   2. On StartQuest: validate all FragmentStep refs resolve in the fragments dict.
///   3. On StartQuest: validate all required parameters are supplied per FragmentStep.
///   4. On StartQuest: reject nested FragmentStep inside a FragmentDefinition.Steps.
///   5. Expand FragmentStep entries into concrete steps, scoping Ids to avoid collisions
///      (e.g. "{fragmentStepId}:{originalStepId}").
///   6. Substitute ${paramName} tokens in ExpectValue predicates using FragmentStep.Params.
///   7. The confirmed-step cursor must work on the expanded (scoped) step Ids.
///
/// Run: dotnet test QuestForge.Engine.Tests --filter FragmentExecutionTests -v detailed
/// </summary>
public sealed class FragmentExecutionTests
{
    // =========================================================================
    // Helpers
    // =========================================================================

    private static JsonElement JsonPos(float x, float y, float z) =>
        JsonSerializer.SerializeToElement(new { x, y, z });

    private static JsonElement JsonStr(string s) =>
        JsonSerializer.SerializeToElement(s);

    /// <summary>
    /// Builds a minimal QuestDefinition with a single sequence block (Sequence=0).
    /// </summary>
    private static QuestDefinition BuildSyntheticQuest(uint questId, params Step[] steps) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "Fragment Test Quest",
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
                    Steps = steps
                }
            ]
        };

    private static TravelStep SimpleTravelStep(string id, Position3 dest, string? expectPredicate = null) =>
        new()
        {
            Id = id,
            Destination = new TravelDestination(Zone: 182, Position: dest),
            Expect = expectPredicate is not null
                ? new PredicateExpect { Predicate = expectPredicate }
                : null
        };

    private static FragmentStep SimpleFragmentStep(string id, string fragmentRef,
        Dictionary<string, JsonElement>? parms = null) =>
        new()
        {
            Id = id,
            Ref = fragmentRef,
            Params = parms
        };

    private static FragmentDefinition SimpleFragment(string fragmentId, params Step[] steps) =>
        new()
        {
            SchemaVersion = "1.0.0",
            FragmentId = fragmentId,
            Parameters = [],
            Steps = steps
        };

    // =========================================================================
    // Group A â€” Fragment expansion basics
    // =========================================================================

    // -------------------------------------------------------------------------
    // A1: single fragment step expands to concrete steps
    // -------------------------------------------------------------------------

    [Fact]
    public async Task A1_single_fragment_step_expands_to_concrete_steps()
    {
        /*
         * RED: Will fail until Builder adds the `fragments` parameter to StartQuest
         * and implements FragmentStep expansion.
         *
         * CONTRACT: Given fragment "nav/step-a" containing one TravelStep (dest 100,0,200),
         *           AND a quest whose only step is a FragmentStep {Ref="nav/step-a"},
         *           AND the player is at (0,0,0),
         *           When StartQuest(quest, fragments) and Tick,
         *           Then the engine emits Navigate(100,0,200) (the fragment's TravelStep).
         *
         * BUILDER GUIDANCE:
         *   The FragmentStep must be replaced in the step list by the fragment's inner steps
         *   before the step evaluation loop runs. The inner TravelStep has no expect,
         *   so it is never confirmed â€” Navigate is returned immediately.
         */

        // Arrange
        const uint questId = 80001;
        var destPos = new Position3(100f, 0f, 200f);

        var fragment = SimpleFragment("nav/step-a",
            new TravelStep
            {
                Id = "to-target",
                Destination = new TravelDestination(Zone: 182, Position: destPos)
            });

        var quest = BuildSyntheticQuest(questId,
            SimpleFragmentStep("frag-ref-step", "nav/step-a"));

        var fragments = new Dictionary<string, FragmentDefinition>
        {
            ["nav/step-a"] = fragment
        };

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-a1");

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(100f, 0f, 200f), navigate.Destination);
    }

    // -------------------------------------------------------------------------
    // A2: fragment steps inserted at the correct position in the sequence
    // -------------------------------------------------------------------------

    [Fact]
    public async Task A2_fragment_steps_inserted_in_correct_sequence_position()
    {
        /*
         * RED: Will fail until Builder implements fragment expansion at the correct list offset.
         *
         * CONTRACT: Given fragment "nav/x" containing one TravelStep (id "frag-mid", dest 50,0,50,
         *               expect "playerZone() == 999"),
         *           AND quest sequence: [
         *               TravelStep("step-a", dest 10,0,10, expect "playerZone()==129"),
         *               FragmentStep("frag-use", Ref="nav/x"),
         *               TravelStep("step-b", dest 90,0,90)
         *           ],
         *           AND player zone == 129 (step-a's expect is true, so step-a is confirmed),
         *           When Tick,
         *           Then Navigate(50,0,50) is returned (frag-mid is the next unconfirmed step;
         *                step-b is not reached because frag-mid has no satisfied expect).
         *
         * BUILDER GUIDANCE:
         *   After expansion the effective step list is [step-a, frag-mid, step-b].
         *   step-a expect "playerZone()==129" is true â†’ confirmed + skip.
         *   frag-mid expect "playerZone()==999" is false (zone is 129) â†’ Navigate(50,0,50).
         */

        // Arrange
        const uint questId = 80002;

        var fragment = SimpleFragment("nav/x",
            new TravelStep
            {
                Id = "frag-mid",
                Destination = new TravelDestination(Zone: 182, Position: new Position3(50f, 0f, 50f)),
                Expect = new PredicateExpect { Predicate = "playerZone() == 999" }
            });

        var quest = BuildSyntheticQuest(questId,
            SimpleTravelStep("step-a", new Position3(10f, 0f, 10f), "playerZone()==129"),
            SimpleFragmentStep("frag-use", "nav/x"),
            SimpleTravelStep("step-b", new Position3(90f, 0f, 90f)));

        var fragments = new Dictionary<string, FragmentDefinition>
        {
            ["nav/x"] = fragment
        };

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(129));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-a2");

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert â€” step-a confirmed; frag-mid is next (zone 129 != 999, expect false) â†’ Navigate
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(50f, 0f, 50f), navigate.Destination);
    }

    // -------------------------------------------------------------------------
    // A3: fragment with multiple steps â€” all expanded and processed in order
    // -------------------------------------------------------------------------

    [Fact]
    public async Task A3_fragment_with_multiple_steps_all_expanded()
    {
        /*
         * RED: Will fail until Builder expands all steps from a multi-step fragment.
         *
         * CONTRACT: Given fragment "nav/multi" with 3 TravelSteps:
         *               frag-1: dest (10,0,10), expect "playerZone()==100"
         *               frag-2: dest (20,0,20), expect "playerZone()==200"
         *               frag-3: dest (30,0,30), no expect
         *           AND quest has one FragmentStep {Ref="nav/multi"},
         *           When zone 0 â†’ Tick returns Navigate(10,0,10),
         *           When zone 100 â†’ Tick returns Navigate(20,0,20) (frag-1 confirmed),
         *           When zone 200 â†’ Tick returns Navigate(30,0,30) (frag-1 and frag-2 confirmed).
         *
         * BUILDER GUIDANCE:
         *   All 3 inner steps must appear in the expansion, in source order.
         *   The confirmed-step cursor accumulates confirmations across ticks within the same
         *   sequence, so each tick confirms one more step until frag-3 (no expect) is reached.
         */

        // Arrange
        const uint questId = 80003;

        var fragment = new FragmentDefinition
        {
            SchemaVersion = "1.0.0",
            FragmentId = "nav/multi",
            Parameters = [],
            Steps =
            [
                new TravelStep
                {
                    Id = "frag-1",
                    Destination = new TravelDestination(Zone: 182, Position: new Position3(10f, 0f, 10f)),
                    Expect = new PredicateExpect { Predicate = "playerZone()==100" }
                },
                new TravelStep
                {
                    Id = "frag-2",
                    Destination = new TravelDestination(Zone: 182, Position: new Position3(20f, 0f, 20f)),
                    Expect = new PredicateExpect { Predicate = "playerZone()==200" }
                },
                new TravelStep
                {
                    Id = "frag-3",
                    Destination = new TravelDestination(Zone: 182, Position: new Position3(30f, 0f, 30f))
                    // no expect
                }
            ]
        };

        var quest = BuildSyntheticQuest(questId,
            SimpleFragmentStep("frag-use", "nav/multi"));

        var fragments = new Dictionary<string, FragmentDefinition>
        {
            ["nav/multi"] = fragment
        };

        // Single harness across all ticks so the confirmed-step cursor accumulates
        // confirmations within the same sequence (as the contract docstring describes).
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(0));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-a3");

        // Tick 1 â€” zone 0: frag-1 expect false â†’ Navigate(10,0,10)
        var action1 = await harness.Engine.Tick(CancellationToken.None);
        var nav1 = Assert.IsType<EngineAction.Navigate>(action1);
        Assert.Equal(new WorldPosition(10f, 0f, 10f), nav1.Destination);

        // Tick 2 â€” zone 100: frag-1 expect true (now confirmed), frag-2 expect false â†’ Navigate(20,0,20)
        harness.GameState.SetZone(new ZoneId(100));
        var action2 = await harness.Engine.Tick(CancellationToken.None);
        var nav2 = Assert.IsType<EngineAction.Navigate>(action2);
        Assert.Equal(new WorldPosition(20f, 0f, 20f), nav2.Destination);

        // Tick 3 â€” zone 200: frag-1 already confirmed from Tick 2, frag-2 expect true (now confirmed),
        // frag-3 has no expect â†’ Navigate(30,0,30)
        harness.GameState.SetZone(new ZoneId(200));
        var action3 = await harness.Engine.Tick(CancellationToken.None);
        var nav3 = Assert.IsType<EngineAction.Navigate>(action3);
        Assert.Equal(new WorldPosition(30f, 0f, 30f), nav3.Destination);
    }

    // =========================================================================
    // Group B â€” Parameter substitution
    // =========================================================================

    // B1 â€” position parameter substitution â€” SKIPPED
    // TravelDestination.Position is Position3? (a typed C# struct), not a string.
    // Putting "${targetPos}" in it is not representable. This test requires a Builder
    // design decision on how position placeholders are represented in the in-memory
    // schema before the substitution strategy can be pinned down.
    // Re-introduce B1 once the Builder documents the position placeholder representation.

    // -------------------------------------------------------------------------
    // B2: required string parameter substituted into expect predicate
    // -------------------------------------------------------------------------

    [Fact]
    public async Task B2_required_string_parameter_substituted_in_expect_predicate()
    {
        /*
         * RED: Will fail until Builder implements ${name} token substitution in
         * expect predicates when expanding FragmentStep.
         *
         * CONTRACT: Given fragment "nav/param-zone" declaring parameter {name:"zone", type:"string", required:true},
         *           AND fragment TravelStep (id "t", dest (0,0,0)):
         *               expect = "playerZone() == ${zone}"
         *           AND FragmentStep.Params = {"zone": JsonStr("129")},
         *           AND player zone == 129,
         *           When StartQuest(quest, fragments) and Tick,
         *           Then the step's expect evaluates true (${zone} substituted to "129"),
         *                the step is confirmed, no more steps remain â†’ Wait is returned.
         *
         * BUILDER GUIDANCE:
         *   Before registering the expanded step in the step list, replace ${zone} in the
         *   expect predicate string with the string value of the corresponding Param entry.
         *   After substitution the predicate becomes "playerZone() == 129" which evaluates
         *   true at zone 129 â†’ step is confirmed â†’ loop exhausts â†’ Wait.
         */

        // Arrange
        const uint questId = 80004;

        var fragment = new FragmentDefinition
        {
            SchemaVersion = "1.0.0",
            FragmentId = "nav/param-zone",
            Parameters = [new FragmentParameter("zone", "string", Required: true)],
            Steps =
            [
                new TravelStep
                {
                    Id = "t",
                    Destination = new TravelDestination(Zone: 182, Position: new Position3(0f, 0f, 0f)),
                    Expect = new PredicateExpect { Predicate = "playerZone() == ${zone}" }
                }
            ]
        };

        var quest = BuildSyntheticQuest(questId,
            new FragmentStep
            {
                Id = "frag-ref-step",
                Ref = "nav/param-zone",
                Params = new Dictionary<string, JsonElement>
                {
                    { "zone", JsonStr("129") }
                }
            });

        var fragments = new Dictionary<string, FragmentDefinition>
        {
            ["nav/param-zone"] = fragment
        };

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(129));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-b2");

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert â€” ${zone} â†’ "129"; predicate true â†’ step confirmed; no more steps â†’ Wait
        Assert.IsType<EngineAction.Wait>(action);
    }

    // -------------------------------------------------------------------------
    // B3: missing required parameter throws at StartQuest
    // -------------------------------------------------------------------------

    [Fact]
    public void B3_missing_required_parameter_throws_at_start_quest()
    {
        /*
         * RED: Will fail until Builder validates required parameters at StartQuest time.
         *
         * CONTRACT: Given fragment "nav/param-zone" declaring required parameter "zone",
         *           AND FragmentStep.Params = {} (empty â€” "zone" not supplied),
         *           When StartQuest(quest, fragments),
         *           Then an exception is thrown before any tick occurs.
         *
         * BUILDER GUIDANCE:
         *   During fragment expansion (inside StartQuest), for each FragmentStep:
         *     foreach required parameter in the fragment's Parameters list:
         *       if (!step.Params.ContainsKey(param.Name)) throw;
         *   Use InvalidOperationException or ArgumentException â€” any exception type is acceptable.
         */

        // Arrange
        const uint questId = 80005;

        var fragment = new FragmentDefinition
        {
            SchemaVersion = "1.0.0",
            FragmentId = "nav/param-zone",
            Parameters = [new FragmentParameter("zone", "string", Required: true)],
            Steps =
            [
                new TravelStep
                {
                    Id = "t",
                    Destination = new TravelDestination(Zone: 182, Position: new Position3(0f, 0f, 0f)),
                    Expect = new PredicateExpect { Predicate = "playerZone() == ${zone}" }
                }
            ]
        };

        var quest = BuildSyntheticQuest(questId,
            new FragmentStep
            {
                Id = "frag-ref-step",
                Ref = "nav/param-zone",
                Params = new Dictionary<string, JsonElement>() // "zone" intentionally absent
            });

        var fragments = new Dictionary<string, FragmentDefinition>
        {
            ["nav/param-zone"] = fragment
        };

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);

        // Act & Assert
        Assert.ThrowsAny<Exception>(() => harness.Engine.StartQuest(quest, fragments));
    }

    // =========================================================================
    // Group C â€” Cursor interaction with expanded steps
    // =========================================================================

    // -------------------------------------------------------------------------
    // C1: expanded steps participate in the confirmed-step cursor
    // -------------------------------------------------------------------------

    [Fact]
    public async Task C1_expanded_steps_participate_in_confirmed_cursor()
    {
        /*
         * RED: Will fail until Builder ensures expanded fragment steps use the confirmed-step cursor.
         *
         * CONTRACT: Given fragment "nav/zone-check" containing TravelStep
         *               (id "travel", dest (50,0,50), expect "playerZone()==129"),
         *           AND quest has one FragmentStep {Ref="nav/zone-check"},
         *           AND player zone == 129 (expect true â†’ step confirmed on first tick),
         *           When tick 1 â†’ Wait (step confirmed, no more steps),
         *           THEN zone changes to 130 (expect would now be false),
         *           When tick 2 â†’ still Wait (cursor prevents re-execution of confirmed step).
         *
         * BUILDER GUIDANCE:
         *   The expanded step Id (scoped) must be inserted into _confirmedStepIds on tick 1
         *   when the expect evaluates true. On tick 2 the cursor check fires first,
         *   finds the scoped Id in the set, and skips the step â€” same as a non-fragment step.
         */

        // Arrange
        const uint questId = 80006;

        var fragment = SimpleFragment("nav/zone-check",
            new TravelStep
            {
                Id = "travel",
                Destination = new TravelDestination(Zone: 182, Position: new Position3(50f, 0f, 50f)),
                Expect = new PredicateExpect { Predicate = "playerZone()==129" }
            });

        var quest = BuildSyntheticQuest(questId,
            SimpleFragmentStep("frag-ref-step", "nav/zone-check"));

        var fragments = new Dictionary<string, FragmentDefinition>
        {
            ["nav/zone-check"] = fragment
        };

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(129)); // expect true
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-c1");

        // Act â€” tick 1: expect true â†’ step confirmed; no more steps â†’ Wait
        var action1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(action1);

        // Change zone so the expect would now be false if re-evaluated
        harness.GameState.SetZone(new ZoneId(130));

        // Act â€” tick 2: cursor prevents re-execution â†’ still Wait
        var action2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(action2);
    }

    // -------------------------------------------------------------------------
    // C2: same fragment used twice gets distinct scoped ids
    // -------------------------------------------------------------------------

    [Fact]
    public async Task C2_same_fragment_used_twice_gets_distinct_scoped_ids()
    {
        /*
         * RED: Will fail until Builder scopes expanded step Ids to prevent collisions.
         *
         * CONTRACT: Given fragment "nav/step-a" containing TravelStep
         *               (id "travel", dest (50,0,50), expect "playerZone()==129"),
         *           AND quest has TWO FragmentStep entries (ids "use-1" and "use-2"),
         *               both with Ref="nav/step-a",
         *           When zone 0 â†’ Tick returns Navigate(50,0,50)
         *               (neither copy confirmed yet),
         *           When zone 129 â†’ Tick returns Wait
         *               (both copies have distinct scoped Ids and both are confirmed),
         *           When zone 0 â†’ Tick returns still Wait
         *               (both confirmed by cursor; cursor not cleared â€” sequence unchanged).
         *
         * BUILDER GUIDANCE:
         *   If both expansions share the raw Id "travel", the cursor would see the same key
         *   for both â€” first confirmation would skip the second copy incorrectly (or both
         *   would resolve to the same cursor entry). Scoping prevents this:
         *   e.g. "use-1:travel" and "use-2:travel" are distinct cursor keys.
         *   At zone 129 both expects are true in the same tick â†’ both confirmed â†’ Wait.
         */

        // Arrange
        const uint questId = 80007;

        var fragment = SimpleFragment("nav/step-a",
            new TravelStep
            {
                Id = "travel",
                Destination = new TravelDestination(Zone: 182, Position: new Position3(50f, 0f, 50f)),
                Expect = new PredicateExpect { Predicate = "playerZone()==129" }
            });

        var quest = BuildSyntheticQuest(questId,
            new FragmentStep { Id = "use-1", Ref = "nav/step-a" },
            new FragmentStep { Id = "use-2", Ref = "nav/step-a" });

        var fragments = new Dictionary<string, FragmentDefinition>
        {
            ["nav/step-a"] = fragment
        };

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(0)); // expects false
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-c2");

        // Tick 1 â€” zone 0: neither confirmed â†’ Navigate(50,0,50)
        var action1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(action1);
        Assert.Equal(new WorldPosition(50f, 0f, 50f), ((EngineAction.Navigate)action1).Destination);

        // Zone 129: both expects now true
        harness.GameState.SetZone(new ZoneId(129));

        // Tick 2 â€” both copies confirmed in same tick â†’ Wait
        var action2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(action2);

        // Zone back to 0: expects would be false if re-evaluated
        harness.GameState.SetZone(new ZoneId(0));

        // Tick 3 â€” cursor intact; both still confirmed â†’ still Wait
        var action3 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(action3);
    }

    // =========================================================================
    // Group D â€” Error cases
    // =========================================================================

    // -------------------------------------------------------------------------
    // D1: no fragments provided but quest has a fragment step â†’ throws
    // -------------------------------------------------------------------------

    [Fact]
    public void D1_no_fragments_provided_fragment_step_throws()
    {
        /*
         * RED: Will fail until Builder validates fragment refs at StartQuest time.
         *
         * CONTRACT: Given a quest with one FragmentStep,
         *           When StartQuest(quest, null) (no fragments dict),
         *           Then an exception is thrown.
         *
         * BUILDER GUIDANCE:
         *   When fragments is null but a FragmentStep is present, there is nothing to resolve.
         *   Throw at StartQuest â€” do not defer the error to Tick.
         */

        // Arrange
        const uint questId = 80008;

        var quest = BuildSyntheticQuest(questId,
            SimpleFragmentStep("frag-ref-step", "nav/some-fragment"));

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);

        // Act & Assert
        Assert.ThrowsAny<Exception>(() => harness.Engine.StartQuest(quest, null));
    }

    // -------------------------------------------------------------------------
    // D2: fragment ref not present in dictionary â†’ throws
    // -------------------------------------------------------------------------

    [Fact]
    public void D2_fragment_ref_not_in_dictionary_throws()
    {
        /*
         * RED: Will fail until Builder validates that every FragmentStep.Ref resolves.
         *
         * CONTRACT: Given a quest with one FragmentStep {Ref="nav/missing"},
         *           AND the fragments dict does not contain "nav/missing",
         *           When StartQuest(quest, fragments),
         *           Then an exception is thrown.
         *
         * BUILDER GUIDANCE:
         *   During expansion: if fragments.TryGetValue(fragmentStep.Ref, out var def) fails,
         *   throw immediately. A KeyNotFoundException or InvalidOperationException is fine.
         */

        // Arrange
        const uint questId = 80009;

        var quest = BuildSyntheticQuest(questId,
            SimpleFragmentStep("frag-ref-step", "nav/missing"));

        // Dictionary exists but does not contain the referenced key
        var fragments = new Dictionary<string, FragmentDefinition>
        {
            ["nav/other"] = SimpleFragment("nav/other",
                new TravelStep
                {
                    Id = "unrelated",
                    Destination = new TravelDestination(Zone: 182, Position: new Position3(0f, 0f, 0f))
                })
        };

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);

        // Act & Assert
        Assert.ThrowsAny<Exception>(() => harness.Engine.StartQuest(quest, fragments));
    }

    // -------------------------------------------------------------------------
    // D3: nested fragment step inside a fragment definition â†’ throws
    // -------------------------------------------------------------------------

    [Fact]
    public void D3_nested_fragment_in_fragment_definition_throws()
    {
        /*
         * RED: Will fail until Builder rejects nested FragmentStep during expansion.
         *
         * CONTRACT: Given fragment "nav/outer" whose Steps contains a FragmentStep {Ref="nav/inner"},
         *           AND fragment "nav/inner" has a TravelStep,
         *           AND quest has one FragmentStep {Ref="nav/outer"},
         *           AND both fragments are provided,
         *           When StartQuest(quest, fragments),
         *           Then an exception is thrown (nested fragments not supported).
         *
         * BUILDER GUIDANCE:
         *   During expansion of "nav/outer", when iterating its Steps, if a step is a
         *   FragmentStep, throw immediately with a descriptive message.
         *   Nested expansion would require recursive resolution â€” out of scope for Phase 4.
         *   Keeping fragments flat avoids composability pitfalls and circular ref risks.
         */

        // Arrange
        const uint questId = 80010;

        var innerFragment = SimpleFragment("nav/inner",
            new TravelStep
            {
                Id = "inner-travel",
                Destination = new TravelDestination(Zone: 182, Position: new Position3(10f, 0f, 10f))
            });

        // "nav/outer" contains a FragmentStep â€” this is the nested case that must be rejected
        var outerFragment = new FragmentDefinition
        {
            SchemaVersion = "1.0.0",
            FragmentId = "nav/outer",
            Parameters = [],
            Steps =
            [
                new FragmentStep
                {
                    Id = "nested-frag",
                    Ref = "nav/inner"
                }
            ]
        };

        var quest = BuildSyntheticQuest(questId,
            SimpleFragmentStep("frag-ref-step", "nav/outer"));

        var fragments = new Dictionary<string, FragmentDefinition>
        {
            ["nav/outer"] = outerFragment,
            ["nav/inner"] = innerFragment
        };

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);

        // Act & Assert
        Assert.ThrowsAny<Exception>(() => harness.Engine.StartQuest(quest, fragments));
    }
}