using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Dialogue;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Dialogue;

/// <summary>
/// RED PHASE — All tests will fail to COMPILE until Builder adds:
///   QuestForge.Engine/Dialogue/YesNoAnswer.cs   (enum YesNoAnswer { Yes, No })
///   QuestForge.Engine/Dialogue/YesNoContext.cs   (readonly record struct YesNoContext(bool RunActive, YesNoAnswer? AuthoredAnswer))
///   QuestForge.Engine/Dialogue/SelectYesnoDecider.cs  (static class with Decide method)
///   QuestEngine._lastResolvedStep, QuestEngine.CurrentYesNoAnswer, QuestEngine.ExtractYesNo
///
/// Spec: docs/SELECT_YESNO_RESPONDER_PLAN.md §Given-When-Then (17 GWT scenarios).
/// Place: QuestForge.Engine.Tests/Dialogue/SelectYesnoDeciderTests.cs
/// </summary>
public sealed class SelectYesnoDeciderTests
{
    // =========================================================================
    // Group A — SelectYesnoDecider.Decide (the core matrix)
    // =========================================================================

    // A1 — Run inactive + authored answer → don't answer (null)
    [Fact]
    public void A1_RunInactive_WithAuthoredAnswer_ReturnsNull()
    {
        // Given: run is NOT active; an authored Yes answer is present
        var ctx = new YesNoContext(RunActive: false, AuthoredAnswer: YesNoAnswer.Yes);

        // When
        var result = SelectYesnoDecider.Decide(ctx);

        // Then: null — we must NEVER answer when no run is active (user owns the popup)
        Assert.Null(result);
    }

    // A2 — Run active + authored Yes → Yes
    [Fact]
    public void A2_RunActive_AuthoredYes_ReturnsYes()
    {
        // Given
        var ctx = new YesNoContext(RunActive: true, AuthoredAnswer: YesNoAnswer.Yes);

        // When
        var result = SelectYesnoDecider.Decide(ctx);

        // Then
        Assert.Equal(YesNoAnswer.Yes, result);
    }

    // A3 — Run active + authored No → No
    [Fact]
    public void A3_RunActive_AuthoredNo_ReturnsNo()
    {
        // Given
        var ctx = new YesNoContext(RunActive: true, AuthoredAnswer: YesNoAnswer.No);

        // When
        var result = SelectYesnoDecider.Decide(ctx);

        // Then
        Assert.Equal(YesNoAnswer.No, result);
    }

    // A4 — Run active + no authored answer → default Yes (subsumes legacy always-Yes)
    [Fact]
    public void A4_RunActive_NullAuthoredAnswer_DefaultsToYes()
    {
        // Given: run is active; step authors no yesno choice at all
        var ctx = new YesNoContext(RunActive: true, AuthoredAnswer: null);

        // When
        var result = SelectYesnoDecider.Decide(ctx);

        // Then: Yes is the safe default — all current quests require Yes
        Assert.Equal(YesNoAnswer.Yes, result);
    }

    // A5 — Run inactive + no authored answer → don't answer (null)
    [Fact]
    public void A5_RunInactive_NullAuthoredAnswer_ReturnsNull()
    {
        // Given
        var ctx = new YesNoContext(RunActive: false, AuthoredAnswer: null);

        // When
        var result = SelectYesnoDecider.Decide(ctx);

        // Then
        Assert.Null(result);
    }

    // =========================================================================
    // Group B — ExtractYesNo / QuestEngine.CurrentYesNoAnswer (answer source)
    //
    // Strategy: drive QuestEngine so a step becomes the active/_lastResolvedStep
    // by calling StartQuest then Tick once (the step is not confirmed/skipped,
    // so ResolveActionForStep fires and _lastResolvedStep = step). Then read
    // CurrentYesNoAnswer. NPC position == player position so Interact is emitted
    // (not Navigate), keeping the setup minimal.
    // =========================================================================

    // B6 — TalkStep with yesno/yes → Yes
    [Fact]
    public async Task B6_TalkStep_YesnoYes_CurrentAnswerIsYes()
    {
        // Given
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99001), 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new TalkStep
        {
            Id = "b6-talk",
            Target = new NpcLocation(NpcId: 1000001, Zone: 130, Position: new Position3(0f, 0f, 0f)),
            DialogueChoices = [new DialogueChoice("yesno", Answer: "yes")]
        };
        harness.Engine.StartQuest(BuildSingleStepQuest(99001, 0, step));

        // When — tick once so _lastResolvedStep = step
        await harness.Engine.Tick(CancellationToken.None);

