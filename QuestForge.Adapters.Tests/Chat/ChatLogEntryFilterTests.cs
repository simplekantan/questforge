using QuestForge.Adapters.Chat;

// TODO: ChatLogEntryFilter — Builder must create QuestForge.Adapters/Chat/ChatLogEntryFilter.cs
//       with static bool IsPlayerSayMessage(int sourceKind, int chatType,
//       ulong entryContentId, ulong localPlayerContentId).

namespace QuestForge.Adapters.Tests.Chat;

/// <summary>
/// RED PHASE: Tests for ChatLogEntryFilter.IsPlayerSayMessage (spec SCI-F1..F6 / Decision SCI2).
/// ALL tests in this file will fail to compile until Builder adds:
///   - QuestForge.Adapters/Chat/ChatLogEntryFilter.cs   (Task SCI-T-FILTER)
///     with IsPlayerSayMessage(int, int, ulong, ulong) → bool.
///
/// Run with: dotnet test QuestForge.Adapters.Tests --filter "FullyQualifiedName~ChatLogEntryFilterTests"
/// </summary>
public sealed class ChatLogEntryFilterTests
{
    // =========================================================================
    // SCI-F1 — Happy path: player's own /say matches → true
    // =========================================================================

    [Fact]
    public void IsPlayerSayMessage_PlayerOwnSay_ReturnsTrue_SCI_F1()
    {
        // CONTRACT: Given sourceKind=1 (EntityRelationKind.LocalPlayer),
        //                  chatType=10 (XivChatType.Say),
        //                  entryContentId=12345UL, localPlayerContentId=12345UL (same),
        //           When IsPlayerSayMessage is called,
        //           Then returns true.
        //
        // Pins: all four guards pass — the common authoring path.

        // TODO: ChatLogEntryFilter not yet implemented.
        Assert.True(ChatLogEntryFilter.IsPlayerSayMessage(
            sourceKind:           1,
            chatType:             10,
            entryContentId:       12345UL,
            localPlayerContentId: 12345UL));
    }

    // =========================================================================
    // SCI-F2 — NPC chat (sourceKind != 1) → false
    // =========================================================================

    [Fact]
    public void IsPlayerSayMessage_FriendlyNpc_ReturnsFalse_SCI_F2()
    {
        // CONTRACT: Given sourceKind=7 (EntityRelationKind.FriendlyNpc),
        //                  chatType=10 (XivChatType.Say — NPCs speak in Say-like channels),
        //                  entryContentId=0UL (NPCs have no ContentId),
        //           When IsPlayerSayMessage is called,
        //           Then returns false.
        //
        // Pins: SourceKind guard — NPC /say from quest scripts must NOT fire inference.

        // TODO: ChatLogEntryFilter not yet implemented.
        Assert.False(ChatLogEntryFilter.IsPlayerSayMessage(
            sourceKind:           7,
            chatType:             10,
            entryContentId:       0UL,
            localPlayerContentId: 12345UL));
    }

    // =========================================================================
    // SCI-F3 — Player /yell (chatType != 10) → false
    // =========================================================================

    [Fact]
    public void IsPlayerSayMessage_PlayerYell_ReturnsFalse_SCI_F3()
    {
        // CONTRACT: Given sourceKind=1 (LocalPlayer), chatType=11 (XivChatType.Yell),
        //                  entryContentId == localPlayerContentId (same player),
        //           When IsPlayerSayMessage is called,
        //           Then returns false.
        //
        // Pins: ChatType guard — only /say (10) triggers inference (Decision SC2).

        // TODO: ChatLogEntryFilter not yet implemented.
        Assert.False(ChatLogEntryFilter.IsPlayerSayMessage(
            sourceKind:           1,
            chatType:             11,
            entryContentId:       12345UL,
            localPlayerContentId: 12345UL));
    }

    // =========================================================================
    // SCI-F4 — Another player's /say in zone (ContentId mismatch) → false
    // =========================================================================

    [Fact]
    public void IsPlayerSayMessage_OtherPlayerSay_ReturnsFalse_SCI_F4()
    {
        // CONTRACT: Given sourceKind=1, chatType=10, but entryContentId=99999UL != 12345UL,
        //           When IsPlayerSayMessage is called,
        //           Then returns false.
        //
        // Pins: ContentId guard — most important filter: other players' /say in the
        //       zone must not fire inference. The signal source records other players'
        //       /say with their own ContentId.

        // TODO: ChatLogEntryFilter not yet implemented.
        Assert.False(ChatLogEntryFilter.IsPlayerSayMessage(
            sourceKind:           1,
            chatType:             10,
            entryContentId:       99999UL,
            localPlayerContentId: 12345UL));
    }

    // =========================================================================
    // SCI-F5 — localPlayerContentId == 0 (early-init / character-select) → false
    // =========================================================================

    [Fact]
    public void IsPlayerSayMessage_ZeroLocalContentId_ReturnsFalse_SCI_F5()
    {
        // CONTRACT: Given sourceKind=1, chatType=10, entryContentId=12345UL,
        //                  localPlayerContentId=0UL (InfoModule not yet populated),
        //           When IsPlayerSayMessage is called,
        //           Then returns false.
        //
        // Pins: defensive guard from Decision SCI2 — unknown local player must never match
        //       so that every chat line is NOT attributed to the player during early init.

        // TODO: ChatLogEntryFilter not yet implemented.
        Assert.False(ChatLogEntryFilter.IsPlayerSayMessage(
            sourceKind:           1,
            chatType:             10,
            entryContentId:       12345UL,
            localPlayerContentId: 0UL));
    }

    // =========================================================================
    // SCI-F6 — Theory: all non-Say chat types return false (even for local player)
    // =========================================================================

    [Theory]
    [InlineData(0)]    // None / unknown
    [InlineData(11)]   // Yell
    [InlineData(12)]   // Shout
    [InlineData(13)]   // Party
    [InlineData(14)]   // Alliance
    [InlineData(33)]   // FreeCompany
    [InlineData(56)]   // NoviceNetwork
    public void IsPlayerSayMessage_NonSayChatTypes_ReturnFalse_SCI_F6(int chatType)
    {
        // CONTRACT: Given sourceKind=1, entryContentId == localPlayerContentId (same player),
        //                  chatType != 10 (any non-Say channel),
        //           When IsPlayerSayMessage is called,
        //           Then returns false.
        //
        // Pins: ChatType guard is exhaustive for all non-Say channels the local player
        //       can use. Only chatType==10 is the trigger.

        // TODO: ChatLogEntryFilter not yet implemented.
        Assert.False(ChatLogEntryFilter.IsPlayerSayMessage(
            sourceKind:           1,
            chatType:             chatType,
            entryContentId:       12345UL,
            localPlayerContentId: 12345UL));
    }
}
