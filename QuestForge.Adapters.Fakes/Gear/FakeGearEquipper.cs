using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Gear;

public sealed class FakeGearEquipper : IGearEquipper
{
    public record EquipCall(uint ItemId, DateTimeOffset At) : AdapterCall(At);

    public CallLog<EquipCall> RecordedCalls { get; } = new();

    private readonly Dictionary<uint, bool> _equippedItems = new();
    private (string Reason, string? Detail)? _nextFailure;
    private EquipOutcome? _nextOutcome;

    public void ScriptNextResult(EquipOutcome outcome) => _nextOutcome = outcome;
    public void ScriptNextFailure(string reason, string? detail = null)
        => _nextFailure = (reason, detail);
    public void SetItemEquipped(uint itemId, bool equipped) => _equippedItems[itemId] = equipped;

    public void Reset()
    {
        RecordedCalls.Clear();
        _equippedItems.Clear();
        _nextFailure = null;
        _nextOutcome = null;
    }

    public Task<Result<EquipOutcome>> EquipItem(uint itemId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedCalls.Add(new EquipCall(itemId, DateTimeOffset.UtcNow));

        if (_nextFailure is { } f)
        {
            _nextFailure = null;
            return Task.FromResult<Result<EquipOutcome>>(Result.Fail<EquipOutcome>(f.Reason, f.Detail));
        }

        var outcome = _nextOutcome ?? EquipOutcome.Equipped;
        _nextOutcome = null;
        return Task.FromResult<Result<EquipOutcome>>(Result.Ok(outcome));
    }

    public Task<Result<bool>> IsItemEquipped(uint itemId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var equipped = _equippedItems.TryGetValue(itemId, out var v) && v;
        return Task.FromResult<Result<bool>>(Result.Ok(equipped));
    }
}
