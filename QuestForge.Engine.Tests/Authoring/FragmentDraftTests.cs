using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for FragmentDraft step management (F1-F17) and
/// FragmentDraftManager (F31-F33) from FRAGMENT_AUTHORING_PLAN.md.
/// All tests will fail with NotImplementedException until Builder implements the stubs.
/// </summary>
public sealed class FragmentDraftTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(60);
    private static readonly DateTimeOffset T2 = T1.AddSeconds(60);
    private static readonly TimeSpan AutoSaveInterval = TimeSpan.FromSeconds(60);

    private static GameStateSnapshot MakeSnapshot(DateTimeOffset? at = null) => new(
        CapturedAt: at ?? T0,
        Zone: new ZoneId(128),
        Position: new WorldPosition(9.5f, 40f, 14.2f),
        ActiveQuest: null,
        QuestSequence: 0,
        QuestFlags: 0,
        QuestAccepted: false,
        QuestCompleted: false,
        LastNpcInteracted: null,
        LastNpcPosition: null,
        LastDialoguePrompt: null,
        LastDialogueAnswer: null,
        InventoryHash: 0,
        LastAttuned: null);

    private static readonly NpcLocation ValidNpcLoc =
        new(NpcId: 1000789, Zone: 128, Position: new Position3(9.5f, 40f, 14.2f));

    private static DraftStep MakeDraftStep(string stepId, Step? raw = null, int seqNum = 0) =>
        new(
            StepId: stepId,
            StepType: raw?.GetType().Name.Replace("Step", "").ToLowerInvariant() ?? "talk",
            SequenceNumber: seqNum,
            InferredFrom: InferredFrom.QuestSequenceChange,
            ObservedBefore: MakeSnapshot(),
            ObservedAfter: MakeSnapshot(T1),
            SuggestedExpect: null,
            Notes: "test",
            Raw: raw ?? new TalkStep { Id = stepId, Target = ValidNpcLoc });

    private static DraftStep MakeDraftStepNullRaw(string stepId) =>
        new(
            StepId: stepId,
            StepType: "talk",
            SequenceNumber: 0,
            InferredFrom: InferredFrom.QuestSequenceChange,
            ObservedBefore: MakeSnapshot(),
            ObservedAfter: MakeSnapshot(T1),
            SuggestedExpect: null,
            Notes: "unconfirmed",
            Raw: null);

    // =========================================================================
    // F1 -- AddStep appends in order
    // =========================================================================
    [Fact]
    public void F1_AddStep_AppendsInOrder()
    {
        // Given: a new FragmentDraft
        var draft = new FragmentDraft("travel/test-fragment", T0);

        // When: three steps added in order
        draft.AddStep(MakeDraftStep("step-a"), T1);
        draft.AddStep(MakeDraftStep("step-b"), T1);
        draft.AddStep(MakeDraftStep("step-c"), T2);

        // Then: Steps returns them in insertion order
        Assert.Equal(3, draft.Steps.Count);
        Assert.Equal("step-a", draft.Steps[0].StepId);
        Assert.Equal("step-b", draft.Steps[1].StepId);
        Assert.Equal("step-c", draft.Steps[2].StepId);
        // Then: LastModifiedAt reflects the timestamp of the last AddStep call
        Assert.Equal(T2, draft.LastModifiedAt);
    }

    // =========================================================================
    // F2 -- AddStep rejects duplicate StepId
    // =========================================================================
    [Fact]
    public void F2_AddStep_RejectsDuplicateStepId()
    {
        // Given: a FragmentDraft with one step "step-a"
        var draft = new FragmentDraft("travel/test-fragment", T0);
        draft.AddStep(MakeDraftStep("step-a"), T0);

        // When: AddStep called with another step whose StepId is "step-a"
        var ex = Assert.Throws<InvalidOperationException>(
            () => draft.AddStep(MakeDraftStep("step-a"), T1));

        // Then: exception message contains "step-a"
        Assert.Contains("step-a", ex.Message);
        // Then: Steps.Count remains 1
        Assert.Equal(1, draft.Steps.Count);
    }

    // =========================================================================
    // F3 -- RemoveStep removes by StepId
    // =========================================================================
    [Fact]
    public void F3_RemoveStep_RemovesByStepId()
    {
        // Given: a FragmentDraft with steps ["step-a", "step-b", "step-c"]
        var draft = new FragmentDraft("travel/test-fragment", T0);
        draft.AddStep(MakeDraftStep("step-a"), T0);
        draft.AddStep(MakeDraftStep("step-b"), T0);
        draft.AddStep(MakeDraftStep("step-c"), T0);

        // When: RemoveStep("step-b", now)
        var result = draft.RemoveStep("step-b", T1);

        // Then: returns true, Steps contains ["step-a", "step-c"], LastModifiedAt updated
        Assert.True(result);
        Assert.Equal(2, draft.Steps.Count);
        Assert.Equal("step-a", draft.Steps[0].StepId);
        Assert.Equal("step-c", draft.Steps[1].StepId);
        Assert.Equal(T1, draft.LastModifiedAt);
    }

    // =========================================================================
    // F4 -- RemoveStep returns false for nonexistent StepId
    // =========================================================================
    [Fact]
    public void F4_RemoveStep_ReturnsFalseForNonexistent()
    {
        // Given: a FragmentDraft with steps ["step-a"]
        var draft = new FragmentDraft("travel/test-fragment", T0);
        draft.AddStep(MakeDraftStep("step-a"), T0);
        var originalModified = draft.LastModifiedAt;

        // When: RemoveStep("step-x", now)
        var result = draft.RemoveStep("step-x", T1);

        // Then: returns false, Steps.Count remains 1, LastModifiedAt NOT updated
        Assert.False(result);
        Assert.Equal(1, draft.Steps.Count);
        Assert.Equal(originalModified, draft.LastModifiedAt);
    }

    // =========================================================================
    // F5 -- ReplaceStep swaps step at same position
    // =========================================================================
    [Fact]
    public void F5_ReplaceStep_SwapsAtSamePosition()
    {
        // Given: a FragmentDraft with steps ["step-a", "step-b", "step-c"]
        var draft = new FragmentDraft("travel/test-fragment", T0);
        draft.AddStep(MakeDraftStep("step-a"), T0);
        draft.AddStep(MakeDraftStep("step-b"), T0);
        draft.AddStep(MakeDraftStep("step-c"), T0);

        // When: ReplaceStep("step-b", newStep with StepId "step-b-v2", now)
        var newStep = MakeDraftStep("step-b-v2");
        var result = draft.ReplaceStep("step-b", newStep, T1);

        // Then: returns true, Steps[1].StepId is "step-b-v2", Steps.Count is still 3
        Assert.True(result);
        Assert.Equal("step-b-v2", draft.Steps[1].StepId);
        Assert.Equal(3, draft.Steps.Count);
    }

    // =========================================================================
    // F6 -- ReplaceStep returns false for nonexistent StepId
    // =========================================================================
    [Fact]
    public void F6_ReplaceStep_ReturnsFalseForNonexistent()
    {
        // Given: a FragmentDraft with steps ["step-a"]
        var draft = new FragmentDraft("travel/test-fragment", T0);
        draft.AddStep(MakeDraftStep("step-a"), T0);

        // When: ReplaceStep("step-x", newStep, now)
        var result = draft.ReplaceStep("step-x", MakeDraftStep("step-y"), T1);

        // Then: returns false
        Assert.False(result);
    }

    // =========================================================================
    // F7 -- GetStep returns step by StepId
    // =========================================================================
    [Fact]
    public void F7_GetStep_ReturnsByStepId()
    {
        // Given: a FragmentDraft with step "step-a"
        var draft = new FragmentDraft("travel/test-fragment", T0);
        var step = MakeDraftStep("step-a");
        draft.AddStep(step, T0);

        // When: GetStep("step-a")
        var found = draft.GetStep("step-a");

        // Then: returns the DraftStep with StepId "step-a"
        Assert.NotNull(found);
        Assert.Equal("step-a", found.StepId);
    }

    // =========================================================================
    // F8 -- GetStep returns null for nonexistent StepId
    // =========================================================================
    [Fact]
    public void F8_GetStep_ReturnsNullForNonexistent()
    {
        // Given: a FragmentDraft with step "step-a"
        var draft = new FragmentDraft("travel/test-fragment", T0);
        draft.AddStep(MakeDraftStep("step-a"), T0);

        // When: GetStep("step-x")
        var found = draft.GetStep("step-x");

        // Then: returns null
        Assert.Null(found);
    }

    // =========================================================================
    // F9 -- MoveStepUp swaps with previous
    // =========================================================================
    [Fact]
    public void F9_MoveStepUp_SwapsWithPrevious()
    {
        // Given: a FragmentDraft with steps ["step-a", "step-b", "step-c"]
        var draft = new FragmentDraft("travel/test-fragment", T0);
        draft.AddStep(MakeDraftStep("step-a"), T0);
        draft.AddStep(MakeDraftStep("step-b"), T0);
        draft.AddStep(MakeDraftStep("step-c"), T0);

        // When: MoveStepUp("step-b", now)
        var result = draft.MoveStepUp("step-b", T1);

        // Then: returns true, order is ["step-b", "step-a", "step-c"]
        Assert.True(result);
        Assert.Equal("step-b", draft.Steps[0].StepId);
        Assert.Equal("step-a", draft.Steps[1].StepId);
        Assert.Equal("step-c", draft.Steps[2].StepId);
    }

    // =========================================================================
    // F10 -- MoveStepUp at index 0 returns false
    // =========================================================================
    [Fact]
    public void F10_MoveStepUp_AtIndex0_ReturnsFalse()
    {
        // Given: a FragmentDraft with steps ["step-a", "step-b"]
        var draft = new FragmentDraft("travel/test-fragment", T0);
        draft.AddStep(MakeDraftStep("step-a"), T0);
        draft.AddStep(MakeDraftStep("step-b"), T0);

        // When: MoveStepUp("step-a", now)
        var result = draft.MoveStepUp("step-a", T1);

        // Then: returns false, order unchanged
        Assert.False(result);
        Assert.Equal("step-a", draft.Steps[0].StepId);
        Assert.Equal("step-b", draft.Steps[1].StepId);
    }

    // =========================================================================
    // F11 -- MoveStepDown swaps with next
    // =========================================================================
    [Fact]
    public void F11_MoveStepDown_SwapsWithNext()
    {
        // Given: a FragmentDraft with steps ["step-a", "step-b", "step-c"]
        var draft = new FragmentDraft("travel/test-fragment", T0);
        draft.AddStep(MakeDraftStep("step-a"), T0);
        draft.AddStep(MakeDraftStep("step-b"), T0);
        draft.AddStep(MakeDraftStep("step-c"), T0);

        // When: MoveStepDown("step-b", now)
        var result = draft.MoveStepDown("step-b", T1);

        // Then: returns true, order is ["step-a", "step-c", "step-b"]
        Assert.True(result);
        Assert.Equal("step-a", draft.Steps[0].StepId);
        Assert.Equal("step-c", draft.Steps[1].StepId);
        Assert.Equal("step-b", draft.Steps[2].StepId);
    }

    // =========================================================================
    // F12 -- MoveStepDown at last index returns false
    // =========================================================================
    [Fact]
    public void F12_MoveStepDown_AtLastIndex_ReturnsFalse()
    {
        // Given: a FragmentDraft with steps ["step-a", "step-b"]
        var draft = new FragmentDraft("travel/test-fragment", T0);
        draft.AddStep(MakeDraftStep("step-a"), T0);
        draft.AddStep(MakeDraftStep("step-b"), T0);

        // When: MoveStepDown("step-b", now)
        var result = draft.MoveStepDown("step-b", T1);

        // Then: returns false, order unchanged
        Assert.False(result);
        Assert.Equal("step-a", draft.Steps[0].StepId);
        Assert.Equal("step-b", draft.Steps[1].StepId);
    }

    // =========================================================================
    // F13 -- MoveStepUp with nonexistent StepId returns false
    // =========================================================================
    [Fact]
    public void F13_MoveStepUp_NonexistentStepId_ReturnsFalse()
    {
        // Given: a FragmentDraft with steps ["step-a"]
        var draft = new FragmentDraft("travel/test-fragment", T0);
        draft.AddStep(MakeDraftStep("step-a"), T0);

        // When: MoveStepUp("step-x", now)
        var result = draft.MoveStepUp("step-x", T1);

        // Then: returns false
        Assert.False(result);
    }

    // =========================================================================
    // F14 -- ToFragmentDefinition produces valid FragmentDefinition
    // =========================================================================
    [Fact]
    public void F14_ToFragmentDefinition_ProducesValidDefinition()
    {
        // Given: a FragmentDraft with fragmentId "travel/teleport-to-limsa" and two steps
        var teleportStep = new TeleportStep
        {
            Id = "teleport-limsa",
            AetheryteId = new QuestForge.Schema.AetheryteId(8),
            Expect = new PredicateExpect { Predicate = "playerZone() == 129" }
        };
        var travelStep = new TravelStep
        {
            Id = "walk-to-npc",
            Destination = new TravelDestination(Zone: 129),
            Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }
        };

        var draft = FragmentDraft.CreateForTest(
            "travel/teleport-to-limsa", T0,
            [
                MakeDraftStep("teleport-limsa", teleportStep),
                MakeDraftStep("walk-to-npc", travelStep)
            ]);

        // When: ToFragmentDefinition()
        var def = draft.ToFragmentDefinition();

        // Then: valid FragmentDefinition
        Assert.Equal("1.0.0", def.SchemaVersion);
        Assert.Equal("travel/teleport-to-limsa", def.FragmentId);
        Assert.Empty(def.Parameters);
        Assert.Equal(2, def.Steps.Length);
        Assert.IsType<TeleportStep>(def.Steps[0]);
        Assert.IsType<TravelStep>(def.Steps[1]);
    }

    // =========================================================================
    // F15 -- ToFragmentDefinition throws on null Raw
    // =========================================================================
    [Fact]
    public void F15_ToFragmentDefinition_ThrowsOnNullRaw()
    {
        // Given: a FragmentDraft (via CreateForTest) with one step whose Raw is null
        var draft = FragmentDraft.CreateForTest(
            "travel/test", T0,
            [MakeDraftStepNullRaw("step-null")]);

        // When/Then: throws DraftSerializationException with message containing the step's StepId
        var ex = Assert.Throws<DraftSerializationException>(() => draft.ToFragmentDefinition());
        Assert.Contains("step-null", ex.Message);
    }

    // =========================================================================
    // F16 -- ToFragmentDefinition with empty steps
    // =========================================================================
    [Fact]
    public void F16_ToFragmentDefinition_EmptySteps_ReturnsEmptyArray()
    {
        // Given: a FragmentDraft with zero steps
        var draft = new FragmentDraft("travel/empty", T0);

        // When: ToFragmentDefinition()
        var def = draft.ToFragmentDefinition();

        // Then: Steps = empty array (validator catches this, not ToFragmentDefinition)
        Assert.Empty(def.Steps);
        Assert.Equal("travel/empty", def.FragmentId);
    }

    // =========================================================================
    // F17 -- ToFragmentDefinition ignores SequenceNumber
    // =========================================================================
    [Fact]
    public void F17_ToFragmentDefinition_IgnoresSequenceNumber()
    {
        // Given: a FragmentDraft (via CreateForTest) with steps at SequenceNumber 0, 1, and 2
        var step0 = MakeDraftStep("step-seq0", seqNum: 0);
        var step1 = MakeDraftStep("step-seq1", seqNum: 1);
        var step2 = MakeDraftStep("step-seq2", seqNum: 2);

        var draft = FragmentDraft.CreateForTest(
            "travel/multi-seq", T0,
            [step0, step1, step2]);

        // When: ToFragmentDefinition()
        var def = draft.ToFragmentDefinition();

        // Then: Steps array contains all three in insertion order (not grouped by sequence)
        Assert.Equal(3, def.Steps.Length);
        Assert.Equal("step-seq0", def.Steps[0].Id);
        Assert.Equal("step-seq1", def.Steps[1].Id);
        Assert.Equal("step-seq2", def.Steps[2].Id);
    }

    // =========================================================================
    // F31 -- GetOrCreate returns new draft when storage is empty
    // =========================================================================
    [Fact]
    public async Task F31_GetOrCreate_ReturnsNewDraft_WhenStorageEmpty()
    {
        // Given: a FragmentDraftManager with InMemory storage that returns null
        var storage = new FakeFragmentDraftStorage();
        var clock = new FakeClock(T0);
        var manager = new FragmentDraftManager(storage, clock, AutoSaveInterval);

        // When: GetOrCreate("travel/test", ct)
        var draft = await manager.GetOrCreate("travel/test", CancellationToken.None);

        // Then: returns a FragmentDraft with FragmentId "travel/test" and zero steps
        Assert.NotNull(draft);
        Assert.Equal("travel/test", draft.FragmentId);
        Assert.Equal(0, draft.Steps.Count);
        Assert.Equal(0, storage.SaveCount); // not auto-persisted
    }

    // =========================================================================
    // F32 -- GetOrCreate caches and returns same instance
    // =========================================================================
    [Fact]
    public async Task F32_GetOrCreate_CachesAndReturnsSameInstance()
    {
        // Given: a FragmentDraftManager
        var storage = new FakeFragmentDraftStorage();
        var clock = new FakeClock(T0);
        var manager = new FragmentDraftManager(storage, clock, AutoSaveInterval);

        // When: GetOrCreate("travel/test", ct) called twice
        var draft1 = await manager.GetOrCreate("travel/test", CancellationToken.None);
        var draft2 = await manager.GetOrCreate("travel/test", CancellationToken.None);

        // Then: both calls return the same object reference
        Assert.Same(draft1, draft2);
    }

    // =========================================================================
    // F33 -- SaveNow persists to storage
    // =========================================================================
    [Fact]
    public async Task F33_SaveNow_PersistsToStorage()
    {
        // Given: a FragmentDraftManager with a recording storage
        var storage = new FakeFragmentDraftStorage();
        var clock = new FakeClock(T0);
        var manager = new FragmentDraftManager(storage, clock, AutoSaveInterval);

        // When: GetOrCreate, AddStep, MarkDirty, then SaveNow
        var draft = await manager.GetOrCreate("travel/test", CancellationToken.None);
        draft.AddStep(MakeDraftStep("step-a"), T0);
        manager.MarkDirty("travel/test");
        await manager.SaveNow("travel/test", CancellationToken.None);

        // Then: storage.Save was called with the fragmentId and draft
        Assert.True(storage.SaveCount > 0);
        Assert.Equal("travel/test", storage.LastSavedFragmentId);
    }
}
