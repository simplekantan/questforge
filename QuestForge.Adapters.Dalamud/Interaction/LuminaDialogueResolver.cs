using Dalamud.Game;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Interaction;

// GetActiveLanguage reads ClientState; all other methods are Phase 6 placeholders.
// Quest 66130 has no SelectString choices or Lumina sheet resolution needs.
public sealed class LuminaDialogueResolver : IDialogueResolver
{
    private readonly PluginServices _svc;

    public LuminaDialogueResolver(PluginServices svc) => _svc = svc;

    public Task<Result<GameLanguage>> GetActiveLanguage(CancellationToken ct)
    {
        var lang = _svc.ClientState.ClientLanguage switch
        {
            ClientLanguage.Japanese => GameLanguage.Japanese,
            ClientLanguage.German   => GameLanguage.German,
            ClientLanguage.French   => GameLanguage.French,
            _                       => GameLanguage.English
        };
        return Task.FromResult<Result<GameLanguage>>(Result.Ok(lang));
    }

    public Task<Result<string>> ResolveText(string sheetReference, CancellationToken ct)
        => Task.FromResult<Result<string>>(
            Result.Fail<string>("notImplemented", "Phase 6 placeholder"));

    public Task<Result<DialogueOptionId?>> FindOptionByText(string text, CancellationToken ct)
        => Task.FromResult<Result<DialogueOptionId?>>(Result.Ok<DialogueOptionId?>(null));

    public Task<Result<bool>> CurrentDialogueMatches(string sheetReference, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));
}
