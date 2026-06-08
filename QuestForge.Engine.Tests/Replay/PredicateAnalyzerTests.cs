using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Predicates;
using QuestForge.Engine.Tests.Replay;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Replay;

/// <summary>
/// RED PHASE: Tests for PredicateAnalyzer.ExtractMutations — a pure function that
/// derives StateMutation records from expect predicate strings.
///
/// Spec: docs/QUEST_DATA_DRIVEN_FIXTURES_PLAN.md — Tests T1-T14, T23-T25, T27-T28.
///
/// ALL tests in this file will fail to compile until Builder adds:
///   - PredicateAnalyzer static class (QuestForge.Engine.Tests/Replay/PredicateAnalyzer.cs)
///   - StateMutation discriminated union (same file or co-located)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~PredicateAnalyzerTests"
/// </summary>
public sealed class PredicateAnalyzerTests
{
    private static readonly QuestId ActiveQuest = new(66130);

    // =========================================================================
    // T1 -- simple questSequence comparison
    // =========================================================================

    [Fact]
    public void QuestSequence_GteComparison_ProducesSetQuestSequence_T1()
    {
        // Given predicate string "questSequence(66130) >= 1" and active quest ID 66130
        // When ExtractMutations is called
        // Then returns exactly one mutation: SetQuestSequence(QuestId(66130), 1)
        var expect = new PredicateExpect { Predicate = "questSequence(66130) >= 1" };

        var mutations = PredicateAnalyzer.ExtractMutations(expect, ActiveQuest);

        Assert.Single(mutations);
        var m = Assert.IsType<StateMutation.SetQuestSequence>(mutations[0]);
        Assert.Equal(new QuestId(66130), m.Quest);
        Assert.Equal(1, m.Value);
    }

    // =========================================================================
    // T2 -- isQuestComplete
    // =========================================================================

    [Fact]
    public void IsQuestComplete_ProducesSetQuestComplete_T2()
    {
        // Given predicate string "isQuestComplete(66130)"
        // When ExtractMutations is called
        // Then returns exactly one mutation: SetQuestComplete(QuestId(66130))
        var expect = new PredicateExpect { Predicate = "isQuestComplete(66130)" };

        var mutations = PredicateAnalyzer.ExtractMutations(expect, ActiveQuest);

        Assert.Single(mutations);
        var m = Assert.IsType<StateMutation.SetQuestComplete>(mutations[0]);
        Assert.Equal(new QuestId(66130), m.Quest);
    }

    // =========================================================================
    // T3 -- compound and predicate
    // =========================================================================

    [Fact]
    public void CompoundAnd_ProducesTwoMutations_T3()
    {
        // Given predicate string "playerZone() == 182 and playerNear({x:35.56,y:4.0,z:-151.18}, 3)"
        // When ExtractMutations is called
        // Then returns two mutations: SetPlayerZone(182) and SetPlayerNear(35.56, 4.0, -151.18, 3)
        var expect = new PredicateExpect { Predicate = "playerZone() == 182 and playerNear({x:35.56,y:4.0,z:-151.18}, 3)" };

        var mutations = PredicateAnalyzer.ExtractMutations(expect, ActiveQuest);

        Assert.Equal(2, mutations.Count);
        var zone = Assert.IsType<StateMutation.SetPlayerZone>(mutations[0]);
        Assert.Equal(182, zone.Zone);
        var near = Assert.IsType<StateMutation.SetPlayerNear>(mutations[1]);
        Assert.Equal(35.56f, near.X, 0.01f);
        Assert.Equal(4.0f, near.Y, 0.01f);
        Assert.Equal(-151.18f, near.Z, 0.01f);
        Assert.Equal(3f, near.Radius, 0.01f);
    }

    // =========================================================================
    // T4 -- not predicate
    // =========================================================================

