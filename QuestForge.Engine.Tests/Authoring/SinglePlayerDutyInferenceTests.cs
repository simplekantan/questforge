using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

// WHY: QuestForge.Schema is NOT imported globally to avoid ambiguity between Schema types and
// Adapters.Types on common names (AetheryteId). Schema types are referenced with full qualification.

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for SinglePlayerDutyStep authoring inference -- covers three areas:
///   SPDI-I1..I12  : StepInferenceEngine Rule 2.8 -- SpdEntryDetected (priority pinning)
///   SPDI-A1..A4   : SnapshotAggregator.OnSpdEntryDetected / OnSpdEntryConsumed
///   SPDI-F1..F6   : StepFactory.Build "single-player-duty" arm
///
/// ALL tests in this file will fail to compile until Builder adds:
///   - SpdEntryDetectedSignal record in GameStateSnapshot.cs           (Task 1)
///   - GameStateSnapshot.SpdEntryDetected property                      (Task 1)
///   - InferredFrom.SpdEntryDetected enum value                         (Task 2)
///   - SnapshotAggregator.OnSpdEntryDetected / OnSpdEntryConsumed       (Task 3)
///   - StepInferenceEngine Rule 2.8 (SpdEntryDetected check)            (Task 4)
///   - StepFactory.Build "single-player-duty" arm                       (Task 5)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~SinglePlayerDutyInferenceTests"
/// </summary>
public sealed class SinglePlayerDutyInferenceTests
{
    private static readonly QuestId        ActiveQuest = new(2054);
    private static readonly DateTimeOffset T0          = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // =========================================================================
    // Snapshot builder helper (mirrors UseActionInferenceTests.MakeSnapshot)
    // =========================================================================

    private static GameStateSnapshot MakeSnapshot(
        ZoneId?        zone             = null,
        WorldPosition? position         = null,
        bool           questAccepted    = false,
        bool           questCompleted   = false,
        int            questSequence    = 0,
        NpcId?         lastNpcInteracted = null,
        WorldPosition? lastNpcPosition  = null,
        SpdEntryDetectedSignal? spdEntryDetected = null) =>
        new(
            CapturedAt:          T0,
            Zone:                zone ?? new ZoneId(140),
            Position:            position ?? new WorldPosition(0, 0, 0),
            ActiveQuest:         ActiveQuest,
            QuestSequence:       questSequence,
            QuestFlags:          0,
            QuestAccepted:       questAccepted,
            QuestCompleted:      questCompleted,
            LastNpcInteracted:   lastNpcInteracted,
            LastNpcPosition:     lastNpcPosition,
            LastDialoguePrompt:  null,
            LastDialogueAnswer:  null,
            InventoryHash:       0,
            LastAttuned:         null)
        {
            SpdEntryDetected = spdEntryDetected
        };

    // =========================================================================
    // SPDI-I1 -- Talk-entry SPD with NPC + SelectYesno
    // =========================================================================

    [Fact]
    public void SpdInference_TalkEntry_NpcAndSelectYesno_InfersSinglePlayerDutyStep_SPDI_I1()
    {
        // CONTRACT: Given before snapshot has Zone=140, Position=(10,0,20), SpdEntryDetected=null.
        //           After snapshot has Zone=500, SpdEntryDetected=new(123),
        //           LastNpcInteracted=NpcId(1000100), LastNpcPosition=(15,0,25),
        //           SelectYesnoConfirmed=true.
        //           When Infer(before, after) is called,
        //           Then Result has StepType="single-player-duty",
        //                InferredFrom=SpdEntryDetected, Confidence=High,
        //                SuggestedExpect=null, SuggestedStepId="spd-123".
        //                Notes contain "CfcId=123" and "EntryKind=Talk" and "EntryTargetId=1000100".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(140),
            position: new WorldPosition(10, 0, 20));
        var after = MakeSnapshot(
            zone: new ZoneId(500),
            lastNpcInteracted: new NpcId(1000100),
            lastNpcPosition: new WorldPosition(15, 0, 25),
            spdEntryDetected: new SpdEntryDetectedSignal(123))
        with { SelectYesnoConfirmed = true };

