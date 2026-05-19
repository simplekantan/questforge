using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Types;
using QuestForge.Schema;

namespace QuestForge.Engine;

public static class DialogueChoiceDispatcher
{
    public static bool TryDispatch(
        Step? currentStep,
        bool selectIconStringOpen,
        bool selectStringOpen,
        ref int progress,
        IInteractor interactor,
        CancellationToken ct)
    {
        if (currentStep is not TalkStep talkStep) return false;
        if (progress >= talkStep.DialogueChoices.Length) return false;
        if (!selectIconStringOpen && !selectStringOpen) return false;

        var choice = talkStep.DialogueChoices[progress];
        // "yesno" and other non-list types are handled by AdvanceDialogue's SelectYesno auto-confirm;
        // returning false lets the caller fall through to AdvanceDialogue rather than stalling here.
        if (choice.Type != "list") return false;

        if (!int.TryParse(choice.Answer, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var idx) || idx < 0)
            return false;

        interactor.SelectStringOption(idx, ct).GetAwaiter().GetResult();
        progress++;
        return true;
    }
}
