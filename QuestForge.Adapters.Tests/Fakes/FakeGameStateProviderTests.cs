using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Tests.Fakes;

public class FakeGameStateProviderTests
{
    [Fact]
    public async Task FreshProvider_GetPlayerZone_ReturnsZoneIdZero()
    {
        var state = new FakeGameStateProvider();
        var result = await state.GetPlayerZone(CancellationToken.None);
        Assert.Equal(new ZoneId(0), result.ValueOrThrow);
    }

    [Fact]
    public async Task SetZone_GetPlayerZone_ReturnsSetZone()
    {
        var state = new FakeGameStateProvider();
        state.SetZone(new ZoneId(132));
        var result = await state.GetPlayerZone(CancellationToken.None);
        Assert.Equal(new ZoneId(132), result.ValueOrThrow);
    }

    [Fact]
    public async Task SetPosition_GetPlayerPosition_ReturnsSetPosition()
    {
        var state = new FakeGameStateProvider();
        state.SetPosition(new WorldPosition(1f, 2f, 3f));
        var result = await state.GetPlayerPosition(CancellationToken.None);
        Assert.Equal(new WorldPosition(1f, 2f, 3f), result.ValueOrThrow);
    }

    [Fact]
    public async Task AddNpc_FindNpc_ReturnsNpc()
    {
        var state = new FakeGameStateProvider();
        var npcId = new NpcId(100);
        var npc = new NpcReference(npcId, new WorldPosition(0, 0, 0), 5f);
        state.AddNpc(npc);

        var result = await state.FindNpc(npcId, CancellationToken.None);
        Assert.NotNull(result.ValueOrThrow);
        Assert.Equal(npc, result.ValueOrThrow);
    }

    [Fact]
    public async Task AddNpc_FindUnknownNpc_ReturnsOkNull()
    {
        var state = new FakeGameStateProvider();
        var npc = new NpcReference(new NpcId(100), new WorldPosition(0, 0, 0), 5f);
        state.AddNpc(npc);

        var result = await state.FindNpc(new NpcId(999), CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Null(result.ValueOrThrow);
    }

    [Fact]
    public async Task AddNpc_GetNearbyNpcs_LargeRadius_IncludesNpc()
    {
        var state = new FakeGameStateProvider();
        var npc = new NpcReference(new NpcId(1), new WorldPosition(0, 0, 0), 5f);
        state.AddNpc(npc);

        var result = await state.GetNearbyNpcs(10f, CancellationToken.None);
        Assert.Contains(npc, result.ValueOrThrow);
    }

    [Fact]
    public async Task AddNpc_GetNearbyNpcs_SmallRadius_ExcludesNpc()
    {
        var state = new FakeGameStateProvider();
        var npc = new NpcReference(new NpcId(1), new WorldPosition(0, 0, 0), 5f);
        state.AddNpc(npc);

        var result = await state.GetNearbyNpcs(3f, CancellationToken.None);
        Assert.DoesNotContain(npc, result.ValueOrThrow);
    }

    [Fact]
    public async Task PreCancelledToken_ThrowsOperationCanceledException_AndNoReadsRecorded()
    {
        var state = new FakeGameStateProvider();
        var ct = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(() => state.GetPlayerZone(ct));
        Assert.Equal(0, state.RecordedReads.Count);
    }

    [Fact]
    public async Task Reset_ClearsRecordedReads_ButPreservesPosition()
    {
        var state = new FakeGameStateProvider();
        state.SetPosition(new WorldPosition(5f, 10f, 15f));
        await state.GetPlayerPosition(CancellationToken.None); // records 1 read

        state.Reset();

        Assert.Equal(0, state.RecordedReads.Count);
        // Position survives reset
        var pos = await state.GetPlayerPosition(CancellationToken.None);
        Assert.Equal(new WorldPosition(5f, 10f, 15f), pos.ValueOrThrow);
    }

    [Fact]
    public async Task AddInteractable_IsInteractableActive_ReturnsTrueByDefault()
    {
        var state = new FakeGameStateProvider();
        var id = new InteractableId(42);
        var interactable = new InteractableReference(id, new WorldPosition(0, 0, 0), 2f, InteractableKind.Other);
        state.AddInteractable(interactable);

        var result = await state.IsInteractableActive(id, CancellationToken.None);
        Assert.True(result.ValueOrThrow);
    }

    [Fact]
    public async Task SetInteractableActive_False_IsInteractableActive_ReturnsFalse()
    {
        var state = new FakeGameStateProvider();
        var id = new InteractableId(42);
        var interactable = new InteractableReference(id, new WorldPosition(0, 0, 0), 2f, InteractableKind.Other);
        state.AddInteractable(interactable);

        state.SetInteractableActive(id, false);

        var result = await state.IsInteractableActive(id, CancellationToken.None);
        Assert.False(result.ValueOrThrow);
    }

    [Fact]
    public async Task SetAetheryteAttuned_True_IsAetheryteAttuned_ReturnsTrue()
    {
        var state = new FakeGameStateProvider();
        var aetheryteId = new AetheryteId(8);
        state.SetAetheryteAttuned(aetheryteId, true);

        var result = await state.IsAetheryteAttuned(aetheryteId, CancellationToken.None);
        Assert.True(result.ValueOrThrow);
    }

    [Fact]
    public async Task GetItemCount_UnknownItem_ReturnsZeroNotFailure()
    {
        var state = new FakeGameStateProvider();
        var unknownItem = new ItemId(99999);

        var result = await state.GetItemCount(unknownItem, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.ValueOrThrow);
    }
}