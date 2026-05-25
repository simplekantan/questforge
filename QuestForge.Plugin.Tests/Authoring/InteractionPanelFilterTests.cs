using QuestForge.Plugin.Tracing.Authoring;
using Xunit;

namespace QuestForge.Plugin.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for the panel-filter truth table defined in ACCEPTABLE_NOW_PLAN §3.6
/// and acceptance criteria Group PANEL (PANEL1–PANEL6).
///
/// These tests target a pure static helper:
///   static bool InteractionPanelFilter.ShouldShow(
///       bool isComplete, bool isAcceptableNow,
///       bool showCompleted, bool showUnacceptable)
///
/// The Builder must create this helper in the QuestForge.Plugin.Tracing project
/// (namespace: QuestForge.Plugin.Tracing.Authoring) so it is reachable from
/// QuestForge.Plugin.Tests without requiring a Dalamud dependency.
/// The class: QuestForge.Plugin.Tracing.Authoring.InteractionPanelFilter (static class).
///
/// Truth table (§3.6):
///   showCompleted=F, showUnacceptable=F: hide not-complete+not-acceptable; show acceptable
///   showCompleted=F, showUnacceptable=T: show not-complete+not-acceptable (locked/future revealed)
///   showCompleted=T, showUnacceptable=F: show completed regardless; show acceptable; hide not-acceptable
///   showCompleted=T, showUnacceptable=T: show everything
///
/// Filter ordering (§3.6):
///   (1) if isComplete and !showCompleted → hide (return false)
///   (2) if !isComplete and !isAcceptableNow and !showUnacceptable → hide (return false)
///   else show (return true)
/// </summary>
public sealed class InteractionPanelFilterTests
{
    // Helper that formats the inputs for failure messages
    private static string FormatInputs(bool isComplete, bool isAcceptableNow, bool showCompleted, bool showUnacceptable)
        => $"isComplete={isComplete}, isAcceptableNow={isAcceptableNow}, showCompleted={showCompleted}, showUnacceptable={showUnacceptable}";

    // -------------------------------------------------------------------------
    // PANEL1: default flags (both false) hides not-acceptable, not-complete quests
    // -------------------------------------------------------------------------

    [Fact]
    public void PANEL1_DefaultFlags_NotCompleteNotAcceptable_IsHidden()
    {
        /*
         * RED: Will fail until InteractionPanelFilter.ShouldShow is created.
         *
         * CONTRACT: With both flags false, a quest that is available-but-not-acceptable
         *   (isComplete=false, isAcceptableNow=false) must be hidden (ShouldShow == false).
         *   This is the primary purpose of the acceptable-now filter.
         */

        bool result = InteractionPanelFilter.ShouldShow(
            isComplete: false,
            isAcceptableNow: false,
            showCompleted: false,
            showUnacceptable: false);

        Assert.False(result,
            FormatInputs(false, false, false, false) + " → expected hidden");
    }

    // -------------------------------------------------------------------------
    // PANEL2: default flags show acceptable quests
    // -------------------------------------------------------------------------

    [Fact]
    public void PANEL2_DefaultFlags_NotCompleteAcceptable_IsShown()
    {
        /*
         * CONTRACT: With both flags false, a quest with isAcceptableNow=true must be shown.
         */

        bool result = InteractionPanelFilter.ShouldShow(
            isComplete: false,
            isAcceptableNow: true,
            showCompleted: false,
            showUnacceptable: false);

        Assert.True(result,
            FormatInputs(false, true, false, false) + " → expected shown");
    }

    // -------------------------------------------------------------------------
    // PANEL3: reveal toggle (showUnacceptable=true) shows locked/future quests
    // -------------------------------------------------------------------------

    [Fact]
    public void PANEL3_ShowUnacceptable_NotCompleteNotAcceptable_IsShown()
    {
        /*
         * CONTRACT: With showUnacceptable=true, showCompleted=false:
         *   a not-complete, not-acceptable quest is shown (locked/future revealed).
         */

        bool result = InteractionPanelFilter.ShouldShow(
            isComplete: false,
            isAcceptableNow: false,
            showCompleted: false,
            showUnacceptable: true);

        Assert.True(result,
            FormatInputs(false, false, false, true) + " → expected shown (reveal toggle on)");
    }