        // Then
        Assert.Equal(YesNoAnswer.Yes, harness.Engine.CurrentYesNoAnswer);
    }

    // B7 — TalkStep with yesno/no → No
    [Fact]
    public async Task B7_TalkStep_YesnoNo_CurrentAnswerIsNo()
    {
        // Given
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99002), 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new TalkStep
        {
            Id = "b7-talk",
            Target = new NpcLocation(NpcId: 1000002, Zone: 130, Position: new Position3(0f, 0f, 0f)),
            DialogueChoices = [new DialogueChoice("yesno", Answer: "no")]
        };
        harness.Engine.StartQuest(BuildSingleStepQuest(99002, 0, step));

        // When
        await harness.Engine.Tick(CancellationToken.None);

        // Then
        Assert.Equal(YesNoAnswer.No, harness.Engine.CurrentYesNoAnswer);
    }

    // B8 — Answer casing is ignored: "No" (capital N) → No; Type "YESNO" → still recognized
    [Theory]
    [InlineData("No")]
    [InlineData("NO")]
    public async Task B8_TalkStep_YesnoCasingVariants_AnswerNo_IsNo(string answerText)
    {
        // Given
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99003), 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new TalkStep
        {
            Id = "b8-talk",
            Target = new NpcLocation(NpcId: 1000003, Zone: 130, Position: new Position3(0f, 0f, 0f)),
            DialogueChoices = [new DialogueChoice("yesno", Answer: answerText)]
        };
        harness.Engine.StartQuest(BuildSingleStepQuest(99003, 0, step));

        // When
        await harness.Engine.Tick(CancellationToken.None);

        // Then: answer comparison is case-insensitive; "No"/"NO" all map to YesNoAnswer.No
        Assert.Equal(YesNoAnswer.No, harness.Engine.CurrentYesNoAnswer);
    }

    [Theory]
    [InlineData("Yes")]
    [InlineData("YES")]
    public async Task B8_TalkStep_YesnoCasingVariants_AnswerYes_IsYes(string answerText)
    {
        // Given
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99004), 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new TalkStep
        {
            Id = "b8b-talk",
            Target = new NpcLocation(NpcId: 1000004, Zone: 130, Position: new Position3(0f, 0f, 0f)),
            DialogueChoices = [new DialogueChoice("yesno", Answer: answerText)]
        };
        harness.Engine.StartQuest(BuildSingleStepQuest(99004, 0, step));

        // When
        await harness.Engine.Tick(CancellationToken.None);

        // Then
        Assert.Equal(YesNoAnswer.Yes, harness.Engine.CurrentYesNoAnswer);
    }

    [Fact]
    public async Task B8_TalkStep_TypeCasingYESNO_IsRecognized()
    {
        // Given: Type field is "YESNO" (uppercase) — OrdinalIgnoreCase on Type field
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99005), 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new TalkStep
        {
            Id = "b8c-talk",
            Target = new NpcLocation(NpcId: 1000005, Zone: 130, Position: new Position3(0f, 0f, 0f)),
            DialogueChoices = [new DialogueChoice("YESNO", Answer: "no")]
        };
        harness.Engine.StartQuest(BuildSingleStepQuest(99005, 0, step));

        // When
        await harness.Engine.Tick(CancellationToken.None);

        // Then: "YESNO" type is recognized case-insensitively → No
        Assert.Equal(YesNoAnswer.No, harness.Engine.CurrentYesNoAnswer);
    }

    // B9 — TalkStep with only a list choice → null (responder will default to Yes via Decide)
    [Fact]
    public async Task B9_TalkStep_OnlyListChoice_CurrentAnswerIsNull()
    {
        // Given
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99006), 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new TalkStep
        {
            Id = "b9-talk",
            Target = new NpcLocation(NpcId: 1000006, Zone: 130, Position: new Position3(0f, 0f, 0f)),
            DialogueChoices = [new DialogueChoice("list", Answer: "0")]
        };
        harness.Engine.StartQuest(BuildSingleStepQuest(99006, 0, step));

        // When
        await harness.Engine.Tick(CancellationToken.None);

        // Then: no yesno choice authored → null (caller defaults to Yes)
        Assert.Null(harness.Engine.CurrentYesNoAnswer);
    }

    // B10 — TurnInStep with yesno/yes → Yes
    [Fact]
    public async Task B10_TurnInStep_YesnoYes_CurrentAnswerIsYes()
    {
        // Given
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99007), 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new TurnInStep
        {
            Id = "b10-turnin",
            Target = new NpcLocation(NpcId: 1000007, Zone: 130, Position: new Position3(0f, 0f, 0f)),
            DialogueChoices = [new DialogueChoice("yesno", Answer: "yes")]
        };
        harness.Engine.StartQuest(BuildSingleStepQuest(99007, 0, step));

        // When
        await harness.Engine.Tick(CancellationToken.None);

        // Then: TurnInStep.DialogueChoices is honored by ExtractYesNo
        Assert.Equal(YesNoAnswer.Yes, harness.Engine.CurrentYesNoAnswer);
    }

    // B11 — TravelStep with RouteHint.NpcDialogue yesno/no → No
    [Fact]
    public async Task B11_TravelStep_NpcDialogue_YesnoNo_CurrentAnswerIsNo()
    {
        // Given: TravelStep with an NpcDialogue RouteHint carrying a yesno choice
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99008), 0);
        // Place player at NPC position so Interact is emitted (not Navigate)
        harness.GameState.SetPosition(new WorldPosition(5f, 0f, 5f));

        var step = new TravelStep
        {
            Id = "b11-travel",
            Destination = new TravelDestination(Zone: 131, Position: new Position3(100f, 0f, 100f)),
            RouteHint = new RouteHint(NpcDialogue: new NpcDialogueHint(
                target: new NpcLocation(NpcId: 1000008, Zone: 130, Position: new Position3(5f, 0f, 5f)),
                dialogueChoices: [new DialogueChoice("yesno", Answer: "no")]))
        };
        harness.Engine.StartQuest(BuildSingleStepQuest(99008, 0, step));

        // When
        await harness.Engine.Tick(CancellationToken.None);

        // Then: TravelStep.RouteHint.NpcDialogue.DialogueChoices is checked by ExtractYesNo
        Assert.Equal(YesNoAnswer.No, harness.Engine.CurrentYesNoAnswer);
    }

    // B12 — TalkStep with no DialogueChoices → null
    [Fact]
    public async Task B12_TalkStep_EmptyChoices_CurrentAnswerIsNull()
    {
        // Given
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99009), 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new TalkStep
        {
            Id = "b12-talk",
            Target = new NpcLocation(NpcId: 1000009, Zone: 130, Position: new Position3(0f, 0f, 0f)),
            DialogueChoices = []
        };
        harness.Engine.StartQuest(BuildSingleStepQuest(99009, 0, step));

        // When
        await harness.Engine.Tick(CancellationToken.None);

        // Then
        Assert.Null(harness.Engine.CurrentYesNoAnswer);
    }

    // B13 — Non-dialogue step (CombatStep) → null
    // Note: CombatStep dispatch path is async (controller.Decide). We use a simpler non-dialogue
    // step type (AcceptStep) to avoid wiring the full combat controller. The spec says "e.g. CombatStep"
    // — any non-dialogue step must return null. AcceptStep is the simplest concrete non-TalkStep
    // non-TurnInStep non-TravelStep type that dispatches via ResolveActionForStep.
    [Fact]
    public async Task B13_NonDialogueStep_CurrentAnswerIsNull()
    {
        // Given: AcceptStep carries no DialogueChoices — ExtractYesNo should return null for it
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99010), 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new AcceptStep
        {
            Id = "b13-accept",
            Target = new NpcLocation(NpcId: 1000010, Zone: 130, Position: new Position3(0f, 0f, 0f))
        };
        harness.Engine.StartQuest(BuildSingleStepQuest(99010, 0, step));

        // When
        await harness.Engine.Tick(CancellationToken.None);

        // Then: AcceptStep has no yesno choices → null
        Assert.Null(harness.Engine.CurrentYesNoAnswer);
    }

    // B14 — No active step (before first Tick / before StartQuest) → null
    [Fact]
    public void B14_BeforeAnyTick_CurrentAnswerIsNull()
    {
        // Given: engine created, StartQuest called, but Tick never called
        // (_lastResolvedStep is null → CurrentYesNoAnswer must be null)
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99011), 0);

        var step = new TalkStep
        {
            Id = "b14-talk",
            Target = new NpcLocation(NpcId: 1000011, Zone: 130, Position: new Position3(0f, 0f, 0f)),
            DialogueChoices = [new DialogueChoice("yesno", Answer: "yes")]
        };
        harness.Engine.StartQuest(BuildSingleStepQuest(99011, 0, step));

        // When: no Tick called
        // Then: no _lastResolvedStep yet → null
        Assert.Null(harness.Engine.CurrentYesNoAnswer);
    }

    // B14b — No StartQuest at all → null
    [Fact]
    public void B14b_NoQuestLoaded_CurrentAnswerIsNull()
    {
        // Given: fresh engine, no quest loaded, no tick
        var harness = new EngineTestHarness();

        // When / Then
        Assert.Null(harness.Engine.CurrentYesNoAnswer);
    }

    // B15 — First yesno wins when a step mixes list then yesno
    [Fact]
    public async Task B15_MixedChoices_ListBeforeYesno_FirstYesnoWins()
    {
        // Given: choices = [{list/"0"}, {yesno/"no"}]
        // The list choice is skipped by ExtractYesNo; the yesno is found at index 1
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99012), 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new TalkStep
        {
            Id = "b15-talk",
            Target = new NpcLocation(NpcId: 1000012, Zone: 130, Position: new Position3(0f, 0f, 0f)),
            DialogueChoices =
            [
                new DialogueChoice("list", Answer: "0"),
                new DialogueChoice("yesno", Answer: "no")
            ]
        };
        harness.Engine.StartQuest(BuildSingleStepQuest(99012, 0, step));

        // When
        await harness.Engine.Tick(CancellationToken.None);

        // Then: list entry is ignored; first yesno ("no") resolves
        Assert.Equal(YesNoAnswer.No, harness.Engine.CurrentYesNoAnswer);
    }

    // B15b — First yesno wins when there are two yesno choices
    [Fact]
    public async Task B15b_TwoYesnoChoices_FirstWins()
    {
        // Given: choices = [{yesno/"yes"}, {yesno/"no"}]
        // First yesno wins → Yes
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99013), 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new TalkStep
        {
            Id = "b15b-talk",
            Target = new NpcLocation(NpcId: 1000013, Zone: 130, Position: new Position3(0f, 0f, 0f)),
            DialogueChoices =
            [
                new DialogueChoice("yesno", Answer: "yes"),
                new DialogueChoice("yesno", Answer: "no")
            ]
        };
        harness.Engine.StartQuest(BuildSingleStepQuest(99013, 0, step));

        // When
        await harness.Engine.Tick(CancellationToken.None);

        // Then: first yesno ("yes") wins; second is ignored
        Assert.Equal(YesNoAnswer.Yes, harness.Engine.CurrentYesNoAnswer);
    }

    // =========================================================================
    // Group C — end-to-end pure composition (Decider over extracted answer)
    // =========================================================================

    // C16 — Active run + step authors No → Decide returns No
    [Fact]
    public async Task C16_RunActive_StepAuthorsNo_DecideReturnsNo()
    {
        // Given: TurnInStep authors yesno/no; run is active
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99014), 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new TurnInStep
        {
            Id = "c16-turnin",
            Target = new NpcLocation(NpcId: 1000014, Zone: 130, Position: new Position3(0f, 0f, 0f)),
            DialogueChoices = [new DialogueChoice("yesno", Answer: "no")]
        };
        harness.Engine.StartQuest(BuildSingleStepQuest(99014, 0, step));

        // When: tick so _lastResolvedStep is set
        await harness.Engine.Tick(CancellationToken.None);

        var authoredAnswer = harness.Engine.CurrentYesNoAnswer;   // No
        var decision = SelectYesnoDecider.Decide(new YesNoContext(RunActive: true, authoredAnswer));

        // Then: composed result is No
        Assert.Equal(YesNoAnswer.No, decision);
    }

    // C17 — Active run + step authors nothing → Decide returns Yes (the quest-65847 default case)
    [Fact]
    public async Task C17_RunActive_StepAuthorsNoYesno_DecideReturnsYes()
    {
        // Given: TalkStep with no yesno choice; run is active
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99015), 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new TalkStep
        {
            Id = "c17-talk",
            Target = new NpcLocation(NpcId: 1000015, Zone: 130, Position: new Position3(0f, 0f, 0f)),
            DialogueChoices = []
        };
        harness.Engine.StartQuest(BuildSingleStepQuest(99015, 0, step));

        // When: tick so _lastResolvedStep is set; CurrentYesNoAnswer will be null
        await harness.Engine.Tick(CancellationToken.None);

        var authoredAnswer = harness.Engine.CurrentYesNoAnswer;   // null
        var decision = SelectYesnoDecider.Decide(new YesNoContext(RunActive: true, authoredAnswer));

        // Then: null authored answer + active run → default Yes
        // This is the quest-65847 case: "Are you prepared for carnage?" has no authored answer → Yes
        Assert.Equal(YesNoAnswer.Yes, decision);
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "SelectYesno Test Quest",
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
