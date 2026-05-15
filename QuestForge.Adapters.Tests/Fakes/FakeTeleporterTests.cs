using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Tests.Fakes;

public class FakeTeleporterTests
{
    private static FakeTeleporter CreateTeleporter(out FakeGameStateProvider state)
    {
        state = new FakeGameStateProvider();
        return new FakeTeleporter(state);
    }

    [Fact]
    public async Task RegisterAetheryte_TeleportToAetheryte_ReturnsArrived_UpdatesZoneAndPosition()
    {
        var teleporter = CreateTeleporter(out var state);
        var aetheryteId = new AetheryteId(10);
        var targetZone = new ZoneId(132);
        var targetPos = new WorldPosition(100f, 0f, 200f);
        teleporter.RegisterAetheryte(aetheryteId, targetZone, targetPos);

        var result = await teleporter.TeleportToAetheryte(aetheryteId, CancellationToken.None);

        Assert.Equal(TeleportOutcome.Arrived, result.ValueOrThrow);
        var zone = await state.GetPlayerZone(CancellationToken.None);
        Assert.Equal(targetZone, zone.ValueOrThrow);
        var pos = await state.GetPlayerPosition(CancellationToken.None);
        Assert.Equal(targetPos, pos.ValueOrThrow);
    }

    [Fact]
    public async Task ScriptNextTeleportResult_NotAttuned_StateNotUpdated()
    {
        var teleporter = CreateTeleporter(out var state);
        var aetheryteId = new AetheryteId(10);
        var targetZone = new ZoneId(132);
        teleporter.RegisterAetheryte(aetheryteId, targetZone, new WorldPosition(0, 0, 0));
        state.SetZone(new ZoneId(1));
        teleporter.ScriptNextTeleportResult(TeleportOutcome.NotAttuned);

        var result = await teleporter.TeleportToAetheryte(aetheryteId, CancellationToken.None);

        Assert.Equal(TeleportOutcome.NotAttuned, result.ValueOrThrow);
        var zone = await state.GetPlayerZone(CancellationToken.None);
        // Zone should remain unchanged
        Assert.Equal(new ZoneId(1), zone.ValueOrThrow);
    }

    [Fact]
    public async Task UseReturn_RecordedInRecordedReturns()
    {
        var teleporter = CreateTeleporter(out _);

        await teleporter.UseReturn(CancellationToken.None);

        Assert.Equal(1, teleporter.RecordedReturns.Count);
    }

    [Fact]
    public async Task GetHomeAetheryte_AfterSetHomeAetheryte_ReturnsId()
    {
        var teleporter = CreateTeleporter(out _);
        var homeId = new AetheryteId(5);
        teleporter.SetHomeAetheryte(homeId);

        var result = await teleporter.GetHomeAetheryte(CancellationToken.None);
        Assert.Equal(homeId, result.ValueOrThrow);
    }

    [Fact]
    public async Task GetReturnCooldown_AfterSetReturnCooldown_ReturnsCorrectValue()
    {
        var teleporter = CreateTeleporter(out _);
        var cooldown = TimeSpan.FromMinutes(15);
        teleporter.SetReturnCooldown(cooldown);

        var result = await teleporter.GetReturnCooldown(CancellationToken.None);
        Assert.Equal(cooldown, result.ValueOrThrow);
    }

    [Fact]
    public async Task PreCancelledToken_ThrowsOperationCanceledException()
    {
        var teleporter = CreateTeleporter(out _);
        var ct = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => teleporter.TeleportToAetheryte(new AetheryteId(1), ct));
    }
}