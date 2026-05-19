using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for GameStateSnapshot.DialogueOptionSelected (int? non-positional property).
///
/// ALL tests will fail to compile until Builder adds:
///   public int? DialogueOptionSelected { get; init; }
/// to GameStateSnapshot following the same non-positional init pattern as AethernetTeleportCompleted.
/// </summary>
public sealed class DialogueOptionSnapshotTests
{
    private static readonly QuestId ActiveQuest = new(2054);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static GameStateSnapshot DefaultSnapshot() => new(
        CapturedAt: T0,
        Zone: new ZoneId(131),
        Position: new WorldPosition(0, 0, 0),
        ActiveQuest: ActiveQuest,
        QuestSequence: 0,
        QuestFlags: 0,
        QuestAccepted: false,
        QuestCompleted: false,
        LastNpcInteracted: null,
        LastNpcPosition: null,
        LastDialoguePrompt: null,
        LastDialogueAnswer: null,
        InventoryHash: 0,
        LastAttuned: null);

    // SNAP-DLG-1
    [Fact]
    public void SNAP_DLG_1_Default_DialogueOptionSelected_IsNull()
    {
        /*
         * RED: Will fail to compile until Builder adds DialogueOptionSelected property.
         *
         * CONTRACT: Given a GameStateSnapshot constructed with positional args only,
         *           When DialogueOptionSelected is read,
         *           Then it returns null.
         *
         * BUILDER GUIDANCE: Add as a non-positional init property in GameStateSnapshot.cs:
         *   public int? DialogueOptionSelected { get; init; }
         * Place after AethernetTeleportCompleted so no existing call site is affected.
         */

        // Arrange
        var snapshot = DefaultSnapshot();

        // Act & Assert
        // RED: DialogueOptionSelected does not exist yet
        Assert.Null(snapshot.DialogueOptionSelected);
    }

    // SNAP-DLG-2
    [Fact]
    public void SNAP_DLG_2_InitWithValue2_ReadsBack2()
    {
        /*
         * RED: Will fail to compile until Builder adds DialogueOptionSelected property.
         *
         * CONTRACT: Given a snapshot initialised with DialogueOptionSelected = 2,
         *           When the property is read,
         *           Then it returns 2.
         *
         * BUILDER GUIDANCE: Non-positional init property; value set via object initializer.
         */

        // Arrange
        var snapshot = DefaultSnapshot() with { DialogueOptionSelected = 2 };

        // Act & Assert
        // RED: Property does not exist yet
        Assert.Equal(2, snapshot.DialogueOptionSelected);
    }

    // SNAP-DLG-3
    [Fact]
    public void SNAP_DLG_3_InitWithValue0_ReadsBack0_NotNull()
    {
        /*
         * RED: Will fail to compile until Builder adds DialogueOptionSelected property.
         *
         * CONTRACT: Given a snapshot initialised with DialogueOptionSelected = 0,
         *           When the property is read,
         *           Then it returns 0 (not null — zero is a valid list index).
         *
         * BUILDER GUIDANCE: int? distinguishes null (no selection) from 0 (first item selected).
         */

        // Arrange
        var snapshot = DefaultSnapshot() with { DialogueOptionSelected = 0 };

        // Act & Assert
        // RED: Property does not exist yet
        Assert.NotNull(snapshot.DialogueOptionSelected);
        Assert.Equal(0, snapshot.DialogueOptionSelected);
    }

    // SNAP-DLG-4
    [Fact]
    public void SNAP_DLG_4_WithExpression_OriginalUnchanged_NewHas5()
    {
        /*
         * RED: Will fail to compile until Builder adds DialogueOptionSelected property.
         *
         * CONTRACT: Given original snapshot with DialogueOptionSelected = null,
         *           When modified = original with { DialogueOptionSelected = 5 },
         *           Then original.DialogueOptionSelected is still null,
         *           And  modified.DialogueOptionSelected == 5.
         *
         * BUILDER GUIDANCE: Records are immutable; with-expression produces a new instance.
         */

        // Arrange
        var original = DefaultSnapshot();

        // Act
        // RED: Property does not exist yet
        var modified = original with { DialogueOptionSelected = 5 };

        // Assert
        Assert.Null(original.DialogueOptionSelected);
        Assert.Equal(5, modified.DialogueOptionSelected);
    }
}