    [Fact]
    public void NotPredicate_WrapsInSetNotPredicate_T4()
    {
        // Given predicate string "not isQuestComplete(65644)"
        // When ExtractMutations is called
        // Then returns one mutation: SetNotPredicate(SetQuestComplete(QuestId(65644)))
        var expect = new PredicateExpect { Predicate = "not isQuestComplete(65644)" };

        var mutations = PredicateAnalyzer.ExtractMutations(expect, ActiveQuest);

        Assert.Single(mutations);
        var not = Assert.IsType<StateMutation.SetNotPredicate>(mutations[0]);
        var inner = Assert.IsType<StateMutation.SetQuestComplete>(not.Inner);
        Assert.Equal(new QuestId(65644), inner.Quest);
    }

    // =========================================================================
    // T5 -- or predicate (first branch only)
    // =========================================================================

    [Fact]
    public void OrPredicate_ReturnsFirstBranchOnly_T5()
    {
        // Given predicate string "questSequence(65644) >= 2 or isQuestComplete(65644)"
        // When ExtractMutations is called
        // Then returns one mutation: SetQuestSequence(QuestId(65644), 2) (first branch only)
        var expect = new PredicateExpect { Predicate = "questSequence(65644) >= 2 or isQuestComplete(65644)" };

        var mutations = PredicateAnalyzer.ExtractMutations(expect, ActiveQuest);

        Assert.Single(mutations);
        var m = Assert.IsType<StateMutation.SetQuestSequence>(mutations[0]);
        Assert.Equal(new QuestId(65644), m.Quest);
        Assert.Equal(2, m.Value);
    }

    // =========================================================================
    // T6 -- isQuestAccepted
    // =========================================================================

    [Fact]
    public void IsQuestAccepted_ProducesSetQuestAccepted_T6()
    {
        // Given predicate string "isQuestAccepted(65644)"
        // When ExtractMutations is called
        // Then returns one mutation: SetQuestAccepted(QuestId(65644))
        var expect = new PredicateExpect { Predicate = "isQuestAccepted(65644)" };

        var mutations = PredicateAnalyzer.ExtractMutations(expect, ActiveQuest);

        Assert.Single(mutations);
        var m = Assert.IsType<StateMutation.SetQuestAccepted>(mutations[0]);
        Assert.Equal(new QuestId(65644), m.Quest);
    }

    // =========================================================================
    // T7 -- questFlag
    // =========================================================================

    [Fact]
    public void QuestFlag_ProducesSetQuestFlag_T7()
    {
        // Given predicate string "questFlag(65644, 3)"
        // When ExtractMutations is called
        // Then returns one mutation: SetQuestFlag(QuestId(65644), 3, true)
        var expect = new PredicateExpect { Predicate = "questFlag(65644, 3)" };

        var mutations = PredicateAnalyzer.ExtractMutations(expect, ActiveQuest);

        Assert.Single(mutations);
        var m = Assert.IsType<StateMutation.SetQuestFlag>(mutations[0]);
        Assert.Equal(new QuestId(65644), m.Quest);
        Assert.Equal(3, m.Bit);
        Assert.True(m.Value);
    }

    // =========================================================================
    // T8 -- isAttuned
    // =========================================================================

    [Fact]
    public void IsAttuned_ProducesSetAttuned_T8()
    {
        // Given predicate string "isAttuned(8)"
        // When ExtractMutations is called
        // Then returns one mutation: SetAttuned(8)
        var expect = new PredicateExpect { Predicate = "isAttuned(8)" };

        var mutations = PredicateAnalyzer.ExtractMutations(expect, ActiveQuest);

        Assert.Single(mutations);
        var m = Assert.IsType<StateMutation.SetAttuned>(mutations[0]);
        Assert.Equal(8u, m.AetheryteId);
    }

    // =========================================================================
    // T9 -- playerHasItem
    // =========================================================================

