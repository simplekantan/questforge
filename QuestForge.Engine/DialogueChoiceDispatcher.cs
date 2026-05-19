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
        DialogueChoice[] choices = currentStep switch
        {
            TalkStep talk => talk.DialogueChoices,
            TravelStep travel => travel.RouteHint?.NpcDialogue?.DialogueChoices ?? Array.Empty<DialogueChoice>(),
            _ => Array.Empty<DialogueChoice>()
        };
        if (choices.Length == 0 || progress >= choices.Length) return false;
        if (!selectIconStringOpen && !selectStringOpen) return false;

        var choice = choices[progress];
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
