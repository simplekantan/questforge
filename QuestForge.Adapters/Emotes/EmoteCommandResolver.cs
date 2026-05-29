using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Emotes;

public static class EmoteCommandResolver
{
    public static Result<string> Resolve(uint emoteId, bool motion, Func<uint, string?> commandLookup)
    {
        var raw = commandLookup(emoteId);
        if (string.IsNullOrEmpty(raw))
            return Result.Fail<string>(
                "emoteCommandNotFound",
                $"no text command for emote {emoteId}");

        if (!raw.StartsWith('/'))
            return Result.Fail<string>(
                "emoteCommandMalformed",
                $"emote {emoteId} text command does not start with '/': '{raw}'");

        return Result.Ok(motion ? $"{raw} motion" : raw);
    }
}