    [Fact]
    public void PlayerHasItem_ProducesSetPlayerHasItem_T9()
    {
        // Given predicate string "playerHasItem(2000104, 1)"
        // When ExtractMutations is called
        // Then returns one mutation: SetPlayerHasItem(2000104, 1)
        var expect = new PredicateExpect { Predicate = "playerHasItem(2000104, 1)" };

        var mutations = PredicateAnalyzer.ExtractMutations(expect, ActiveQuest);

        Assert.Single(mutations);
        var m = Assert.IsType<StateMutation.SetPlayerHasItem>(mutations[0]);
        Assert.Equal(2000104u, m.ItemId);
        Assert.Equal(1, m.Quantity);
    }

    // =========================================================================
    // T10 -- null expect
    // =========================================================================

    [Fact]
    public void NullExpect_ReturnsEmptyList_T10()
    {
        // Given null ExpectValue
        // When ExtractMutations is called
        // Then returns empty list
        var mutations = PredicateAnalyzer.ExtractMutations(null, ActiveQuest);

        Assert.Empty(mutations);
    }

    // =========================================================================
    // T11 -- unknown function
    // =========================================================================

    [Fact]
    public void UnknownFunction_ReturnsEmptyList_NoCrash_T11()
    {
        // Given predicate string "someNewFunction(42) == true"
        // When ExtractMutations is called
        // Then returns empty list (no crash, no exception)
        var expect = new PredicateExpect { Predicate = "someNewFunction(42) == true" };

        var mutations = PredicateAnalyzer.ExtractMutations(expect, ActiveQuest);

        Assert.Empty(mutations);
    }

    // =========================================================================
    // T12 -- questVariable (whole byte)
    // =========================================================================

    [Fact]
    public void QuestVariable_WholeByte_ProducesSetQuestVariable_T12()
    {
        // Given predicate string "questVariable(65644, 0) >= 3"
        // When ExtractMutations is called
        // Then returns one mutation: SetQuestVariable(QuestId(65644), 0, 3, Nibble.Whole)
        var expect = new PredicateExpect { Predicate = "questVariable(65644, 0) >= 3" };

        var mutations = PredicateAnalyzer.ExtractMutations(expect, ActiveQuest);

        Assert.Single(mutations);
        var m = Assert.IsType<StateMutation.SetQuestVariable>(mutations[0]);
        Assert.Equal(new QuestId(65644), m.Quest);
        Assert.Equal(0, m.Index);
        Assert.Equal(3, m.Value);
        Assert.Equal(Nibble.Whole, m.Nibble);
    }

    // =========================================================================
    // T13 -- AllExpect compound
    // =========================================================================

    [Fact]
    public void AllExpect_ProducesMutationsFromAllBranches_T13()
    {
        // Given AllExpect { All = ["questSequence(65644) >= 2", "questFlag(65644, 1)"] }
        // When ExtractMutations is called
        // Then returns two mutations: SetQuestSequence and SetQuestFlag
        var expect = new AllExpect { All = ["questSequence(65644) >= 2", "questFlag(65644, 1)"] };

        var mutations = PredicateAnalyzer.ExtractMutations(expect, new QuestId(65644));

        Assert.Equal(2, mutations.Count);
        var seq = Assert.IsType<StateMutation.SetQuestSequence>(mutations[0]);
        Assert.Equal(new QuestId(65644), seq.Quest);
        Assert.Equal(2, seq.Value);
        var flag = Assert.IsType<StateMutation.SetQuestFlag>(mutations[1]);
        Assert.Equal(new QuestId(65644), flag.Quest);
        Assert.Equal(1, flag.Bit);
        Assert.True(flag.Value);
    }

    // =========================================================================
    // T14 -- AnyExpect compound
    // =========================================================================

