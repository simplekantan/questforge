using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Tests.Fakes;

/// <summary>
/// Tests for the reshaped FakeCombat (part A).
/// UseAction / UseActionOnObject / IsActionUsable tests removed — those methods were deleted
/// from ICombat per Decision UA1 (moved to IActionExecutor).
/// </summary>
public class FakeCombatTests
{
    [Fact]
    public async Task PreCancelledToken_SetTarget_ThrowsOperationCanceledException()
    {
        var combat = new FakeCombat();
        var ct = new CancellationToken(canceled: true);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => combat.SetTarget(new ActorId(1), ct));
    }
}
