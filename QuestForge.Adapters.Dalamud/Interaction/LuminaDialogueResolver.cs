using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Interaction;

public sealed class LuminaDialogueResolver : IDialogueResolver
{
    private readonly PluginServices _svc;

    public LuminaDialogueResolver(PluginServices svc) => _svc = svc;

    public Task<Result<string>> ResolveText(string sheetReference, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<DialogueOptionId?>> FindOptionByText(string text, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<bool>> CurrentDialogueMatches(string sheetReference, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<GameLanguage>> GetActiveLanguage(CancellationToken ct)
        => throw new NotImplementedException();
}