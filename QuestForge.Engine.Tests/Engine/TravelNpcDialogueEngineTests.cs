using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using NpcId = QuestForge.Adapters.Types.NpcId;
using QuestId = QuestForge.Adapters.Types.QuestId;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// RED PHASE: Tests for QuestEngine dispatch of TravelStep with RouteHint.NpcDialogue.
///
/// ALL tests will fail to compile or runtime-fail until Builder:
///   - Adds NpcDialogue arm to QuestEngine.ResolveActionForStep:
///       TravelStep when RouteHint.NpcDialogue is set
///       → ResolveInteractOrNavigate to NpcDialogue.Target.Position,
///         emitting Interact(NpcId(NpcDialogue.Target.NpcId), Origin: step)
///   - NpcDialogue arm must come BEFORE aethernet arm in the switch expression
///   - EngineAction.Interact.Origin carries the TravelStep so DialogueChoiceDispatcher
///     can read RouteHint.NpcDialogue.DialogueChoices from it
///
/// Test IDs: QE-1 through QE-9
/// </summary>
public sealed class TravelNpcDialogueEngineTests
{
    private const uint TestNpcId       = 7777u;
    private const uint TestQuestId     = 99002u;
    private const int  TestSequence    = 0;
    private const int  SourceZone      = 131;
    private const int  DestZone        = 200;

    // NPC (Lift Attendant) is at (10, 0, 20) in zone 131
    private static readonly Position3      NpcPos     = new(10f, 0f, 20f);
    private static readonly NpcLocation    NpcLoc     = new(NpcId: TestNpcId, Zone: SourceZone, Position: NpcPos);
    private static readonly WorldPosition  FarPlayer  = new(100f, 0f, 100f);  // XZ distance ~121 to NPC
    private static readonly WorldPosition  NearPlayer = new(11f,  0f, 20f);   // XZ distance = 1 to NPC

    private static NpcDialogueHint SingleChoiceHint =>
        new(NpcLoc, [new DialogueChoice(Type: "list", Answer: "2")]);

    private static TravelStep BuildTravelStep(
        NpcDialogueHint? npcDialogue = null,
        AethernetRouteHint? aethernet = null,
        float? stopDistance = null) => new()
    {
        Id = "lift-to-zone",
        Destination = new TravelDestination(Zone: DestZone, Position: NpcPos),
        StopDistance = stopDistance,
        RouteHint = (npcDialogue is not null || aethernet is not null)
            ? new RouteHint(Aethernet: aethernet, NpcDialogue: npcDialogue)
            : null
    };

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "NpcDialogue Engine Test Quest",
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