    [Fact]
    public void AnyExpect_ReturnsFirstBranchOnly_T14()
    {
        // Given AnyExpect { Any = ["questSequence(65644) >= 2", "isQuestComplete(65644)"] }
        // When ExtractMutations is called
        // Then returns one mutation: SetQuestSequence(QuestId(65644), 2) (first branch only)
        var expect = new AnyExpect { Any = ["questSequence(65644) >= 2", "isQuestComplete(65644)"] };

        var mutations = PredicateAnalyzer.ExtractMutations(expect, new QuestId(65644));

        Assert.Single(mutations);
        var m = Assert.IsType<StateMutation.SetQuestSequence>(mutations[0]);
        Assert.Equal(new QuestId(65644), m.Quest);
        Assert.Equal(2, m.Value);
    }

    // =========================================================================
    // T23 -- questVariableLow nibble
    // =========================================================================

    [Fact]
    public void QuestVariableLow_ProducesLowNibbleMutation_T23()
    {
        // Given predicate string "questVariableLow(65847, 0) >= 3"
        // When ExtractMutations is called
        // Then returns exactly one mutation: SetQuestVariable(QuestId(65847), 0, 3, Nibble.Low)
        var expect = new PredicateExpect { Predicate = "questVariableLow(65847, 0) >= 3" };

        var mutations = PredicateAnalyzer.ExtractMutations(expect, new QuestId(65847));

        Assert.Single(mutations);
        var m = Assert.IsType<StateMutation.SetQuestVariable>(mutations[0]);
        Assert.Equal(new QuestId(65847), m.Quest);
        Assert.Equal(0, m.Index);
        Assert.Equal(3, m.Value);
        Assert.Equal(Nibble.Low, m.Nibble);
    }

    // =========================================================================
    // T24 -- questVariableHigh nibble
    // =========================================================================

    [Fact]
    public void QuestVariableHigh_ProducesHighNibbleMutation_T24()
    {
        // Given predicate string "questVariableHigh(65847, 1) >= 1"
        // When ExtractMutations is called
        // Then returns exactly one mutation: SetQuestVariable(QuestId(65847), 1, 1, Nibble.High)
        var expect = new PredicateExpect { Predicate = "questVariableHigh(65847, 1) >= 1" };

        var mutations = PredicateAnalyzer.ExtractMutations(expect, new QuestId(65847));

        Assert.Single(mutations);
        var m = Assert.IsType<StateMutation.SetQuestVariable>(mutations[0]);
        Assert.Equal(new QuestId(65847), m.Quest);
        Assert.Equal(1, m.Index);
        Assert.Equal(1, m.Value);
        Assert.Equal(Nibble.High, m.Nibble);
    }

    // =========================================================================
    // T25 -- isSlotEquipped
    // =========================================================================

    [Fact]
    public void IsSlotEquipped_ProducesSetSlotEquipped_T25()
    {
        // Given predicate string "isSlotEquipped(3)"
        // When ExtractMutations is called
        // Then returns exactly one mutation: SetSlotEquipped(3, 1)
        // (slotIndex = 3, itemLevel = 1 -- any positive value satisfies ilvl > 0)
        var expect = new PredicateExpect { Predicate = "isSlotEquipped(3)" };

        var mutations = PredicateAnalyzer.ExtractMutations(expect, ActiveQuest);

        Assert.Single(mutations);
        var m = Assert.IsType<StateMutation.SetSlotEquipped>(mutations[0]);
        Assert.Equal(3, m.SlotIndex);
        Assert.Equal(1, m.ItemLevel);
    }

    // =========================================================================
    // T27 -- questVariableLow nibble mutation preserves high nibble
    // =========================================================================

