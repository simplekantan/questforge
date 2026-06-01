using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

// WHY: QuestForge.Schema is NOT imported globally to avoid ambiguity between Schema types and
// Adapters.Types on common names (AetheryteId). Schema types are referenced with full qualification.

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for InteractObjectStep authoring inference -- covers three areas:
///   OI-I1..OI-I11 : StepInferenceEngine Rule 3.5o -- ObjectInteracted (priority pinning)
///   OI-A1..OI-A4  : SnapshotAggregator.OnObjectInteracted / OnObjectInteractedConsumed
///   OI-F1..OI-F3  : StepFactory.Build "interact-object" / "pickup-item" arms
///
/// ALL tests in this file will fail to compile until Builder adds:
///   - ObjectInteractedSignal record in GameStateSnapshot.cs                  (Task 1)
///   - GameStateSnapshot.ObjectInteracted property                            (Task 1)
///   - InferredFrom.ObjectInteracted enum value                               (Task 3)
///   - SnapshotAggregator.OnObjectInteracted / OnObjectInteractedConsumed     (Task 2)
///   - StepInferenceEngine Rule 3.5o (ObjectInteracted check)                 (Task 4)
///   - StepFactory.Build "interact-object" arm fix to use signal              (Task 5)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~InteractObjectInferenceTests"
/// </summary>
public sealed class InteractObjectInferenceTests
{
    private static readonly QuestId        ActiveQuest = new(2054);
    private static readonly DateTimeOffset T0          = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // =========================================================================
    // Snapshot builder helper (mirrors UseItemInferenceTests.MakeSnapshot)
    // =========================================================================

    private static GameStateSnapshot MakeSnapshot(
        ZoneId?    zone           = null,
        bool       questAccepted  = false,
        bool       questCompleted = false,
        int        questSequence  = 0) =>
        new(
            CapturedAt:          T0,
            Zone:                zone ?? new ZoneId(134),
            Position:            new WorldPosition(0, 0, 0),
            ActiveQuest:         ActiveQuest,
            QuestSequence:       questSequence,
            QuestFlags:          0,
            QuestAccepted:       questAccepted,
            QuestCompleted:      questCompleted,
            LastNpcInteracted:   null,
            LastNpcPosition:     null,
            LastDialoguePrompt:  null,
            LastDialogueAnswer:  null,
            InventoryHash:       0,
            LastAttuned:         null);

    // =========================================================================
    // OI-I1 -- Happy path: ObjectInteracted fires rule 3.5o -> infers "interact-object"
    // =========================================================================

    [Fact]
    public void ObjectInteracted_HappyPath_InfersInteractObjectStep_OI_I1()
    {
        // CONTRACT: Given ObjectInteracted = new(2001500, 10f, 5f, 20f) in after,
        //           no zone change and no quest changes,
        //           When Infer is called,
        //           Then StepType="interact-object", SuggestedStepId="interact-object-2001500",
        //                SuggestedExpect=null, Confidence=High, InferredFrom=ObjectInteracted.
        //                Notes contains "Author MUST write the Expect predicate".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            ObjectInteracted = new ObjectInteractedSignal(2001500u, 10f, 5f, 20f)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("interact-object",                     result.StepType);
        Assert.Equal("interact-object-2001500",             result.SuggestedStepId);
        Assert.Null(result.SuggestedExpect);
        Assert.Equal(Confidence.High,                       result.Confidence);
        Assert.Equal(InferredFrom.ObjectInteracted,         result.InferredFrom);
        Assert.NotNull(result.Notes);
        Assert.Contains("Author MUST write the Expect predicate", result.Notes, StringComparison.Ordinal);
    }

    // =========================================================================
    // OI-I2 -- Priority BELOW Rule 1 (QuestCompleted): turn-in wins
    // =========================================================================

