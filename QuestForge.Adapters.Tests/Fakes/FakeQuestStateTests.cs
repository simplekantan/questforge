using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Tests.Fakes;

public class FakeQuestStateTests
{
    private static readonly QuestId TestQuest = new QuestId(1001);

    [Fact]
    public async Task FreshState_GetQuestStatus_ReturnsUnknown()
    {
        var state = new FakeQuestState();
        var result = await state.GetQuestStatus(TestQuest, CancellationToken.None);
        Assert.Equal(QuestStatus.Unknown, result.ValueOrThrow);
    }

    [Fact]
    public async Task SetQuestStatus_GetQuestStatus_RoundTrips()
    {
        var state = new FakeQuestState();
        state.SetQuestStatus(TestQuest, QuestStatus.Accepted);
        var result = await state.GetQuestStatus(TestQuest, CancellationToken.None);
        Assert.Equal(QuestStatus.Accepted, result.ValueOrThrow);
    }

    [Fact]
    public async Task SetQuestSequence_GetQuestSequence_RoundTrips()
    {
        var state = new FakeQuestState();
        state.SetQuestSequence(TestQuest, 3);
        var result = await state.GetQuestSequence(TestQuest, CancellationToken.None);
        Assert.Equal(3, result.ValueOrThrow);
    }

    [Fact]
    public async Task SetQuestFlagBit_Bit2_IsQuestFlagSet_ReturnsTrueForBit2FalseForBit1()
    {
        var state = new FakeQuestState();
        state.SetQuestFlagBit(TestQuest, 2, true);

        var bit2 = await state.IsQuestFlagSet(TestQuest, 2, CancellationToken.None);
        var bit1 = await state.IsQuestFlagSet(TestQuest, 1, CancellationToken.None);

        Assert.True(bit2.ValueOrThrow);
        Assert.False(bit1.ValueOrThrow);
    }

    [Fact]
    public async Task AddAcceptedQuest_IsQuestAccepted_ReturnsTrue()
    {
        var state = new FakeQuestState();
        state.AddAcceptedQuest(TestQuest);
        var result = await state.IsQuestAccepted(TestQuest, CancellationToken.None);
        Assert.True(result.ValueOrThrow);
    }

    [Fact]
    public async Task AddAcceptedQuest_AppearsInGetAcceptedQuests()
    {
        var state = new FakeQuestState();
        state.AddAcceptedQuest(TestQuest);
        var result = await state.GetAcceptedQuests(CancellationToken.None);
        Assert.Contains(TestQuest, result.ValueOrThrow);
    }

    [Fact]
    public async Task RemoveAcceptedQuest_IsQuestAccepted_ReturnsFalse()
    {
        var state = new FakeQuestState();
        state.AddAcceptedQuest(TestQuest);
        state.RemoveAcceptedQuest(TestQuest);
        var result = await state.IsQuestAccepted(TestQuest, CancellationToken.None);
        Assert.False(result.ValueOrThrow);
    }

    [Fact]
    public async Task IsQuestComplete_ReflectsStatusComplete()
    {
        var state = new FakeQuestState();
        state.SetQuestStatus(TestQuest, QuestStatus.Complete);
        var result = await state.IsQuestComplete(TestQuest, CancellationToken.None);
        Assert.True(result.ValueOrThrow);
    }

    [Fact]
    public async Task IsQuestComplete_StatusNotComplete_ReturnsFalse()
    {
        var state = new FakeQuestState();
        state.SetQuestStatus(TestQuest, QuestStatus.Accepted);
        var result = await state.IsQuestComplete(TestQuest, CancellationToken.None);
        Assert.False(result.ValueOrThrow);
    }

    [Fact]
    public async Task PreCancelledToken_ThrowsOperationCanceledException_AndNoReadsRecorded()
    {
        var state = new FakeQuestState();
        var ct = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(() => state.GetQuestStatus(TestQuest, ct));
        Assert.Equal(0, state.RecordedReads.Count);
    }
}