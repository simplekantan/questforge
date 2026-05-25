namespace QuestForge.Plugin.Tracing.Authoring;

/// <summary>
/// Pure static helper for the Interaction panel's quest-filter truth table (§3.6).
/// Builder: implement ShouldShow per ACCEPTABLE_NOW_PLAN §3.6.
///
/// Filter ordering:
///   (1) if isComplete and !showCompleted → hide (return false)
///   (2) if !isComplete and !isAcceptableNow and !showUnacceptable → hide (return false)
///   else → show (return true)
/// </summary>
public static class InteractionPanelFilter
{
    /// <summary>
    /// Returns true if the quest should appear in the Interaction panel given the current flag state.
    /// </summary>
    /// <param name="isComplete">Whether the quest is already completed.</param>
    /// <param name="isAcceptableNow">Whether the player can accept the quest right now (IsAcceptableNow result).</param>
    /// <param name="showCompleted">Value of ShowCompletedQuestsInAuthorPanel config flag.</param>
    /// <param name="showUnacceptable">Value of ShowUnacceptableQuestsInAuthorPanel config flag.</param>
    public static bool ShouldShow(bool isComplete, bool isAcceptableNow, bool showCompleted, bool showUnacceptable)
    {
        // (1) Completed quests: governed solely by showCompleted.
        if (isComplete) return showCompleted;
        // (2) Not-complete quests: shown if acceptable-now OR the reveal toggle is on.
        return isAcceptableNow || showUnacceptable;
    }
}