    // QE-1
    [Fact]
    public async Task QE1_PlayerFarFromNpc_EmitsNavigateToNpcPosition()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue arm to ResolveActionForStep.
         *
         * CONTRACT: Given TravelStep with RouteHint.NpcDialogue, player at (100,0,100)
         *           (XZ distance ~121 from NPC at (10,0,20), default StopDistance 3.0),
         *           When Engine.Tick is called,
         *           Then EngineAction.Navigate is returned with destination = NPC position.
         *
         * BUILDER GUIDANCE: The NpcDialogue arm calls ResolveInteractOrNavigate with
         * NpcDialogue.Target.Position as the target. Far away → Navigate first.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSequence);
        harness.GameState.SetPosition(FarPlayer);

        var quest = BuildSingleStepQuest(TestQuestId, TestSequence,
            BuildTravelStep(npcDialogue: SingleChoiceHint));
        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(NpcPos.X, navigate.Destination.X, precision: 3);
        Assert.Equal(NpcPos.Z, navigate.Destination.Z, precision: 3);
    }

    // QE-2
    [Fact]
    public async Task QE2_PlayerAtNpc_EmitsInteract_TargetIsNpcId()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue arm.
         *
         * CONTRACT: Given TravelStep with NpcDialogue, player near NPC (XZ distance = 1),
         *           When Engine.Tick is called,
         *           Then EngineAction.Interact is returned with Target == NpcId(7777)
         *                and Origin is the TravelStep.
         *
         * BUILDER GUIDANCE: Within stop distance → emit Interact(NpcId(npcDialogue.Target.NpcId), Origin: step).
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSequence);
        harness.GameState.SetPosition(NearPlayer);

        var step = BuildTravelStep(npcDialogue: SingleChoiceHint);
        var quest = BuildSingleStepQuest(TestQuestId, TestSequence, step);
        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var interact = Assert.IsType<EngineAction.Interact>(action);
        Assert.Equal(new NpcId(TestNpcId), interact.Target);
        Assert.NotNull(interact.Origin);
        Assert.IsType<TravelStep>(interact.Origin);
    }

    // QE-3
    [Fact]
    public async Task QE3_PlayerWithinDefaultStopDistance_EmitsInteract()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue arm.
         *
         * CONTRACT: Given TravelStep+NpcDialogue, default StopDistance (3.0),
         *           player at XZ distance 1 (within 3.0),
         *           When Engine.Tick is called,
         *           Then EngineAction.Interact (not Navigate).
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSequence);
        harness.GameState.SetPosition(NearPlayer); // distance 1

        var quest = BuildSingleStepQuest(TestQuestId, TestSequence,
            BuildTravelStep(npcDialogue: SingleChoiceHint));
        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        Assert.IsType<EngineAction.Interact>(action);
    }

    // QE-4
    [Fact]
    public async Task QE4_CustomStopDistance_Honoured_NavigateWithCorrectStoppingDistance()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue arm respecting StopDistance.
         *
         * CONTRACT: Given TravelStep+NpcDialogue with StopDistance=0.5,
         *           player at XZ distance 1 (beyond 0.5),
         *           When Engine.Tick is called,
         *           Then EngineAction.Navigate with Options.StoppingDistance == 0.5f.
         *
         * BUILDER GUIDANCE: Pass step.StopDistance ?? DefaultStopDistance to both the
         * comparison threshold and NavigationOptions.StoppingDistance, as existing arms do.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSequence);
        harness.GameState.SetPosition(NearPlayer); // distance 1, beyond custom stop of 0.5

        var quest = BuildSingleStepQuest(TestQuestId, TestSequence,
            BuildTravelStep(npcDialogue: SingleChoiceHint, stopDistance: 0.5f));
        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(0.5f, navigate.Options.StoppingDistance, precision: 3);
    }

    // QE-5
    [Fact]
    public async Task QE5_NpcDialogueBeatsAethernet_EmitsInteract_NotUseAethernet()
    {
        /*
         * RED: Will fail until Builder places NpcDialogue arm before aethernet arm.
         *
         * CONTRACT: Given TravelStep with BOTH RouteHint.NpcDialogue AND RouteHint.Aethernet set,
         *           player near NPC,
         *           When Engine.Tick is called,
         *           Then EngineAction.Interact is returned (not UseAethernet).
         *
         * BUILDER GUIDANCE: NpcDialogue arm must appear BEFORE the aethernet pattern in the
         * switch expression: `TravelStep t when t.RouteHint?.NpcDialogue is { } =>` comes first.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSequence);
        harness.GameState.SetPosition(NearPlayer);

        var quest = BuildSingleStepQuest(TestQuestId, TestSequence,
            BuildTravelStep(
                npcDialogue: SingleChoiceHint,
                aethernet: new AethernetRouteHint(From: 125u, To: 33u)));
        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        Assert.IsType<EngineAction.Interact>(action);
        Assert.IsNotType<EngineAction.UseAethernet>(action);
    }

    // QE-6
    [Fact]
    public async Task QE6_AethernetOnly_ExistingBehaviourPreserved_Regression()
    {
        /*
         * Regression guard: TravelStep with Aethernet only must still emit UseAethernet.
         *
         * CONTRACT: Given TravelStep with RouteHint.Aethernet only (no NpcDialogue),
         *           When Engine.Tick is called,
         *           Then EngineAction.UseAethernet is returned (existing aethernet behaviour).
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSequence);
        harness.GameState.SetPosition(NearPlayer);

        var step = new TravelStep
        {
            Id = "aethernet-only",
            Destination = new TravelDestination(Zone: SourceZone, Position: NpcPos),
            RouteHint = new RouteHint(Aethernet: new AethernetRouteHint(From: 125u, To: 33u))
        };
        var quest = BuildSingleStepQuest(TestQuestId, TestSequence, step);
        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        Assert.IsType<EngineAction.UseAethernet>(action);
    }

    // QE-7
    [Fact]
    public async Task QE7_TravelStepNoRouteHint_ExistingNavigateBehaviourPreserved_Regression()
    {
        /*
         * Regression guard: TravelStep without RouteHint still emits Navigate.
         *
         * CONTRACT: Given TravelStep with no RouteHint and Destination.Position set,
         *           When Engine.Tick is called,
         *           Then EngineAction.Navigate is returned (existing bare-travel behaviour).
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSequence);
        harness.GameState.SetPosition(FarPlayer);

        var step = new TravelStep
        {
            Id = "bare-travel",
            Destination = new TravelDestination(Zone: DestZone, Position: new Position3(50f, 0f, 50f)),
            RouteHint = null
        };
        var quest = BuildSingleStepQuest(TestQuestId, TestSequence, step);
        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        Assert.IsType<EngineAction.Navigate>(action);
    }

    // QE-8
    [Fact]
    public async Task QE8_PlayerPositionUnavailable_EmitsInteract_FailOpen()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue arm.
         *
         * CONTRACT: Given TravelStep+NpcDialogue and playerPos unavailable (adapter failure),
         *           When Engine.Tick is called,
         *           Then EngineAction.Interact is returned (fail-open: position unavailable
         *           → cannot compute distance → assume in range → Interact).
         *
         * BUILDER GUIDANCE: ResolveInteractOrNavigate already implements fail-open via:
         *   if (playerPos is null) return interactAction;
         * No extra code needed — just pass the correct args.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSequence);
        harness.GameState.SetPositionFailure("simulated adapter failure", "unit test");

        var quest = BuildSingleStepQuest(TestQuestId, TestSequence,
            BuildTravelStep(npcDialogue: SingleChoiceHint));
        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        Assert.IsType<EngineAction.Interact>(action);
    }

    // QE-9
    [Fact]
    public async Task QE9_Interact_Origin_Is_TravelStep_With_CorrectDialogueChoice()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue arm carrying Origin.
         *
         * CONTRACT: Given TravelStep+NpcDialogue with DialogueChoices[0].Answer=="2",
         *           player near NPC,
         *           When Engine.Tick is called and returns Interact,
         *           Then Interact.Origin is a TravelStep whose
         *                RouteHint.NpcDialogue.DialogueChoices[0].Answer == "2".
         *
         * BUILDER GUIDANCE: Pass Origin: step in the Interact action so that
         * DialogueChoiceDispatcher can find the choices via
         * ((TravelStep)interact.Origin).RouteHint.NpcDialogue.DialogueChoices.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSequence);
        harness.GameState.SetPosition(NearPlayer);

        var step = BuildTravelStep(npcDialogue: SingleChoiceHint);
        var quest = BuildSingleStepQuest(TestQuestId, TestSequence, step);
        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var interact = Assert.IsType<EngineAction.Interact>(action);
        var origin = Assert.IsType<TravelStep>(interact.Origin);
        Assert.NotNull(origin.RouteHint?.NpcDialogue);
        Assert.Single(origin.RouteHint!.NpcDialogue!.DialogueChoices);
        Assert.Equal("2", origin.RouteHint.NpcDialogue.DialogueChoices[0].Answer);
    }
}
