using QuestForge.Adapters.Items;
using QuestForge.Adapters.Types;
using QuestForge.Schema;

// WHY: Schema.ItemKind and Adapters.Types.NpcId are used directly; Position3 comes from
// QuestForge.Schema. Full qualification is used where types from different assemblies share
// a name so the tests are readable alongside the interpreter under test.

namespace QuestForge.Adapters.Tests.Items;

/// <summary>
/// Tests for ItemUseInterpreter.ResolveTargetMode — the pure target-mode decision
/// (Direct / Npc / Ground) kept under test in the Dalamud-free adapters assembly.
///
/// Run with: dotnet test QuestForge.Adapters.Tests --filter "FullyQualifiedName~ItemUseInterpreterTests"
/// </summary>
public sealed class ItemUseInterpreterTests
{
    // =========================================================================
    // ResolveTargetMode — decides Direct / Npc / Ground from the optional pair
    // =========================================================================

    // UI-TGT-1: Both null → Direct (no explicit target; item used on self or current target)
    [Fact]
    public void ResolveTargetMode_BothNull_ReturnsDirect()
    {
        // Given: neither targetNpcId nor targetPosition is provided
        // When:  the interpreter resolves the target mode
        // Then:  Direct is returned

        var result = ItemUseInterpreter.ResolveTargetMode(targetNpcId: null, targetPosition: null);

        Assert.Equal(ItemTargetMode.Direct, result);
    }

    // UI-TGT-2: Only targetNpcId set → Npc
    [Fact]
    public void ResolveTargetMode_OnlyNpcId_ReturnsNpc()
    {
        // Given: only targetNpcId is provided
        // When:  the interpreter resolves the target mode
        // Then:  Npc is returned

        var npcId = new NpcId(1000789u);

        var result = ItemUseInterpreter.ResolveTargetMode(targetNpcId: npcId, targetPosition: null);

        Assert.Equal(ItemTargetMode.Npc, result);
    }

    // UI-TGT-3: Only targetPosition set → Ground
    [Fact]
    public void ResolveTargetMode_OnlyPosition_ReturnsGround()
    {
        // Given: only targetPosition is provided
        // When:  the interpreter resolves the target mode
        // Then:  Ground is returned

        var position = new Position3(10f, 0f, 20f);

        var result = ItemUseInterpreter.ResolveTargetMode(targetNpcId: null, targetPosition: position);

        Assert.Equal(ItemTargetMode.Ground, result);
    }

    // UI-TGT-4: Both set → ArgumentException (defense-in-depth; DraftValidator E15 already
    // rejects this at authoring time, but the helper must not silently pick one).
    [Fact]
    public void ResolveTargetMode_BothSet_ThrowsArgumentException()
    {
        // Given: both targetNpcId and targetPosition are provided (impossible in valid quest data
        //        but must be rejected here rather than silently picking one)
        // When:  the interpreter is called
        // Then:  ArgumentException is thrown

        var npcId    = new NpcId(1000789u);
        var position = new Position3(10f, 0f, 20f);

        Assert.Throws<ArgumentException>(() =>
            ItemUseInterpreter.ResolveTargetMode(targetNpcId: npcId, targetPosition: position));
    }
}
