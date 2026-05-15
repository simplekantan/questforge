using Dalamud.Game;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Interaction;

// Phase 6: GetActiveLanguage is fully implemented via ClientState.
// ResolveText, FindOptionByText, and CurrentDialogueMatches are placeholders —
// quest 66130 has no SelectString dialogue choices or sheet resolution needs.
public sealed class LuminaDialogueResolver : IDialogueResolver
{
    private readonly PluginServices _svc;

    public LuminaDialogueResolver(PluginServices svc) => _svc = svc;

    // GetActiveLanguage — reads Dalamud ClientState for the active client language
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

    // ResolveText — Lumina sheet resolution deferred to Phase 9
    public Task<Result<string>> ResolveText(string sheetReference, CancellationToken ct)
        => Task.FromResult<Result<string>>(
            Result.Fail<string>("notImplemented", "Phase 6 placeholder: Lumina sheet resolution not yet wired"));

    // FindOptionByText — SelectString matching deferred to Phase 9
    public Task<Result<DialogueOptionId?>> FindOptionByText(string text, CancellationToken ct)
        => Task.FromResult<Result<DialogueOptionId?>>(Result.Ok<DialogueOptionId?>(null));

    // CurrentDialogueMatches — dialogue state checking deferred to Phase 9
    public Task<Result<bool>> CurrentDialogueMatches(string sheetReference, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));
}