        var result = engine.Infer(before, after);

        Assert.Equal("single-player-duty",             result.StepType);
        Assert.Equal("spd-123",                        result.SuggestedStepId);
        Assert.Null(result.SuggestedExpect);
        Assert.Equal(Confidence.High,                  result.Confidence);
        Assert.Equal(InferredFrom.SpdEntryDetected,    result.InferredFrom);
        Assert.NotNull(result.Notes);
        Assert.Contains("CfcId=123",                   result.Notes!);
        Assert.Contains("EntryKind=Talk",               result.Notes);
        Assert.Contains("EntryTargetId=1000100",        result.Notes);
    }

    // =========================================================================
    // SPDI-I2 -- Interact-entry SPD with EventObj
    // =========================================================================

    [Fact]
    public void SpdInference_InteractEntry_EventObj_InfersSinglePlayerDutyStep_SPDI_I2()
    {
        // CONTRACT: Given before snapshot has Zone=140, Position=(10,0,20), SpdEntryDetected=null.
        //           After snapshot has Zone=500, SpdEntryDetected=new(456),
        //           ObjectInteracted=new(2001234, 81.5f, 7f, 32.2f).
        //           When Infer(before, after) is called,
        //           Then Result has StepType="single-player-duty",
        //                InferredFrom=SpdEntryDetected, Confidence=High.
        //                Notes contain "EntryKind=Interact" and "EntryTargetId=2001234".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(140),
            position: new WorldPosition(10, 0, 20));
        var after = MakeSnapshot(
            zone: new ZoneId(500),
            spdEntryDetected: new SpdEntryDetectedSignal(456))
        with { ObjectInteracted = new ObjectInteractedSignal(2001234u, 81.5f, 7f, 32.2f) };

        var result = engine.Infer(before, after);

        Assert.Equal("single-player-duty",             result.StepType);
        Assert.Equal(Confidence.High,                  result.Confidence);
        Assert.Equal(InferredFrom.SpdEntryDetected,    result.InferredFrom);
        Assert.NotNull(result.Notes);
        Assert.Contains("EntryKind=Interact",           result.Notes!);
        Assert.Contains("EntryTargetId=2001234",        result.Notes);
    }

    // =========================================================================
    // SPDI-I3 -- Proximity-entry SPD (no NPC, no object, no dialog)
    // =========================================================================

    [Fact]
    public void SpdInference_ProximityEntry_NoNpcNoObject_InfersSinglePlayerDutyStep_SPDI_I3()
    {
        // CONTRACT: Given before has Zone=140, Position=(50,0,60), SpdEntryDetected=null.
        //           After has Zone=500, SpdEntryDetected=new(789),
        //           LastNpcInteracted=null, ObjectInteracted=null,
        //           SelectYesnoConfirmed=false, DialogueOptionSelected=null.
        //           When Infer(before, after) is called,
        //           Then Result has StepType="single-player-duty",
        //                InferredFrom=SpdEntryDetected, Confidence=High.
        //                Notes contain "EntryKind=Proximity" and "EntryTargetId=".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(140),
            position: new WorldPosition(50, 0, 60));
        var after = MakeSnapshot(
            zone: new ZoneId(500),
            spdEntryDetected: new SpdEntryDetectedSignal(789));

        var result = engine.Infer(before, after);

        Assert.Equal("single-player-duty",             result.StepType);
        Assert.Equal(Confidence.High,                  result.Confidence);
        Assert.Equal(InferredFrom.SpdEntryDetected,    result.InferredFrom);
        Assert.NotNull(result.Notes);
        Assert.Contains("EntryKind=Proximity",          result.Notes!);
        Assert.Contains("EntryTargetId=",               result.Notes);
    }

    // =========================================================================
    // SPDI-I4 -- SpdEntryDetected on both before and after -- no fire (already inside)
    // =========================================================================

    [Fact]
    public void SpdInference_BothBeforeAndAfterHaveSignal_DoesNotFireSpdRule_SPDI_I4()
    {
        // CONTRACT: Given before.SpdEntryDetected=new(123) AND after.SpdEntryDetected=new(123),
        //           When Infer(before, after) is called,
        //           Then Result does NOT have StepType="single-player-duty".
        //           Falls through to lower rules (zone change, sequence change, etc.).

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(spdEntryDetected: new SpdEntryDetectedSignal(123));
        var after  = MakeSnapshot(spdEntryDetected: new SpdEntryDetectedSignal(123));

        var result = engine.Infer(before, after);

        Assert.NotEqual("single-player-duty", result.StepType);
    }

    // =========================================================================
    // SPDI-I5 -- Rule priority: SpdEntryDetected beats QuestSequence advance (Rule 3)
    // =========================================================================

    [Fact]
    public void SpdInference_BeatsQuestSequenceAdvance_Rule28OverRule3_SPDI_I5()
    {
        // CONTRACT: Given before has QuestSequence=1, SpdEntryDetected=null.
        //           After has QuestSequence=2, SpdEntryDetected=new(100),
        //           LastNpcInteracted=NpcId(1000100), SelectYesnoConfirmed=true.
        //           When Infer(before, after) is called,
        //           Then Result has StepType="single-player-duty", NOT "talk".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 1);
        var after  = MakeSnapshot(
            questSequence: 2,
            lastNpcInteracted: new NpcId(1000100),
            spdEntryDetected: new SpdEntryDetectedSignal(100))
        with { SelectYesnoConfirmed = true };

        var result = engine.Infer(before, after);

        Assert.Equal("single-player-duty",             result.StepType);
        Assert.Equal(InferredFrom.SpdEntryDetected,    result.InferredFrom);
        Assert.NotEqual("talk",                         result.StepType);
    }

    // =========================================================================
    // SPDI-I6 -- Rule priority: QuestCompleted (Rule 1) beats SpdEntryDetected
    // =========================================================================

    [Fact]
    public void SpdInference_LosesToQuestCompleted_Rule1Wins_SPDI_I6()
    {
        // CONTRACT: Given before has QuestCompleted=false, SpdEntryDetected=null.
        //           After has QuestCompleted=true, SpdEntryDetected=new(100).
        //           When Infer(before, after) is called,
        //           Then Result has StepType="turn-in", NOT "single-player-duty".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questCompleted: false);
        var after  = MakeSnapshot(
            questCompleted: true,
            spdEntryDetected: new SpdEntryDetectedSignal(100));

        var result = engine.Infer(before, after);

        Assert.Equal("turn-in",                        result.StepType);
        Assert.Equal(InferredFrom.QuestCompleted,      result.InferredFrom);
        Assert.NotEqual("single-player-duty",           result.StepType);
    }

    // =========================================================================
    // SPDI-I7 -- Rule priority: QuestAccepted (Rule 2) beats SpdEntryDetected
    // =========================================================================

    [Fact]
    public void SpdInference_LosesToQuestAccepted_Rule2Wins_SPDI_I7()
    {
        // CONTRACT: Given before has QuestAccepted=false, SpdEntryDetected=null.
        //           After has QuestAccepted=true, SpdEntryDetected=new(100).
        //           When Infer(before, after) is called,
        //           Then Result has StepType="accept", NOT "single-player-duty".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questAccepted: false);
        var after  = MakeSnapshot(
            questAccepted: true,
            spdEntryDetected: new SpdEntryDetectedSignal(100));

        var result = engine.Infer(before, after);

        Assert.Equal("accept",                         result.StepType);
        Assert.Equal(InferredFrom.QuestAccepted,       result.InferredFrom);
        Assert.NotEqual("single-player-duty",           result.StepType);
    }

    // =========================================================================
    // SPDI-I8 -- Talk-entry with SelectString (difficulty dialog) instead of SelectYesno
    // =========================================================================

    [Fact]
    public void SpdInference_TalkEntry_SelectString_InfersTalkEntry_SPDI_I8()
    {
        // CONTRACT: Given before has SpdEntryDetected=null.
        //           After has SpdEntryDetected=new(123), LastNpcInteracted=NpcId(1000100),
        //           LastNpcPosition=(15,0,25), DialogueOptionSelected=1,
        //           SelectYesnoConfirmed=false.
        //           When Infer(before, after) is called,
        //           Then Result has StepType="single-player-duty",
        //                Notes contain "EntryKind=Talk".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = MakeSnapshot(
            zone: new ZoneId(500),
            lastNpcInteracted: new NpcId(1000100),
            lastNpcPosition: new WorldPosition(15, 0, 25),
            spdEntryDetected: new SpdEntryDetectedSignal(123))
        with { DialogueOptionSelected = 1, SelectYesnoConfirmed = false };

        var result = engine.Infer(before, after);

        Assert.Equal("single-player-duty",             result.StepType);
        Assert.Equal(InferredFrom.SpdEntryDetected,    result.InferredFrom);
        Assert.NotNull(result.Notes);
        Assert.Contains("EntryKind=Talk",               result.Notes!);
    }

    // =========================================================================
    // SPDI-I9 -- NPC present but no dialog confirmation -- falls back to proximity
    // =========================================================================

    [Fact]
    public void SpdInference_NpcWithoutDialogConfirmation_FallsBackToProximity_SPDI_I9()
    {
        // CONTRACT: Given before has LastNpcInteracted=NpcId(1000100), SpdEntryDetected=null.
        //           After has SpdEntryDetected=new(123), LastNpcInteracted=NpcId(1000100),
        //           SelectYesnoConfirmed=false, DialogueOptionSelected=null,
        //           ObjectInteracted=null.
        //           When Infer(before, after) is called,
        //           Then Result has StepType="single-player-duty",
        //                Notes contain "EntryKind=Proximity".
        //           The stale NPC is not used without dialog confirmation.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(lastNpcInteracted: new NpcId(1000100));
        var after  = MakeSnapshot(
            zone: new ZoneId(500),
            lastNpcInteracted: new NpcId(1000100),
            spdEntryDetected: new SpdEntryDetectedSignal(123));

        var result = engine.Infer(before, after);

        Assert.Equal("single-player-duty",             result.StepType);
        Assert.Equal(InferredFrom.SpdEntryDetected,    result.InferredFrom);
        Assert.NotNull(result.Notes);
        Assert.Contains("EntryKind=Proximity",          result.Notes!);
    }

    // =========================================================================
    // SPDI-I10 -- ObjectInteracted beats NPC + dialog (interact-entry priority)
    // =========================================================================

    [Fact]
    public void SpdInference_ObjectInteractedBeatsNpcPlusDialog_InteractEntryWins_SPDI_I10()
    {
        // CONTRACT: Given before has SpdEntryDetected=null.
        //           After has SpdEntryDetected=new(123),
        //           ObjectInteracted=new(2001234, 81.5f, 7f, 32.2f),
        //           LastNpcInteracted=NpcId(1000100), SelectYesnoConfirmed=true.
        //           When Infer(before, after) is called,
        //           Then Notes contain "EntryKind=Interact"
        //           (ObjectInteracted wins over NPC+dialog).

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = MakeSnapshot(
            zone: new ZoneId(500),
            lastNpcInteracted: new NpcId(1000100),
            spdEntryDetected: new SpdEntryDetectedSignal(123))
        with { ObjectInteracted = new ObjectInteractedSignal(2001234u, 81.5f, 7f, 32.2f),
               SelectYesnoConfirmed = true };

        var result = engine.Infer(before, after);

        Assert.Equal("single-player-duty",             result.StepType);
        Assert.NotNull(result.Notes);
        Assert.Contains("EntryKind=Interact",           result.Notes!);
    }

    // =========================================================================
    // SPDI-I11 -- NPC from before snapshot used when after.LastNpcInteracted is null
    // =========================================================================

    [Fact]
    public void SpdInference_NpcFromBeforeSnapshot_WhenAfterNpcNull_SPDI_I11()
    {
        // CONTRACT: Given before has LastNpcInteracted=NpcId(1000200),
        //           LastNpcPosition=(20,0,30), SpdEntryDetected=null.
        //           After has SpdEntryDetected=new(123), LastNpcInteracted=null,
        //           SelectYesnoConfirmed=true.
        //           When Infer(before, after) is called,
        //           Then Notes contain "EntryKind=Talk" and "EntryTargetId=1000200".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            lastNpcInteracted: new NpcId(1000200),
            lastNpcPosition: new WorldPosition(20, 0, 30));
        var after = MakeSnapshot(
            zone: new ZoneId(500),
            spdEntryDetected: new SpdEntryDetectedSignal(123))
        with { SelectYesnoConfirmed = true };

        var result = engine.Infer(before, after);

        Assert.Equal("single-player-duty",             result.StepType);
        Assert.Equal(InferredFrom.SpdEntryDetected,    result.InferredFrom);
        Assert.NotNull(result.Notes);
        Assert.Contains("EntryKind=Talk",               result.Notes!);
        Assert.Contains("EntryTargetId=1000200",        result.Notes);
    }

    // =========================================================================
    // SPDI-I12 -- Rule priority: Combat (Rule 2.2) beats SpdEntryDetected when
    //             nibble bumps present
    // =========================================================================

    [Fact]
    public void SpdInference_LosesToCombat_Rule22Wins_WhenNibbleBumps_SPDI_I12()
    {
        // CONTRACT: Given before has SpdEntryDetected=null.
        //           After has SpdEntryDetected=new(100),
        //           KillCorrelatedTargets with one entry (dataId=999, FinalValue=2).
        //           When Infer(before, after) is called,
        //           Then Result has StepType="combat", NOT "single-player-duty".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = MakeSnapshot(
            spdEntryDetected: new SpdEntryDetectedSignal(100))
        with { KillCorrelatedTargets = new Dictionary<NibbleKey, KillCorrelation>
            {
                [new NibbleKey(0, NibbleHalf.Low)] = new KillCorrelation([999u], 2)
            } };

        var result = engine.Infer(before, after);

        Assert.Equal("combat",                         result.StepType);
        Assert.Equal(InferredFrom.Combat,              result.InferredFrom);
        Assert.NotEqual("single-player-duty",           result.StepType);
    }

    // =========================================================================
    // SPDI-A1 -- Aggregator: OnSpdEntryDetected sets the SpdEntryDetected field
    // =========================================================================

    [Fact]
    public void Aggregator_OnSpdEntryDetected_SetsField_SPDI_A1()
    {
        // CONTRACT: Given a fresh SnapshotAggregator,
        //           When OnSpdEntryDetected(123) is called,
        //           Then agg.Current.SpdEntryDetected is non-null
        //                with ContentFinderConditionId=123.

        var clock = new FakeClock(T0);
        var agg   = new SnapshotAggregator(activeQuest: null, clock: clock);

        agg.OnSpdEntryDetected(123);

        var snap = agg.Current;

        Assert.NotNull(snap.SpdEntryDetected);
        Assert.Equal((ushort)123, snap.SpdEntryDetected!.ContentFinderConditionId);
    }

    // =========================================================================
    // SPDI-A2 -- Aggregator: OnSpdEntryConsumed clears the field
    // =========================================================================

    [Fact]
    public void Aggregator_OnSpdEntryConsumed_ClearsField_SPDI_A2()
    {
        // CONTRACT: Given OnSpdEntryDetected(123) was called,
        //           When OnSpdEntryConsumed() is called,
        //           Then agg.Current.SpdEntryDetected is null.

        var clock = new FakeClock(T0);
        var agg   = new SnapshotAggregator(activeQuest: null, clock: clock);
        agg.OnSpdEntryDetected(123);
        Assert.NotNull(agg.Current.SpdEntryDetected); // sanity

        agg.OnSpdEntryConsumed();

        Assert.Null(agg.Current.SpdEntryDetected);
    }

    // =========================================================================
    // SPDI-A3 -- Aggregator: ResetDeltas does NOT clear SpdEntryDetected
    // =========================================================================

    [Fact]
    public void Aggregator_ResetDeltas_DoesNotClearSpdEntryDetected_SPDI_A3()
    {
        // CONTRACT: Given OnSpdEntryDetected(123) was called,
        //           When ResetDeltas() is called (per-window delta reset),
        //           Then agg.Current.SpdEntryDetected is still set.
        //
        // WHY: SpdEntryDetected survives the per-window reset so PreviewInference can see it
        //      when the author opens the Record-Step modal. Matches ActionCompleted lifecycle.

        var clock = new FakeClock(T0);
        var agg   = new SnapshotAggregator(activeQuest: null, clock: clock);
        agg.OnSpdEntryDetected(123);

        agg.ResetDeltas();

        Assert.NotNull(agg.Current.SpdEntryDetected);
        Assert.Equal((ushort)123, agg.Current.SpdEntryDetected!.ContentFinderConditionId);
    }

    // =========================================================================
    // SPDI-A4 -- Aggregator: last-write-wins on multiple OnSpdEntryDetected calls
    // =========================================================================

    [Fact]
    public void Aggregator_MultipleSpdEntryDetected_LastWriteWins_SPDI_A4()
    {
        // CONTRACT: Given OnSpdEntryDetected(100) then OnSpdEntryDetected(200),
        //           Then agg.Current.SpdEntryDetected?.ContentFinderConditionId == 200.

        var clock = new FakeClock(T0);
        var agg   = new SnapshotAggregator(activeQuest: null, clock: clock);

        agg.OnSpdEntryDetected(100);
        agg.OnSpdEntryDetected(200);

        Assert.Equal((ushort)200, agg.Current.SpdEntryDetected?.ContentFinderConditionId);
    }

    // =========================================================================
    // SPDI-F1 -- Factory produces SinglePlayerDutyStep with talk entry
    // =========================================================================

    [Fact]
    public void StepFactory_SinglePlayerDuty_TalkEntry_ProducesCorrectStep_SPDI_F1()
    {
        // CONTRACT: Given stepType="single-player-duty", after snapshot has
        //           SpdEntryDetected=new(123), LastNpcInteracted=NpcId(1000100),
        //           LastNpcPosition=(15,0,25), SelectYesnoConfirmed=true.
        //           When StepFactory.Build(...) is called,
        //           Then Result is SinglePlayerDutyStep with
        //                ContentFinderConditionId=123, EntryKind=Talk,
        //                EntryTargetId=1000100, EntryPosition=(15,0,25).

        var after = MakeSnapshot(
            lastNpcInteracted: new NpcId(1000100),
            lastNpcPosition: new WorldPosition(15, 0, 25),
            spdEntryDetected: new SpdEntryDetectedSignal(123))
        with { SelectYesnoConfirmed = true };

        var step = StepFactory.Build("single-player-duty", "spd-123", null, after);

        var spd = Assert.IsType<QuestForge.Schema.SinglePlayerDutyStep>(step);
        Assert.Equal("spd-123",                        spd.Id);
        Assert.Equal(123u,                             spd.ContentFinderConditionId);
        Assert.Equal(QuestForge.Schema.SpdEntryKind.Talk, spd.EntryKind);
        Assert.Equal(1000100u,                         spd.EntryTargetId);
        Assert.Equal(15f,                              spd.EntryPosition.X);
        Assert.Equal(0f,                               spd.EntryPosition.Y);
        Assert.Equal(25f,                              spd.EntryPosition.Z);
    }

    // =========================================================================
    // SPDI-F2 -- Factory produces SinglePlayerDutyStep with interact entry
    // =========================================================================

    [Fact]
    public void StepFactory_SinglePlayerDuty_InteractEntry_ProducesCorrectStep_SPDI_F2()
    {
        // CONTRACT: Given stepType="single-player-duty", after has
        //           SpdEntryDetected=new(456),
        //           ObjectInteracted=new(2001234, 81.5f, 7f, 32.2f).
        //           When StepFactory.Build(...) is called,
        //           Then Result is SinglePlayerDutyStep with
        //                ContentFinderConditionId=456, EntryKind=Interact,
        //                EntryTargetId=2001234, EntryPosition=(81.5, 7, 32.2).

        var after = MakeSnapshot(
            spdEntryDetected: new SpdEntryDetectedSignal(456))
        with { ObjectInteracted = new ObjectInteractedSignal(2001234u, 81.5f, 7f, 32.2f) };

        var step = StepFactory.Build("single-player-duty", "spd-456", null, after);

        var spd = Assert.IsType<QuestForge.Schema.SinglePlayerDutyStep>(step);
        Assert.Equal(456u,                               spd.ContentFinderConditionId);
        Assert.Equal(QuestForge.Schema.SpdEntryKind.Interact, spd.EntryKind);
        Assert.Equal(2001234u,                           spd.EntryTargetId);
        Assert.Equal(81.5f,                              spd.EntryPosition.X, 1);
        Assert.Equal(7f,                                 spd.EntryPosition.Y, 1);
        Assert.Equal(32.2f,                              spd.EntryPosition.Z, 1);
    }

    // =========================================================================
    // SPDI-F3 -- Factory produces SinglePlayerDutyStep with proximity entry
    // =========================================================================

    [Fact]
    public void StepFactory_SinglePlayerDuty_ProximityEntry_ProducesCorrectStep_SPDI_F3()
    {
        // CONTRACT: Given stepType="single-player-duty", before has Position=(50,0,60),
        //           after has SpdEntryDetected=new(789),
        //           ObjectInteracted=null, LastNpcInteracted=null.
        //           When StepFactory.Build(...) is called,
        //           Then Result is SinglePlayerDutyStep with
        //                ContentFinderConditionId=789, EntryKind=Proximity,
        //                EntryTargetId=null, EntryPosition=(50,0,60).

        var before = MakeSnapshot(position: new WorldPosition(50, 0, 60));
        var after  = MakeSnapshot(
            zone: new ZoneId(500),
            spdEntryDetected: new SpdEntryDetectedSignal(789));

        var step = StepFactory.Build("single-player-duty", "spd-789", null, after, before: before);

        var spd = Assert.IsType<QuestForge.Schema.SinglePlayerDutyStep>(step);
        Assert.Equal(789u,                                 spd.ContentFinderConditionId);
        Assert.Equal(QuestForge.Schema.SpdEntryKind.Proximity, spd.EntryKind);
        Assert.Null(spd.EntryTargetId);
        Assert.Equal(50f,                                  spd.EntryPosition.X);
        Assert.Equal(0f,                                   spd.EntryPosition.Y);
        Assert.Equal(60f,                                  spd.EntryPosition.Z);
    }

    // =========================================================================
    // SPDI-F4 -- Factory with null SpdEntryDetected defaults CfcId to 0
    // =========================================================================

    [Fact]
    public void StepFactory_SinglePlayerDuty_NullSpdSignal_FallsBackToZeroCfcId_SPDI_F4()
    {
        // CONTRACT: Given stepType="single-player-duty", after has SpdEntryDetected=null.
        //           When StepFactory.Build(...) is called,
        //           Then Result is SinglePlayerDutyStep with ContentFinderConditionId=0
        //           (draft validator will catch this as E30).

        var after = MakeSnapshot(); // SpdEntryDetected is null

        var ex = Record.Exception(() =>
        {
            var step = StepFactory.Build("single-player-duty", "spd-0", null, after);
            var spd = Assert.IsType<QuestForge.Schema.SinglePlayerDutyStep>(step);
            Assert.Equal(0u, spd.ContentFinderConditionId);
        });

        Assert.Null(ex);
    }

    // =========================================================================
    // SPDI-F5 -- Factory interact-entry beats talk-entry when both signals present
    // =========================================================================

    [Fact]
    public void StepFactory_SinglePlayerDuty_InteractBeatsNpc_WhenBothPresent_SPDI_F5()
    {
        // CONTRACT: Given stepType="single-player-duty", after has SpdEntryDetected=new(123),
        //           ObjectInteracted=new(2001234, 10f, 0f, 20f),
        //           LastNpcInteracted=NpcId(1000100), SelectYesnoConfirmed=true.
        //           When StepFactory.Build(...) is called,
        //           Then Result is SinglePlayerDutyStep with EntryKind=Interact.

        var after = MakeSnapshot(
            lastNpcInteracted: new NpcId(1000100),
            spdEntryDetected: new SpdEntryDetectedSignal(123))
        with { ObjectInteracted = new ObjectInteractedSignal(2001234u, 10f, 0f, 20f),
               SelectYesnoConfirmed = true };

        var step = StepFactory.Build("single-player-duty", "spd-123", null, after);

        var spd = Assert.IsType<QuestForge.Schema.SinglePlayerDutyStep>(step);
        Assert.Equal(QuestForge.Schema.SpdEntryKind.Interact, spd.EntryKind);
    }

    // =========================================================================
    // SPDI-F6 -- Factory talk-entry uses before.LastNpcPosition when after is null
    // =========================================================================

    [Fact]
    public void StepFactory_SinglePlayerDuty_TalkEntry_UsesBeforeNpcPosition_SPDI_F6()
    {
        // CONTRACT: Given stepType="single-player-duty",
        //           before has LastNpcInteracted=NpcId(1000200), LastNpcPosition=(20,0,30),
        //           after has SpdEntryDetected=new(123), LastNpcInteracted=null,
        //           LastNpcPosition=null, SelectYesnoConfirmed=true.
        //           When StepFactory.Build(...) is called,
        //           Then Result is SinglePlayerDutyStep with EntryKind=Talk,
        //                EntryTargetId=1000200, EntryPosition=(20,0,30).

        var before = MakeSnapshot(
            lastNpcInteracted: new NpcId(1000200),
            lastNpcPosition: new WorldPosition(20, 0, 30));
        var after = MakeSnapshot(
            zone: new ZoneId(500),
            spdEntryDetected: new SpdEntryDetectedSignal(123))
        with { SelectYesnoConfirmed = true };

        var step = StepFactory.Build("single-player-duty", "spd-123", null, after, before: before);

        var spd = Assert.IsType<QuestForge.Schema.SinglePlayerDutyStep>(step);
        Assert.Equal(QuestForge.Schema.SpdEntryKind.Talk, spd.EntryKind);
        Assert.Equal(1000200u,                             spd.EntryTargetId);
        Assert.Equal(20f,                                  spd.EntryPosition.X);
        Assert.Equal(0f,                                   spd.EntryPosition.Y);
        Assert.Equal(30f,                                  spd.EntryPosition.Z);
    }
}
