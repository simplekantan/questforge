using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Predicates;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Predicates;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Predicates;

/// <summary>
/// RED PHASE: Tests for the isSlotEquipped(slotIndex) predicate function.
/// Spec: docs/IS_SLOT_EQUIPPED_PREDICATE_PLAN.md -- Scenarios S12-S23.
///
/// ALL tests in this file will fail until Builder adds:
///   - "isSlotEquipped" arm in PredicateEvaluator.EvaluateFunction (TODO)
///
/// The adapter method GetEquippedItemLevelForSlot already exists on
/// IGameStateProvider/FakeGameStateProvider. No adapter changes needed.
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~IsSlotEquippedPredicateTests"
/// </summary>
public sealed class IsSlotEquippedPredicateTests
{
    private static PredicateEvaluator Evaluator(FakeGameStateProvider? gameState = null)
    {
        gameState ??= new FakeGameStateProvider();
        return new PredicateEvaluator(gameState, new FakeQuestState());
    }

    private static string FormatErrors(ParseResult result)
        => string.Join("; ", result.Errors.Select(e => $"[{e.Code}] {e.Message}"));

    // =========================================================================
    // S12 -- Slot equipped returns true
    // =========================================================================

    [Fact]
    public async Task SlotEquipped_ReturnsTrue_S12()
    {
        // CONTRACT: Given SetEquippedItemLevelForSlot(3, 55) (body slot, ilvl 55),
        //           When evaluating "isSlotEquipped(3)",
        //           Then result is true.
        var gs = new FakeGameStateProvider();
        gs.SetEquippedItemLevelForSlot(3, 55);
        var evaluator = Evaluator(gs);
        var parseResult = PredicateParser.Parse("isSlotEquipped(3)");
        Assert.True(parseResult.IsSuccess, $"Parse failed: {FormatErrors(parseResult)}");
        var ast = parseResult.Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.True(result, "isSlotEquipped(3) should be true when body slot has ilvl 55");
    }

    // =========================================================================
    // S13 -- Slot empty returns false
    // =========================================================================

    [Fact]
    public async Task SlotEmpty_ReturnsFalse_S13()
    {
        // CONTRACT: Given no SetEquippedItemLevelForSlot calls (all slots default to 0),
        //           When evaluating "isSlotEquipped(3)",
        //           Then result is false.
        var evaluator = Evaluator();
        var ast = PredicateParser.Parse("isSlotEquipped(3)").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.False(result, "isSlotEquipped(3) should be false when slot defaults to 0");
    }

    // =========================================================================
    // S14 -- Different slots are independent
    // =========================================================================

    [Fact]
    public async Task DifferentSlots_AreIndependent_S14()
    {
        // CONTRACT: Given SetEquippedItemLevelForSlot(2, 30) (head equipped), no other slots,
        //           When evaluating isSlotEquipped(2) -> true, isSlotEquipped(3) -> false.
        var gs = new FakeGameStateProvider();
        gs.SetEquippedItemLevelForSlot(2, 30);
        var evaluator = Evaluator(gs);

        var resultHead = await evaluator.Evaluate(PredicateParser.Parse("isSlotEquipped(2)").Ast!, CancellationToken.None);
        var resultBody = await evaluator.Evaluate(PredicateParser.Parse("isSlotEquipped(3)").Ast!, CancellationToken.None);

        Assert.True(resultHead, "isSlotEquipped(2) should be true when head slot has ilvl 30");
        Assert.False(resultBody, "isSlotEquipped(3) should be false when body slot is not set");
    }

    // =========================================================================
    // S15 -- Negation works
    // =========================================================================

    [Fact]
    public async Task Negation_ReturnsFalse_WhenSlotEquipped_S15()
    {
        // CONTRACT: Given SetEquippedItemLevelForSlot(3, 55),
        //           When evaluating "not isSlotEquipped(3)",
        //           Then result is false.
        var gs = new FakeGameStateProvider();
        gs.SetEquippedItemLevelForSlot(3, 55);
        var evaluator = Evaluator(gs);
        var ast = PredicateParser.Parse("not isSlotEquipped(3)").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.False(result, "not isSlotEquipped(3) should be false when slot 3 is equipped");
    }

    // =========================================================================
    // S16 -- Conjunction of multiple slots (quest 65999 use case)
    // =========================================================================

    [Fact]
    public async Task AllFiveQuestSlots_Equipped_ConjunctionTrue_S16()
    {
        // CONTRACT: Given slots 2,3,4,6,7 all set to ilvl 30,
        //           When evaluating "isSlotEquipped(2) and isSlotEquipped(3) and isSlotEquipped(4) and isSlotEquipped(6) and isSlotEquipped(7)",
        //           Then result is true.
        var gs = new FakeGameStateProvider();
        gs.SetEquippedItemLevelForSlot(2, 30);
        gs.SetEquippedItemLevelForSlot(3, 30);
        gs.SetEquippedItemLevelForSlot(4, 30);
        gs.SetEquippedItemLevelForSlot(6, 30);
        gs.SetEquippedItemLevelForSlot(7, 30);
        var evaluator = Evaluator(gs);
        var ast = PredicateParser.Parse(
            "isSlotEquipped(2) and isSlotEquipped(3) and isSlotEquipped(4) and isSlotEquipped(6) and isSlotEquipped(7)").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.True(result, "All five quest-relevant slots equipped should yield true");
    }

    // =========================================================================
    // S17 -- Conjunction fails when one slot is empty
    // =========================================================================

