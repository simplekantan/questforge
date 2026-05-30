namespace QuestForge.Adapters.Chat;

public static class ChatLogEntryFilter
{
    public const int SourceKindLocalPlayer = 1;
    public const int ChatTypeSay = 10;

    public static bool IsPlayerSayMessage(
        int sourceKind,
        int chatType,
        ulong entryContentId,
        ulong localPlayerContentId)
    {
        if (localPlayerContentId == 0UL) return false;
        if (sourceKind != SourceKindLocalPlayer) return false;
        if (chatType != ChatTypeSay) return false;
        if (entryContentId != localPlayerContentId) return false;
        return true;
    }
}