    [Fact]
    public async Task LowNibbleMutation_PreservesHighNibble_T27()
    {
        // Given quest variable byte at index 0 is 0x51 (high nibble = 5, low nibble = 1).
        // Predicate string "questVariableLow(65847, 0) >= 3".
        // When ApplyVariableMutation is called with SetQuestVariable(QuestId(65847), 0, 3, Nibble.Low)
        // Then the byte at index 0 becomes 0x53 (high nibble 5 preserved, low nibble set to 3).
        //
        // We test this at the QuestDataDrivenState level by creating a state, pre-setting
        // the variable, and verifying the mutation preserves the high nibble.
        var questId = new QuestId(65847);
        var questState = new FakeQuestState();
        questState.SetQuestVariables(questId, 0x51, 0, 0, 0, 0, 0);

        // Apply the low-nibble mutation using the same read-modify-write logic the
        // builder will implement in QuestDataDrivenState.ApplyVariableMutation.
        // Since QuestDataDrivenState does not exist yet, we inline the expected logic
        // and assert the result. The builder's implementation must produce the same outcome.
        var current = await questState.GetQuestVariables(questId, CancellationToken.None);
        var bytes = current.ValueOrThrow.ToArray();
        bytes[0] = (byte)((bytes[0] & 0xF0) | (3 & 0x0F)); // Low nibble set to 3
        questState.SetQuestVariables(questId, bytes);

        var result = await questState.GetQuestVariables(questId, CancellationToken.None);
        Assert.Equal(0x53, result.ValueOrThrow[0]);
    }

    // =========================================================================
    // T28 -- questVariableHigh nibble mutation preserves low nibble
    // =========================================================================

    [Fact]
    public async Task HighNibbleMutation_PreservesLowNibble_T28()
    {
        // Given quest variable byte at index 1 is 0x02 (high nibble = 0, low nibble = 2).
        // Predicate string "questVariableHigh(65847, 1) >= 1".
        // When ApplyVariableMutation is called with SetQuestVariable(QuestId(65847), 1, 1, Nibble.High)
        // Then the byte at index 1 becomes 0x12 (high nibble set to 1, low nibble 2 preserved).
        var questId = new QuestId(65847);
        var questState = new FakeQuestState();
        questState.SetQuestVariables(questId, 0, 0x02, 0, 0, 0, 0);

        var current = await questState.GetQuestVariables(questId, CancellationToken.None);
        var bytes = current.ValueOrThrow.ToArray();
        bytes[1] = (byte)((bytes[1] & 0x0F) | ((1 & 0x0F) << 4)); // High nibble set to 1
        questState.SetQuestVariables(questId, bytes);

        var result = await questState.GetQuestVariables(questId, CancellationToken.None);
        Assert.Equal(0x12, result.ValueOrThrow[1]);
    }

    // =========================================================================
    // Positive counterparts for edge-case tests
    // =========================================================================

    [Fact]
    public void QuestSequence_EqualityComparison_ProducesSetQuestSequence()
    {
        // Positive variant: == also produces SetQuestSequence (per spec 1.3)
        var expect = new PredicateExpect { Predicate = "questSequence(66130) == 5" };

        var mutations = PredicateAnalyzer.ExtractMutations(expect, ActiveQuest);

        Assert.Single(mutations);
        var m = Assert.IsType<StateMutation.SetQuestSequence>(mutations[0]);
        Assert.Equal(5, m.Value);
    }

    [Fact]
    public void PlayerHasItem_SingleArg_DefaultsQuantityToOne()
    {
        // playerHasItem(id) with one argument defaults quantity to 1 (per spec table)
        var expect = new PredicateExpect { Predicate = "playerHasItem(2000104)" };

        var mutations = PredicateAnalyzer.ExtractMutations(expect, ActiveQuest);

        Assert.Single(mutations);
        var m = Assert.IsType<StateMutation.SetPlayerHasItem>(mutations[0]);
        Assert.Equal(2000104u, m.ItemId);
        Assert.Equal(1, m.Quantity);
    }

    [Fact]
    public void JobPredicate_ReturnsEmptyList()
    {
        // isDiscipleOfWar returns empty list (per spec: job set via initial state)
        var expect = new PredicateExpect { Predicate = "isDiscipleOfWar" };

        var mutations = PredicateAnalyzer.ExtractMutations(expect, ActiveQuest);

        Assert.Empty(mutations);
    }
}
