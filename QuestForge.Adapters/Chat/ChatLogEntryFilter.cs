namespace QuestForge.Adapters.Chat;

/// <summary>
/// Pure-logic filter for identifying the local player's own /say messages in the chat log.
/// </summary>
/// <remarks>
/// In-game verification (slice 5 smoke) revealed that:
/// <list type="bullet">
///   <item><c>LogInfo.SourceKind</c> is uniformly 0 for every chat entry — not useful for identification.</item>
///   <item>The player's own outbound messages do NOT appear in <c>RaptureLogModule.MsgSourceArray</c>
///         (which only tracks inbound messages from other characters). So filtering by ContentId
///         against that array misses player-spoken messages entirely.</item>
///   <item>The reliable signal is <c>LogInfo.LogKind</c> from the full log range (10 = Say channel)
///         combined with a sender-name match against the local player's display name.</item>
/// </list>
/// </remarks>
public static class ChatLogEntryFilter
{
    /// <summary>LogInfo.LogKind value for the /say chat channel.</summary>
    public const int LogKindSay = 10;

    /// <summary>
    /// Returns true when the given chat log entry is the local player's own /say message.
    /// Requires both the channel match (Say) and an exact sender-name match against
    /// the local player's display name.
    /// </summary>
    public static bool IsPlayerSayMessage(int logKind, string? senderName, string? localPlayerName)
    {
        if (logKind != LogKindSay) return false;
        if (string.IsNullOrEmpty(localPlayerName)) return false;
        if (string.IsNullOrEmpty(senderName)) return false;
        return string.Equals(senderName, localPlayerName, System.StringComparison.Ordinal);
    }
}
