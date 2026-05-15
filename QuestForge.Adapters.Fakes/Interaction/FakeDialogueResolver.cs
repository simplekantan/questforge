using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Interaction;

public sealed class FakeDialogueResolver : IDialogueResolver
{
    private readonly Dictionary<(string Reference, GameLanguage Language), string> _texts = new();
    private readonly Dictionary<(string Reference, GameLanguage Language), DialogueOptionId> _options = new();
    private GameLanguage _activeLanguage = GameLanguage.English;

    public record ResolutionCall(string Reference, GameLanguage Language, DateTimeOffset At) : AdapterCall(At);
    public CallLog<ResolutionCall> RecordedResolutions { get; } = new();

    // ----- Scripting -----
    public void RegisterText(string sheetReference, GameLanguage language, string text) =>
        _texts[(sheetReference, language)] = text;

    public void RegisterOption(string sheetReference, GameLanguage language, DialogueOptionId optionId) =>
        _options[(sheetReference, language)] = optionId;

    public void SetActiveLanguage(GameLanguage language) => _activeLanguage = language;

    // ----- Reset -----
    // Clears recorded calls only — text/option registrations (SetX state) survive,
    // matching the plan's convention: Reset ≠ wipe game state.
    public void Reset() => RecordedResolutions.Clear();

    // ----- IDialogueResolver implementation -----

    public Task<Result<string>> ResolveText(string sheetReference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedResolutions.Add(new ResolutionCall(sheetReference, _activeLanguage, DateTimeOffset.UtcNow));

        if (_texts.TryGetValue((sheetReference, _activeLanguage), out var text))
            return Task.FromResult<Result<string>>(Result.Ok(text));

        return Task.FromResult<Result<string>>(Result.Fail<string>("sheet-reference-not-found"));
    }

    public Task<Result<DialogueOptionId?>> FindOptionByText(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedResolutions.Add(new ResolutionCall(text, _activeLanguage, DateTimeOffset.UtcNow));

        foreach (var kvp in _options)
        {
            if (kvp.Key.Language == _activeLanguage &&
                _texts.TryGetValue(kvp.Key, out var resolved) &&
                resolved == text)
            {
                DialogueOptionId? found = kvp.Value;
                return Task.FromResult<Result<DialogueOptionId?>>(Result.Ok(found));
            }
        }

        DialogueOptionId? notFound = null;
        return Task.FromResult<Result<DialogueOptionId?>>(Result.Ok(notFound));
    }

    public Task<Result<bool>> CurrentDialogueMatches(string sheetReference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedResolutions.Add(new ResolutionCall(sheetReference, _activeLanguage, DateTimeOffset.UtcNow));
        bool matches = _texts.ContainsKey((sheetReference, _activeLanguage));
        return Task.FromResult<Result<bool>>(Result.Ok(matches));
    }

    public Task<Result<GameLanguage>> GetActiveLanguage(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<GameLanguage>>(Result.Ok(_activeLanguage));
    }
}