    [Fact]
    public async Task ConjunctionFails_WhenOneSlotEmpty_S17()
    {
        // CONTRACT: Given slots 2,3,4,7 set to ilvl 30 but slot 6 set to 0 (legs empty),
        //           When evaluating the same five-slot conjunction,
        //           Then result is false.
        var gs = new FakeGameStateProvider();
        gs.SetEquippedItemLevelForSlot(2, 30);
        gs.SetEquippedItemLevelForSlot(3, 30);
        gs.SetEquippedItemLevelForSlot(4, 30);
        gs.SetEquippedItemLevelForSlot(6, 0);
        gs.SetEquippedItemLevelForSlot(7, 30);
        var evaluator = Evaluator(gs);
        var ast = PredicateParser.Parse(
            "isSlotEquipped(2) and isSlotEquipped(3) and isSlotEquipped(4) and isSlotEquipped(6) and isSlotEquipped(7)").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.False(result, "Conjunction should be false when slot 6 (legs) is empty");
    }

    // =========================================================================
    // S18 -- Out-of-range slot index returns false (not throw)
    // =========================================================================

    [Fact]
    public async Task OutOfRangeSlot_ReturnsFalse_S18()
    {
        // CONTRACT: Given no slots set,
        //           When evaluating "isSlotEquipped(99)",
        //           Then result is false (adapter returns 0 for unknown slot).
        var evaluator = Evaluator();
        var ast = PredicateParser.Parse("isSlotEquipped(99)").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.False(result, "isSlotEquipped(99) should be false for out-of-range slot");
    }

    // =========================================================================
    // S19 -- Slot 0 (MainHand) works
    // =========================================================================

    [Fact]
    public async Task Slot0MainHand_Equipped_ReturnsTrue_S19()
    {
        // CONTRACT: Given SetEquippedItemLevelForSlot(0, 10),
        //           When evaluating "isSlotEquipped(0)",
        //           Then result is true.
        var gs = new FakeGameStateProvider();
        gs.SetEquippedItemLevelForSlot(0, 10);
        var evaluator = Evaluator(gs);
        var ast = PredicateParser.Parse("isSlotEquipped(0)").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.True(result, "isSlotEquipped(0) should be true when MainHand slot has ilvl 10");
    }

    // =========================================================================
    // S20 -- Slot 13 (SoulCrystal) boundary
    // =========================================================================

    [Fact]
    public async Task Slot13SoulCrystal_Equipped_ReturnsTrue_S20()
    {
        // CONTRACT: Given SetEquippedItemLevelForSlot(13, 1),
        //           When evaluating "isSlotEquipped(13)",
        //           Then result is true.
        var gs = new FakeGameStateProvider();
        gs.SetEquippedItemLevelForSlot(13, 1);
        var evaluator = Evaluator(gs);
        var ast = PredicateParser.Parse("isSlotEquipped(13)").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.True(result, "isSlotEquipped(13) should be true when SoulCrystal slot has ilvl 1");
    }

    // =========================================================================
    // S21 -- Composition with playerHasEquipped
    // =========================================================================

    [Fact]
    public async Task ComposedWithPlayerHasEquipped_BothTrue_S21()
    {
        // CONTRACT: Given SetEquippedItemLevelForSlot(3, 55) and SetItemEquipped(ItemId(4567), true),
        //           When evaluating "isSlotEquipped(3) and playerHasEquipped(4567)",
        //           Then result is true.
        var gs = new FakeGameStateProvider();
        gs.SetEquippedItemLevelForSlot(3, 55);
        gs.SetItemEquipped(new ItemId(4567), true);
        var evaluator = Evaluator(gs);
        var ast = PredicateParser.Parse("isSlotEquipped(3) and playerHasEquipped(4567)").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.True(result, "isSlotEquipped(3) and playerHasEquipped(4567) should both be true");
    }

    // =========================================================================
    // S22 -- Used in Expect postcondition
    // =========================================================================

    [Fact]
    public async Task IsSlotEquipped_InExpect_StepConfirmed_S22()
    {
        // CONTRACT: Given a quest step with Expect: "isSlotEquipped(3)" and slot 3 has ilvl 55,
        //           When engine evaluates the step's postcondition,
        //           Then postcondition passes (step completes).
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65999), 0);
        harness.GameState.SetEquippedItemLevelForSlot(3, 55);

        var step = new TalkStep
        {
            Id = "slot-equipped-expect",
            Target = new NpcLocation(1000000u, 128, new Position3(0f, 0f, 0f)),
            Expect = new PredicateExpect { Predicate = "isSlotEquipped(3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(65999, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Expect satisfied means postcondition passes; engine sees step as done
        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // S23 -- Used in SkipIf guard
    // =========================================================================

    [Fact]
    public async Task IsSlotEquipped_InSkipIf_StepSkipped_S23()
    {
        // CONTRACT: Given a quest step with SkipIf: "isSlotEquipped(3)" and slot 3 has ilvl 55,
        //           When engine evaluates the skip condition,
        //           Then step is skipped.
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65999), 0);
        harness.GameState.SetEquippedItemLevelForSlot(3, 55);

        var step = new TalkStep
        {
            Id = "slot-equipped-skipif",
            Target = new NpcLocation(1000000u, 128, new Position3(0f, 0f, 0f)),
            SkipIf = new PredicateExpect { Predicate = "isSlotEquipped(3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(65999, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Step skipped, engine moves past it
        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // Factory helper
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "IsSlotEquipped Predicate Test Quest",
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
                    Sequence = sequence,
                    Steps = [step]
                }
            ]
        };
}
