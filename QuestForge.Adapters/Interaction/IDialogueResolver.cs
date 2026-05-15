using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Interaction;

public interface IDialogueResolver
{
    Task<Result<string>> ResolveText(string sheetReference, CancellationToken ct);

    Task<Result<DialogueOptionId?>> FindOptionByText(string text, CancellationToken ct);

    Task<Result<bool>> CurrentDialogueMatches(string sheetReference, CancellationToken ct);

    Task<Result<GameLanguage>> GetActiveLanguage(CancellationToken ct);
}

public enum GameLanguage { English, German, French, Japanese }