    [Fact]
    public void ObjectInteracted_LosesToQuestCompleted_Rule1Wins_OI_I2()
    {
        // CONTRACT: Given QuestCompleted=true AND ObjectInteracted set in same window,
        //           When Infer is called,
        //           Then StepType="turn-in" (Rule 1), NOT "interact-object".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questCompleted: false);
        var after  = before with
        {
            QuestCompleted = true,
            ObjectInteracted = new ObjectInteractedSignal(2001500u, 10f, 5f, 20f)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("turn-in",                     result.StepType);
        Assert.Equal(InferredFrom.QuestCompleted,   result.InferredFrom);
        Assert.NotEqual("interact-object",          result.StepType);
    }

    // =========================================================================
    // OI-I3 -- Priority BELOW Rule 2 (QuestAccepted): accept wins
    // =========================================================================

    [Fact]
    public void ObjectInteracted_LosesToQuestAccepted_Rule2Wins_OI_I3()
    {
        // CONTRACT: Given QuestAccepted=true AND ObjectInteracted set in same window,
        //           When Infer is called,
        //           Then StepType="accept" (Rule 2), NOT "interact-object".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questAccepted: false);
        var after  = before with
        {
            QuestAccepted = true,
            ObjectInteracted = new ObjectInteractedSignal(2001500u, 10f, 5f, 20f)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("accept",                      result.StepType);
        Assert.Equal(InferredFrom.QuestAccepted,    result.InferredFrom);
        Assert.NotEqual("interact-object",          result.StepType);
    }

    // =========================================================================
    // OI-I4 -- Priority BELOW Rule 3.5i (ItemUsed): use-item wins
    // =========================================================================

    [Fact]
    public void ObjectInteracted_LosesToItemUsed_Rule35iWins_OI_I4()
    {
        // CONTRACT: Given both ObjectInteracted AND ItemUsed set in same window,
        //           When Infer is called,
        //           Then StepType="use-item" (Rule 3.5i), NOT "interact-object".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            ObjectInteracted = new ObjectInteractedSignal(2001500u, 10f, 5f, 20f),
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       1234u,
                TargetBaseId: null,
                TargetPosition: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-item",                result.StepType);
        Assert.Equal(InferredFrom.ItemUsed,     result.InferredFrom);
        Assert.NotEqual("interact-object",      result.StepType);
    }

    // =========================================================================
    // OI-I5 -- Priority BELOW Rule 3.5j (JobChanged): change-job wins
    // =========================================================================

    [Fact]
    public void ObjectInteracted_LosesToJobChanged_Rule35jWins_OI_I5()
    {
        // CONTRACT: Given both ObjectInteracted AND JobChanged set in same window,
        //           When Infer is called,
        //           Then StepType="change-job" (Rule 3.5j), NOT "interact-object".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            ObjectInteracted = new ObjectInteractedSignal(2001500u, 10f, 5f, 20f),
            JobChanged = new JobChangedSignal(1, 22)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("change-job",                  result.StepType);
        Assert.Equal(InferredFrom.JobChanged,       result.InferredFrom);
        Assert.NotEqual("interact-object",          result.StepType);
    }

    // =========================================================================
    // OI-I6 -- Priority BELOW Rule 3.5k (GearsetRegistered): register-gearset wins
    // =========================================================================

    [Fact]
    public void ObjectInteracted_LosesToGearsetRegistered_Rule35kWins_OI_I6()
    {
        // CONTRACT: Given both ObjectInteracted AND GearsetRegistered set in same window,
        //           When Infer is called,
        //           Then StepType="register-gearset" (Rule 3.5k), NOT "interact-object".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            ObjectInteracted = new ObjectInteractedSignal(2001500u, 10f, 5f, 20f),
            GearsetRegistered = new GearsetRegisteredSignal(3, 4)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("register-gearset",                result.StepType);
        Assert.Equal(InferredFrom.GearsetRegistered,    result.InferredFrom);
        Assert.NotEqual("interact-object",              result.StepType);
    }

    // =========================================================================
    // OI-I7 -- Priority ABOVE Rule 3.5g (EquipmentChanged): interact-object wins
    // =========================================================================

    [Fact]
    public void ObjectInteracted_BeatsEquipmentChanged_Rule35oOverRule35g_OI_I7()
    {
        // CONTRACT: Given both ObjectInteracted AND EquipmentChanged set in same window,
        //           When Infer is called,
        //           Then StepType="interact-object" (Rule 3.5o > 3.5g).

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            ObjectInteracted = new ObjectInteractedSignal(2001500u, 10f, 5f, 20f),
            EquipmentChanged = new EquipmentChangedSignal([12345u])
        };

        var result = engine.Infer(before, after);

        Assert.Equal("interact-object",                 result.StepType);
        Assert.Equal(InferredFrom.ObjectInteracted,     result.InferredFrom);
        Assert.NotEqual("equip-gear-for-quest",         result.StepType);
    }

    // =========================================================================
    // OI-I8 -- Priority ABOVE Rule 3.5e (EmoteCompleted): interact-object wins
    // =========================================================================

    [Fact]
    public void ObjectInteracted_BeatsEmoteCompleted_Rule35oOverRule35e_OI_I8()
    {
        // CONTRACT: Given both ObjectInteracted AND EmoteCompleted set in same window,
        //           When Infer is called,
        //           Then StepType="interact-object" (Rule 3.5o > 3.5e).

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            ObjectInteracted = new ObjectInteractedSignal(2001500u, 10f, 5f, 20f),
            EmoteCompleted = new EmoteCompletedSignal(50u, null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("interact-object",                 result.StepType);
        Assert.Equal(InferredFrom.ObjectInteracted,     result.InferredFrom);
        Assert.NotEqual("use-emote",                    result.StepType);
    }

    // =========================================================================
    // OI-I9 -- Priority ABOVE Rule 3.5 (ActionCompleted): interact-object wins
    // =========================================================================

    [Fact]
    public void ObjectInteracted_BeatsActionCompleted_Rule35oOverRule35_OI_I9()
    {
        // CONTRACT: Given both ObjectInteracted AND ActionCompleted set in same window,
        //           When Infer is called,
        //           Then StepType="interact-object" (Rule 3.5o > 3.5).

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            ObjectInteracted = new ObjectInteractedSignal(2001500u, 10f, 5f, 20f),
            ActionCompleted = new ActionCompletedSignal(
                QuestForge.Schema.ActionType.Action, 7u, null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("interact-object",                 result.StepType);
        Assert.Equal(InferredFrom.ObjectInteracted,     result.InferredFrom);
        Assert.NotEqual("use-action",                   result.StepType);
    }

    // =========================================================================
    // OI-I10 -- Priority ABOVE Rule 3 (QuestSequence advance): interact-object wins
    // =========================================================================

    [Fact]
    public void ObjectInteracted_BeatsQuestSequenceAdvance_Rule35oOverRule3_OI_I10()
    {
        // CONTRACT: Given ObjectInteracted set AND QuestSequence advances (1->2),
        //           When Infer is called,
        //           Then StepType="interact-object" (Rule 3.5o > Rule 3 "talk").

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 1);
        var after  = before with
        {
            QuestSequence = 2,
            ObjectInteracted = new ObjectInteractedSignal(2001500u, 10f, 5f, 20f)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("interact-object",                 result.StepType);
        Assert.Equal(InferredFrom.ObjectInteracted,     result.InferredFrom);
        Assert.NotEqual("talk",                         result.StepType);
    }

    // =========================================================================
    // OI-I11 -- No ObjectInteracted, no other signal: falls through to Rule 9 (Empty)
    // =========================================================================

    [Fact]
    public void NoObjectInteracted_NoOtherSignal_ReturnsEmpty_OI_I11()
    {
        // CONTRACT: Given before and after identical (no changes, no signals),
        //           When Infer is called,
        //           Then result == InferenceResult.Empty.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before; // no changes

        var result = engine.Infer(before, after);

        Assert.Equal(InferenceResult.Empty, result);
    }

    // =========================================================================
    // OI-F1 -- StepFactory builds InteractObjectStep with signal InteractableId and position
    // =========================================================================

    [Fact]
    public void StepFactory_InteractObject_UsesSignal_InteractableIdAndPosition_OI_F1()
    {
        // CONTRACT: Given after.ObjectInteracted = new(2001500, 10f, 5f, 20f), zone=134,
        //           When Build("interact-object", "interact-1", null, after),
        //           Then result is InteractObjectStep with InteractableId=2001500,
        //                Position=(10,5,20), Zone="134".

        var after = MakeSnapshot(zone: new ZoneId(134)) with
        {
            ObjectInteracted = new ObjectInteractedSignal(2001500u, 10f, 5f, 20f)
        };

        var step = StepFactory.Build("interact-object", "interact-1", null, after);

        var io = Assert.IsType<QuestForge.Schema.InteractObjectStep>(step);
        Assert.Equal("interact-1",  io.Id);
        Assert.Equal(2001500u,      io.InteractableId);
        Assert.Equal(10f,           io.Position.X);
        Assert.Equal(5f,            io.Position.Y);
        Assert.Equal(20f,           io.Position.Z);
        Assert.Equal("134",         io.Zone);
    }

    // =========================================================================
    // OI-F2 -- StepFactory falls back to npcId when no ObjectInteracted signal
    // =========================================================================

    [Fact]
    public void StepFactory_InteractObject_NoSignal_FallsBackToNpcId_OI_F2()
    {
        // CONTRACT: Given after.ObjectInteracted = null,
        //           after.LastNpcInteracted = NpcId(5555),
        //           after.LastNpcPosition = (1, 2, 3),
        //           When Build("interact-object", "interact-1", null, after),
        //           Then result is InteractObjectStep with InteractableId=5555,
        //                Position=(1,2,3).

        var after = MakeSnapshot() with
        {
            // ObjectInteracted is null (default)
        };
        // Set LastNpcInteracted and LastNpcPosition via the constructor record
        after = new GameStateSnapshot(
            CapturedAt:          T0,
            Zone:                new ZoneId(134),
            Position:            new WorldPosition(0, 0, 0),
            ActiveQuest:         ActiveQuest,
            QuestSequence:       0,
            QuestFlags:          0,
            QuestAccepted:       false,
            QuestCompleted:      false,
            LastNpcInteracted:   new NpcId(5555),
            LastNpcPosition:     new WorldPosition(1, 2, 3),
            LastDialoguePrompt:  null,
            LastDialogueAnswer:  null,
            InventoryHash:       0,
            LastAttuned:         null);

        var step = StepFactory.Build("interact-object", "interact-1", null, after);

        var io = Assert.IsType<QuestForge.Schema.InteractObjectStep>(step);
        Assert.Equal(5555u,  io.InteractableId);
        Assert.Equal(1f,     io.Position.X);
        Assert.Equal(2f,     io.Position.Y);
        Assert.Equal(3f,     io.Position.Z);
    }

    // =========================================================================
    // OI-F3 -- StepFactory builds PickupItemStep with signal InteractableId and position
    // =========================================================================

    [Fact]
    public void StepFactory_PickupItem_UsesSignal_InteractableIdAndPosition_OI_F3()
    {
        // CONTRACT: Given after.ObjectInteracted = new(2001234, 81.5f, 7f, 32.2f), zone=134,
        //           When Build("pickup-item", "pickup-1", null, after),
        //           Then result is PickupItemStep with InteractableId=2001234,
        //                Position=(81.5,7,32.2), Zone="134".

        var after = MakeSnapshot(zone: new ZoneId(134)) with
        {
            ObjectInteracted = new ObjectInteractedSignal(2001234u, 81.5f, 7f, 32.2f)
        };

        var step = StepFactory.Build("pickup-item", "pickup-1", null, after);

        var pi = Assert.IsType<QuestForge.Schema.PickupItemStep>(step);
        Assert.Equal("pickup-1",    pi.Id);
        Assert.Equal(2001234u,      pi.InteractableId);
        Assert.Equal(81.5f,         pi.Position.X);
        Assert.Equal(7f,            pi.Position.Y);
        Assert.Equal(32.2f,         pi.Position.Z);
        Assert.Equal("134",         pi.Zone);
    }

    // =========================================================================
    // OI-A1 -- Aggregator: OnObjectInteracted sets ObjectInteracted on Current
    // =========================================================================

    [Fact]
    public void Aggregator_OnObjectInteracted_SetsField_OI_A1()
    {
        // CONTRACT: Given a fresh SnapshotAggregator,
        //           When OnObjectInteracted(2001500, 10f, 5f, 20f) is called,
        //           Then Current.ObjectInteracted is new ObjectInteractedSignal(2001500, 10, 5, 20).

        var clock = new FakeClock(T0);
        var agg   = new SnapshotAggregator(activeQuest: null, clock: clock);

        agg.OnObjectInteracted(2001500u, 10f, 5f, 20f);

        var snap = agg.Current;

        Assert.NotNull(snap.ObjectInteracted);
        Assert.Equal(2001500u, snap.ObjectInteracted!.InteractableId);
        Assert.Equal(10f,      snap.ObjectInteracted.X);
        Assert.Equal(5f,       snap.ObjectInteracted.Y);
        Assert.Equal(20f,      snap.ObjectInteracted.Z);
    }

    // =========================================================================
    // OI-A2 -- Aggregator: OnObjectInteractedConsumed clears the signal
    // =========================================================================

    [Fact]
    public void Aggregator_OnObjectInteractedConsumed_ClearsSignal_OI_A2()
    {
        // CONTRACT: Given aggregator with ObjectInteracted set,
        //           When OnObjectInteractedConsumed() is called,
        //           Then Current.ObjectInteracted is null.

        var clock = new FakeClock(T0);
        var agg   = new SnapshotAggregator(activeQuest: null, clock: clock);

        agg.OnObjectInteracted(2001500u, 10f, 5f, 20f);
        Assert.NotNull(agg.Current.ObjectInteracted); // sanity

        agg.OnObjectInteractedConsumed();

        Assert.Null(agg.Current.ObjectInteracted);
    }

    // =========================================================================
    // OI-A3 -- Aggregator: ObjectInteracted survives ResetDeltas
    // =========================================================================

    [Fact]
    public void Aggregator_ObjectInteracted_SurvivesResetDeltas_OI_A3()
    {
        // CONTRACT: Given aggregator with ObjectInteracted set,
        //           When ResetDeltas() is called,
        //           Then Current.ObjectInteracted is STILL non-null (signal preserved).

        var clock = new FakeClock(T0);
        var agg   = new SnapshotAggregator(activeQuest: null, clock: clock);

        agg.OnObjectInteracted(2001500u, 10f, 5f, 20f);

        agg.ResetDeltas();

        var snap = agg.Current;
        Assert.NotNull(snap.ObjectInteracted);
        Assert.Equal(2001500u, snap.ObjectInteracted!.InteractableId);
    }

    // =========================================================================
    // OI-A4 -- Aggregator: last-write-wins on multiple OnObjectInteracted calls
    // =========================================================================

    [Fact]
    public void Aggregator_MultipleOnObjectInteracted_LastWriteWins_OI_A4()
    {
        // CONTRACT: Given OnObjectInteracted(100, 1, 2, 3) then OnObjectInteracted(200, 4, 5, 6),
        //           Then Current.ObjectInteracted?.InteractableId == 200.

        var clock = new FakeClock(T0);
        var agg   = new SnapshotAggregator(activeQuest: null, clock: clock);

        agg.OnObjectInteracted(100u, 1f, 2f, 3f);
        agg.OnObjectInteracted(200u, 4f, 5f, 6f);

        Assert.Equal(200u, agg.Current.ObjectInteracted?.InteractableId);
        Assert.Equal(4f,   agg.Current.ObjectInteracted?.X);
        Assert.Equal(5f,   agg.Current.ObjectInteracted?.Y);
        Assert.Equal(6f,   agg.Current.ObjectInteracted?.Z);
    }
}
