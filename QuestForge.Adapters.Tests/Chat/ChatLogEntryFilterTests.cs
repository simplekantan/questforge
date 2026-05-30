using QuestForge.Adapters.Chat;
using Xunit;

namespace QuestForge.Adapters.Tests.Chat;

/// <summary>
/// Tests for <see cref="ChatLogEntryFilter.IsPlayerSayMessage"/>.
/// In-game smoke (slice 5) showed that LogInfo.SourceKind is uniformly 0 for all chat
/// entries and the player's own messages do not appear in MsgSourceArray. The reliable
/// signal is LogKind (10 = Say) + an exact sender-name match against the local player.
/// </summary>
public sealed class ChatLogEntryFilterTests
{
    private const string LocalPlayer = "Gabriel Deleon";

    [Fact]
    public void IsPlayerSayMessage_PlayerOwnSay_ReturnsTrue_SCI_F1()
    {
        Assert.True(ChatLogEntryFilter.IsPlayerSayMessage(
            logKind: 10, senderName: LocalPlayer, localPlayerName: LocalPlayer));
    }

    [Fact]
    public void IsPlayerSayMessage_OtherPlayerSay_ReturnsFalse_SCI_F2()
    {
        Assert.False(ChatLogEntryFilter.IsPlayerSayMessage(
            logKind: 10, senderName: "Some Stranger", localPlayerName: LocalPlayer));
    }

    [Fact]
    public void IsPlayerSayMessage_PlayerYell_ReturnsFalse_SCI_F3()
    {
        // LogKind 11 is /yell, not /say
        Assert.False(ChatLogEntryFilter.IsPlayerSayMessage(
            logKind: 11, senderName: LocalPlayer, localPlayerName: LocalPlayer));
    }

    [Fact]
    public void IsPlayerSayMessage_SystemMessage_ReturnsFalse_SCI_F4()
    {
        // System messages have empty sender (e.g. "QuestForge: ...", "Looking for Party")
        Assert.False(ChatLogEntryFilter.IsPlayerSayMessage(
            logKind: 10, senderName: "", localPlayerName: LocalPlayer));
    }

    [Fact]
    public void IsPlayerSayMessage_NullLocalPlayerName_ReturnsFalse_SCI_F5()
    {
        // Defensive: probe returned null (no character loaded)
        Assert.False(ChatLogEntryFilter.IsPlayerSayMessage(
            logKind: 10, senderName: LocalPlayer, localPlayerName: null));
    }

    [Theory]
    [InlineData(0)]   // system / login messages
    [InlineData(1)]   // system info (e.g. QuestForge plugin chat output)
    [InlineData(11)]  // yell
    [InlineData(30)]  // party
    [InlineData(57)]  // novice network
    [InlineData(72)]  // recruitment / search-info
    public void IsPlayerSayMessage_NonSayLogKinds_ReturnFalse_SCI_F6(int logKind)
    {
        Assert.False(ChatLogEntryFilter.IsPlayerSayMessage(
            logKind, senderName: LocalPlayer, localPlayerName: LocalPlayer));
    }
}