    // -------------------------------------------------------------------------
    // PANEL4: completed quests governed solely by showCompleted, independent of acceptable-now
    // -------------------------------------------------------------------------

    [Fact]
    public void PANEL4_ShowCompleted_CompletedNotAcceptable_IsShown()
    {
        /*
         * CONTRACT: With showCompleted=true, showUnacceptable=false:
         *   a completed quest (isComplete=true, isAcceptableNow=false) is SHOWN.
         *   The acceptable-now filter must not drop it — completed quests passed the
         *   completed-quest gate (step 1) and must not be re-filtered by step 2.
         *
         * This pins the ordering decision in §3.6: completed-quest gate runs before
         * acceptable-now gate, and completed quests that survive step 1 skip step 2.
         */

        bool result = InteractionPanelFilter.ShouldShow(
            isComplete: true,
            isAcceptableNow: false,
            showCompleted: true,
            showUnacceptable: false);

        Assert.True(result,
            FormatInputs(true, false, true, false) + " → completed quest must show when showCompleted=true");
    }

    // -------------------------------------------------------------------------
    // PANEL5: completed quests hidden when showCompleted=false, regardless of reveal toggle
    // -------------------------------------------------------------------------

    [Fact]
    public void PANEL5_ShowCompletedFalse_ShowUnacceptableTrue_CompletedQuest_IsHidden()
    {
        /*
         * CONTRACT: With showCompleted=false, showUnacceptable=true:
         *   a completed quest is hidden. The reveal toggle reveals locked/future, NOT completed.
         *   Completed quests are governed solely by showCompleted.
         */

        bool result = InteractionPanelFilter.ShouldShow(
            isComplete: true,
            isAcceptableNow: false,
            showCompleted: false,
            showUnacceptable: true);

        Assert.False(result,
            FormatInputs(true, false, false, true) + " → completed quest must hide when showCompleted=false");
    }

    // -------------------------------------------------------------------------
    // PANEL6: additional truth-table coverage — all four combinations of flags
    //         for a not-complete, not-acceptable quest
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false, false)] // PANEL1 rephrased as theory row
    [InlineData(false, true,  true)]  // PANEL3 rephrased
    [InlineData(true,  false, false)] // showCompleted alone doesn't reveal not-acceptable
    [InlineData(true,  true,  true)]  // both flags on → show everything
    public void PANEL6_NotCompleteNotAcceptable_ShowVariations(
        bool showCompleted, bool showUnacceptable, bool expectedShown)
    {
        /*
         * CONTRACT: Truth table for not-complete, not-acceptable quest across all flag combos.
         * The showCompleted flag, when false, must NOT cause the quest to show
         * (only showUnacceptable controls this case).
         */

        bool result = InteractionPanelFilter.ShouldShow(
            isComplete: false,
            isAcceptableNow: false,
            showCompleted: showCompleted,
            showUnacceptable: showUnacceptable);

        var msg = FormatInputs(false, false, showCompleted, showUnacceptable) +
            $" → expected {(expectedShown ? "shown" : "hidden")}";
        if (expectedShown) Assert.True(result, msg);
        else Assert.False(result, msg);
    }

    // Additional check: not-complete + acceptable is always shown regardless of flags
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true,  false)]
    [InlineData(true,  true)]
    public void PANEL6_NotCompleteAcceptable_AlwaysShown(bool showCompleted, bool showUnacceptable)
    {
        bool result = InteractionPanelFilter.ShouldShow(
            isComplete: false,
            isAcceptableNow: true,
            showCompleted: showCompleted,
            showUnacceptable: showUnacceptable);

        Assert.True(result,
            FormatInputs(false, true, showCompleted, showUnacceptable) +
            " → acceptable quest must always be shown");
    }
}
