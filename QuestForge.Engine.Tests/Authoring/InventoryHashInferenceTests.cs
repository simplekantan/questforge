using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for StepInferenceEngine Rule 2.6 — inventory hash diff inference.
/// All 16 tests will fail until Builder implements Rule 2.6 in StepInferenceEngine.
///
/// Rule 2.6 is positioned after Rule 2.4 (KeyItemsRemoved) and before Rule 2.5 (Attunement).
/// It fires when before.InventoryHash != after.InventoryHash and before.KeyItems is non-null.
///
/// Step type follows delta direction:
///   gain-only  → "pickup-item",   InferredFrom=InventoryChange
///   loss-only  → "hand-over-item", InferredFrom=InventoryChange
///   gain + loss → "talk",          InferredFrom=InventoryChange
/// </summary>
public sealed class InventoryHashInferenceTests
{
    private static readonly QuestId ActiveQuest = new(2054);

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(30);

    /// <summary>
    /// Builds a GameStateSnapshot with explicit InventoryHash and KeyItems.
    /// All other fields are defaults that do not trigger other inference rules.
    /// </summary>
    private static GameStateSnapshot MakeSnapshot(
        uint inventoryHash = 0,
        IReadOnlyDictionary<uint, int>? keyItems = null,
        bool questAccepted = false,
        bool questCompleted = false,
        AetheryteId? lastAttuned = null,
        int questSequence = 0,
        IReadOnlyList<uint>? keyItemsAdded = null,
        IReadOnlyList<uint>? keyItemsRemoved = null,
        DateTimeOffset? capturedAt = null) =>
        new(
            CapturedAt: capturedAt ?? T0,
            Zone: new ZoneId(100),
            Position: new WorldPosition(0, 0, 0),
            ActiveQuest: ActiveQuest,
            QuestSequence: questSequence,
            QuestFlags: 0,
            QuestAccepted: questAccepted,
            QuestCompleted: questCompleted,
            LastNpcInteracted: null,
            LastNpcPosition: null,
            LastDialoguePrompt: null,
            LastDialogueAnswer: null,
            InventoryHash: inventoryHash,
            LastAttuned: lastAttuned)
        {
            KeyItems       = keyItems,
            KeyItemsAdded  = keyItemsAdded,
            KeyItemsRemoved = keyItemsRemoved,
        };

    // =========================================================================
    // Happy path — gain-only
    // =========================================================================

