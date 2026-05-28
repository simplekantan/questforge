using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Types;
using QuestForge.Schema;

namespace QuestForge.Adapters.Fakes.Interaction;

public sealed class FakeVendor : IVendor
{
    private PurchaseOutcome? _nextOutcome;
    private readonly List<(ItemId Item, int Increment, FakeGameStateProvider GameState)> _onPurchasedCallbacks = new();

    public record PurchaseCall(NpcId Vendor, ItemId Item, int Quantity, PurchaseCurrency Currency, DateTimeOffset At, int? GcCategory = null, int? GcRankTier = null)
        : AdapterCall(At);

    public CallLog<PurchaseCall> RecordedCalls { get; } = new();

    /// <summary>Number of times <see cref="Close"/> has been called.</summary>
    public int CloseCallCount { get; private set; }

    public FakeVendor() { }

    public FakeVendor(FakeGameStateProvider gameState)
    {
        // Ctor accepting FakeGameStateProvider — used to wire inventory-mutating callbacks.
        _ = gameState; // stored via OnPurchased if needed
    }

    public void ScriptNextOutcome(PurchaseOutcome outcome) => _nextOutcome = outcome;

    public void OnPurchased(ItemId item, int increment, FakeGameStateProvider gameState)
        => _onPurchasedCallbacks.Add((item, increment, gameState));

    public Task<Result<PurchaseOutcome>> Purchase(
        NpcId vendor,
        ItemId item,
        int quantity,
        PurchaseCurrency currency,
        CancellationToken ct = default,
        int? gcCategory = null,
        int? gcRankTier = null)
    {
        ct.ThrowIfCancellationRequested();
        RecordedCalls.Add(new PurchaseCall(vendor, item, quantity, currency, DateTimeOffset.UtcNow, gcCategory, gcRankTier));

        var outcome = _nextOutcome ?? PurchaseOutcome.ShopOpening;
        _nextOutcome = null;

        if (outcome == PurchaseOutcome.Purchased)
        {
            foreach (var (callbackItem, increment, gs) in _onPurchasedCallbacks)
            {
                if (callbackItem == item)
                {
                    // Fire async but we need to read current count — use sync path
                    var current = 0;
                    var readTask = gs.GetItemCount(callbackItem, CancellationToken.None);
                    readTask.Wait();
                    if (readTask.Result is Result<int>.Success { Value: var v }) current = v;
                    gs.SetItemCount(callbackItem, current + increment);
                }
            }
        }

        return Task.FromResult<Result<PurchaseOutcome>>(Result.Ok(outcome));
    }

    public Task Close(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        CloseCallCount++;
        return Task.CompletedTask;
    }
}