    [Fact]
    public void WhenHashDiffersAndGainOnly_StepTypeIsPickupItem()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.6 in StepInferenceEngine.Infer.
         *
         * CONTRACT: Given before.InventoryHash=1, before.KeyItems={},
         *                 after.InventoryHash=2,  after.KeyItems={ 2000123 → 1 },
         *           When Infer is called,
         *           Then StepType="pickup-item",
         *                SuggestedStepId="pickup-item-2000123",
         *                SuggestedExpect="playerHasItem(2000123)",
         *                Confidence=Medium,
         *                InferredFrom=InventoryChange,
         *                Notes=null.
         *
         * BUILDER GUIDANCE: Rule 2.6 fires after Rule 2.4 (KeyItemsRemoved) and before
         *   Rule 2.5 (Attunement). Compare before.KeyItems and after.KeyItems to determine
         *   the delta direction. Gain-only → "pickup-item".
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            inventoryHash: 1u,
            keyItems: new Dictionary<uint, int>());
        var after = MakeSnapshot(
            inventoryHash: 2u,
            keyItems: new Dictionary<uint, int> { [2000123u] = 1 },
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("pickup-item", result.StepType);
        Assert.Equal("pickup-item-2000123", result.SuggestedStepId);
        Assert.Equal("playerHasItem(2000123)", result.SuggestedExpect);
        Assert.Equal(Confidence.Medium, result.Confidence);
        Assert.Equal(InferredFrom.InventoryChange, result.InferredFrom);
        Assert.Null(result.Notes);
    }

    // =========================================================================
    // Happy path — loss-only
    // =========================================================================

    [Fact]
    public void WhenHashDiffersAndLossOnly_StepTypeIsHandOverItem()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.6.
         *
         * CONTRACT: Given before.KeyItems={ 2000456 → 1 }, after.KeyItems={} (item removed),
         *           When Infer is called,
         *           Then StepType="hand-over-item",
         *                SuggestedStepId="hand-over-item-2000456",
         *                SuggestedExpect="not(playerHasItem(2000456))".
         *
         * BUILDER GUIDANCE: Loss-only delta → "hand-over-item". Use first lost item's id for
         *   SuggestedStepId. SuggestedExpect = $"not(playerHasItem({itemId}))".
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            inventoryHash: 5u,
            keyItems: new Dictionary<uint, int> { [2000456u] = 1 });
        var after = MakeSnapshot(
            inventoryHash: 6u,
            keyItems: new Dictionary<uint, int>(),
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("hand-over-item", result.StepType);
        Assert.Equal("hand-over-item-2000456", result.SuggestedStepId);
        Assert.Equal("not(playerHasItem(2000456))", result.SuggestedExpect);
        Assert.Equal(Confidence.Medium, result.Confidence);
        Assert.Equal(InferredFrom.InventoryChange, result.InferredFrom);
    }

    // =========================================================================
    // Happy path — simultaneous gain and loss → talk
    // =========================================================================

    [Fact]
    public void WhenSimultaneousGainAndLoss_StepTypeIsTalk()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.6.
         *
         * CONTRACT: Given before.KeyItems={ 2000456 → 1 },
         *                 after.KeyItems={ 2000123 → 1 } (item exchanged),
         *           When Infer is called,
         *           Then StepType="talk",
         *                SuggestedStepId="exchange-2000456-for-2000123",
         *                SuggestedExpect="playerHasItem(2000123) and not(playerHasItem(2000456))".
         *
         * BUILDER GUIDANCE: Both Gained and Lost are non-empty → "talk".
         *   SuggestedStepId format: "exchange-{firstLostId}-for-{firstGainedId}".
         *   SuggestedExpect: combine both predicates with " and ".
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            inventoryHash: 10u,
            keyItems: new Dictionary<uint, int> { [2000456u] = 1 });
        var after = MakeSnapshot(
            inventoryHash: 11u,
            keyItems: new Dictionary<uint, int> { [2000123u] = 1 },
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("talk", result.StepType);
        Assert.Equal("exchange-2000456-for-2000123", result.SuggestedStepId);
        Assert.Equal("playerHasItem(2000123) and not(playerHasItem(2000456))", result.SuggestedExpect);
        Assert.Equal(InferredFrom.InventoryChange, result.InferredFrom);
    }

    // =========================================================================
    // Happy path — multiple gains → Notes contains all ids
    // =========================================================================

    [Fact]
    public void WhenMultipleGains_NotesContainsAllIds()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.6.
         *
         * CONTRACT: Given after.KeyItems has two gained items (2000100 and 2000101),
         *           When Infer is called,
         *           Then Notes is non-null and contains both "2000100" and "2000101".
         *
         * BUILDER GUIDANCE: When the Gained list has > 1 entry, set Notes to
         *   a string mentioning all gained item ids (same pattern as Rule 2.3).
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            inventoryHash: 20u,
            keyItems: new Dictionary<uint, int>());
        var after = MakeSnapshot(
            inventoryHash: 21u,
            keyItems: new Dictionary<uint, int> { [2000100u] = 1, [2000101u] = 1 },
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.NotNull(result.Notes);
        Assert.Contains("2000100", result.Notes, StringComparison.Ordinal);
        Assert.Contains("2000101", result.Notes, StringComparison.Ordinal);
    }

    // =========================================================================
    // Happy path — quantity decrease treated as loss
    // =========================================================================

    [Fact]
    public void WhenQuantityDecreases_TreatedAsLoss()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.6.
         *
         * CONTRACT: Given before.KeyItems={ 2000100 → 3 }, after.KeyItems={ 2000100 → 1 }
         *           (quantity dropped from 3 to 1, net loss of 2),
         *           When Infer is called,
         *           Then StepType="hand-over-item" (loss-only).
         *
         * BUILDER GUIDANCE: A quantity decrease for an existing id is a loss.
         *   Diff via KeyItemPollDiff or equivalent logic. No gain entry is produced.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            inventoryHash: 30u,
            keyItems: new Dictionary<uint, int> { [2000100u] = 3 });
        var after = MakeSnapshot(
            inventoryHash: 31u,
            keyItems: new Dictionary<uint, int> { [2000100u] = 1 },
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("hand-over-item", result.StepType);
        Assert.Equal(InferredFrom.InventoryChange, result.InferredFrom);
    }

    // =========================================================================
    // Happy path — quantity increase treated as gain
    // =========================================================================

    [Fact]
    public void WhenQuantityIncreases_TreatedAsGain()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.6.
         *
         * CONTRACT: Given before.KeyItems={ 2000100 → 1 }, after.KeyItems={ 2000100 → 4 }
         *           (quantity increased from 1 to 4),
         *           When Infer is called,
         *           Then StepType="pickup-item" (gain-only).
         *
         * BUILDER GUIDANCE: A quantity increase for an existing id is a gain.
         *   The gained qty = newQty - oldQty = 3. No loss entry is produced.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            inventoryHash: 40u,
            keyItems: new Dictionary<uint, int> { [2000100u] = 1 });
        var after = MakeSnapshot(
            inventoryHash: 41u,
            keyItems: new Dictionary<uint, int> { [2000100u] = 4 },
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("pickup-item", result.StepType);
        Assert.Equal(InferredFrom.InventoryChange, result.InferredFrom);
    }

    // =========================================================================
    // Negative — hash equal, rule must not fire
    // =========================================================================

    [Fact]
    public void NotFired_WhenHashEqual()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.6.
         *
         * CONTRACT: Given before.InventoryHash == after.InventoryHash (same hash),
         *           When Infer is called,
         *           Then Rule 2.6 does NOT fire. Falls through to Rule 8 (Empty).
         *
         * BUILDER GUIDANCE: The hash comparison is the cheap short-circuit guard.
         *   If hashes are equal, no full key-item diff is needed.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var map = new Dictionary<uint, int> { [2000100u] = 1 };
        var before = MakeSnapshot(inventoryHash: 99u, keyItems: map);
        var after  = MakeSnapshot(inventoryHash: 99u, keyItems: map, capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.NotEqual("pickup-item",   result.StepType);
        Assert.NotEqual("hand-over-item", result.StepType);
        Assert.Equal("", result.StepType); // Rule 9 (Empty)
    }

    // =========================================================================
    // Negative — before hash is zero (uninitialized), rule must not fire
    // =========================================================================

    [Fact]
    public void NotFired_WhenBeforeHashIsZero()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.6.
         *
         * CONTRACT: Given before.InventoryHash == 0 (uninitialized / no poll yet),
         *           When Infer is called (after.InventoryHash != 0),
         *           Then Rule 2.6 does NOT fire.
         *
         * BUILDER GUIDANCE: Hash=0 means no initial poll has completed. Firing with
         *   hash=0 as baseline would produce spurious "gained all items" events on
         *   the very first poll. Guard: if (before.InventoryHash == 0) skip Rule 2.6.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            inventoryHash: 0u,
            keyItems: new Dictionary<uint, int>());
        var after = MakeSnapshot(
            inventoryHash: 99u,
            keyItems: new Dictionary<uint, int> { [2000100u] = 1 },
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert — Rule 2.6 must NOT fire when before hash is zero
        Assert.NotEqual("pickup-item",   result.StepType);
        Assert.NotEqual("hand-over-item", result.StepType);
    }

    // =========================================================================
    // Negative — before.KeyItems is null (production mode), rule must not fire
    // =========================================================================

    [Fact]
    public void NotFired_WhenBeforeKeyItemsIsNull()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.6.
         *
         * CONTRACT: Given before.KeyItems == null (production / hash-only mode),
         *           When Infer is called with a differing hash,
         *           Then Rule 2.6 does NOT fire (no full map → no diff possible).
         *
         * BUILDER GUIDANCE: Rule 2.6 requires before.KeyItems != null to diff against.
         *   Guard: if (before.KeyItems is null) skip Rule 2.6.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(inventoryHash: 1u, keyItems: null);  // null = production mode
        var after  = MakeSnapshot(
            inventoryHash: 2u,
            keyItems: new Dictionary<uint, int> { [2000100u] = 1 },
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.NotEqual("pickup-item",   result.StepType);
        Assert.NotEqual("hand-over-item", result.StepType);
    }

    // =========================================================================
    // Negative — both KeyItems null, rule must not fire
    // =========================================================================

    [Fact]
    public void NotFired_WhenBothKeyItemsNull()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.6.
         *
         * CONTRACT: Given both before.KeyItems == null and after.KeyItems == null,
         *           When Infer is called with differing hashes,
         *           Then Rule 2.6 does NOT fire.
         *
         * BUILDER GUIDANCE: Without either map, diff is impossible. The rule must be
         *   skipped entirely in production mode even if hashes differ.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(inventoryHash: 1u, keyItems: null);
        var after  = MakeSnapshot(inventoryHash: 2u, keyItems: null, capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.NotEqual("pickup-item",   result.StepType);
        Assert.NotEqual("hand-over-item", result.StepType);
        Assert.NotEqual("talk",           result.StepType);
    }

    // =========================================================================
    // Priority — Rule 1 (QuestCompleted) beats Rule 2.6
    // =========================================================================

    [Fact]
    public void Priority_QuestCompleted_Beats_InventoryHashDiff()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.6 in the correct priority position.
         *
         * CONTRACT: Given after.QuestCompleted=true AND differing InventoryHash with valid KeyItems,
         *           When Infer is called,
         *           Then StepType="turn-in" (Rule 1 fires, not Rule 2.6).
         *
         * BUILDER GUIDANCE: Rule 2.6 is inserted AFTER Rule 2.4 (KeyItemsRemoved) and BEFORE
         *   Rule 2.5 (Attunement). Rule 1 (QuestCompleted) is always evaluated first.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            questCompleted: false,
            inventoryHash: 1u,
            keyItems: new Dictionary<uint, int>());
        var after = MakeSnapshot(
            questCompleted: true,
            inventoryHash: 2u,
            keyItems: new Dictionary<uint, int> { [2000100u] = 1 },
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("turn-in", result.StepType);
    }

    // =========================================================================
    // Priority — Rule 2 (QuestAccepted) beats Rule 2.6
    // =========================================================================

    [Fact]
    public void Priority_QuestAccepted_Beats_InventoryHashDiff()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.6 in the correct priority position.
         *
         * CONTRACT: Given after.QuestAccepted=true AND differing InventoryHash,
         *           When Infer is called,
         *           Then StepType="accept".
         *
         * BUILDER GUIDANCE: Rule 2 (QuestAccepted) is evaluated before Rule 2.6.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            questAccepted: false,
            inventoryHash: 1u,
            keyItems: new Dictionary<uint, int>());
        var after = MakeSnapshot(
            questAccepted: true,
            inventoryHash: 2u,
            keyItems: new Dictionary<uint, int> { [2000100u] = 1 },
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("accept", result.StepType);
    }

    // =========================================================================
    // Priority — Rule 2.3 (KeyItemsAdded) beats Rule 2.6
    // =========================================================================

    [Fact]
    public void Priority_KeyItemsAdded_Beats_InventoryHashDiff()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.6 in the correct priority position.
         *
         * CONTRACT: Given after.KeyItemsAdded=[2000100] AND differing InventoryHash,
         *           When Infer is called,
         *           Then StepType="pickup-item" with InferredFrom=DialogueInteraction
         *           (Rule 2.3 fires, not Rule 2.6).
         *
         * BUILDER GUIDANCE: Rule 2.3 (KeyItemsAdded) is evaluated before Rule 2.6.
         *   The two rules produce the same StepType but differ in InferredFrom:
         *   Rule 2.3 → DialogueInteraction, Rule 2.6 → InventoryChange.
         *   The InferredFrom value is the distinguishing assertion.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            inventoryHash: 1u,
            keyItems: new Dictionary<uint, int>());
        var after = MakeSnapshot(
            inventoryHash: 2u,
            keyItems: new Dictionary<uint, int> { [2000100u] = 1 },
            keyItemsAdded: new List<uint> { 2000100u },   // Rule 2.3 trigger
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("pickup-item", result.StepType);
        Assert.Equal(InferredFrom.DialogueInteraction, result.InferredFrom);
    }

    // =========================================================================
    // Priority — Rule 2.4 (KeyItemsRemoved) beats Rule 2.6
    // =========================================================================

    [Fact]
    public void Priority_KeyItemsRemoved_Beats_InventoryHashDiff()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.6 in the correct priority position.
         *
         * CONTRACT: Given after.KeyItemsRemoved=[2000456] AND differing InventoryHash,
         *           When Infer is called,
         *           Then StepType="hand-over-item" with InferredFrom=DialogueInteraction
         *           (Rule 2.4 fires, not Rule 2.6).
         *
         * BUILDER GUIDANCE: Rule 2.4 (KeyItemsRemoved) is evaluated immediately before Rule 2.6.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            inventoryHash: 5u,
            keyItems: new Dictionary<uint, int> { [2000456u] = 1 });
        var after = MakeSnapshot(
            inventoryHash: 6u,
            keyItems: new Dictionary<uint, int>(),
            keyItemsRemoved: new List<uint> { 2000456u },  // Rule 2.4 trigger
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("hand-over-item", result.StepType);
        Assert.Equal(InferredFrom.DialogueInteraction, result.InferredFrom);
    }

    // =========================================================================
    // Priority — Rule 2.5 (Attunement) beats Rule 2.6
    // =========================================================================

    [Fact]
    public void Priority_Attunement_Beats_InventoryHashDiff()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.6 in the correct priority position.
         *
         * CONTRACT: Given after.LastAttuned changed AND differing InventoryHash,
         *           When Infer is called,
         *           Then StepType="attune" (Rule 2.5 fires, not Rule 2.6).
         *
         * BUILDER GUIDANCE: Rule 2.5 (Attunement) is evaluated BEFORE Rule 2.6 in the cascade.
         *   Insert Rule 2.6 AFTER the Rule 2.5 block, not before it.
         */

        // Arrange — use LastAethernetShardInteracted + matching LastNpcInteracted (new Rule 2.5 trigger)
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            inventoryHash: 1u,
            keyItems: new Dictionary<uint, int>());
        var after = MakeSnapshot(
            inventoryHash: 2u,
            keyItems: new Dictionary<uint, int> { [2000100u] = 1 },
            capturedAt: T1) with
        {
            LastAethernetShardInteracted = new AetheryteId(1000),
            LastNpcInteracted = new NpcId(1000),
        };

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("attune", result.StepType);
        Assert.Equal(InferredFrom.AttunementChange, result.InferredFrom);
    }

    // =========================================================================
    // Priority — Rule 2.6 beats Rule 3 (QuestSequenceChange)
    // =========================================================================

    [Fact]
    public void Priority_InventoryHashDiff_Beats_QuestSequenceChange()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.6 in the correct priority position.
         *
         * CONTRACT: Given differing InventoryHash with valid KeyItems AND a quest sequence
         *           advance (before.QuestSequence=0, after.QuestSequence=1),
         *           When Infer is called,
         *           Then StepType is "pickup-item" or "hand-over-item"
         *                with InferredFrom=InventoryChange
         *           (Rule 2.6 fires BEFORE Rule 3).
         *
         * BUILDER GUIDANCE: Rule 2.6 is inserted between Rule 2.5 and Rule 3 in the cascade.
         *   It is more specific (we know exactly what changed in the inventory) than a generic
         *   quest sequence advance.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            questSequence: 0,
            inventoryHash: 1u,
            keyItems: new Dictionary<uint, int>());
        var after = MakeSnapshot(
            questSequence: 1,     // Rule 3 trigger
            inventoryHash: 2u,    // Rule 2.6 trigger
            keyItems: new Dictionary<uint, int> { [2000100u] = 1 },
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert — Rule 2.6 must win over Rule 3
        Assert.Equal(InferredFrom.InventoryChange, result.InferredFrom);
        Assert.True(
            result.StepType is "pickup-item" or "hand-over-item",
            $"Expected 'pickup-item' or 'hand-over-item', got '{result.StepType}'");
    }
}
