using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for DialogueMenuPoller — pure state machine that fires
/// aggregator.OnDialogueOptionSelected on open→close transition.
///
/// ALL tests will fail to compile until Builder creates:
///   C:\Users\publi\RiderProjects\questforge\QuestForge.Engine\Authoring\DialogueMenuPoller.cs
///
/// Expected class shape:
///   public sealed class DialogueMenuPoller {
///       public DialogueMenuPoller(SnapshotAggregator aggregator);
///       public void Tick(bool menuIsOpen, int? selectedIdx);
///   }
///
/// State machine logic:
///   - Track whether menu was open on last tick (_wasOpen).
///   - Track last known selected index while open (_lastIdx).
///   - On open→close transition: if _lastIdx has a value, call aggregator.OnDialogueOptionSelected(_lastIdx.Value).
///   - Do NOT fire if menu was never opened (close without prior open).
///   - Do NOT fire if selectedIdx was always null while open.
/// </summary>
public sealed class DialogueMenuPollerTests
{
    private static readonly QuestId ActiveQuest = new(2054);

    private static SnapshotAggregator MakeAggregator() => new(activeQuest: ActiveQuest);

    // PD-1
    [Fact]
    public void PD_1_OpenStayOpenCloseWithIdx2_CapturesIdx2()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueMenuPoller.
         *
         * CONTRACT: Given menu open (idx=2), stay open (idx=2), then close (null),
         *           When Current is read,
         *           Then aggregator.Current.DialogueOptionSelected == 2.
         *
         * BUILDER GUIDANCE: On open→close transition, record _lastIdx captured while open.
         */

        // Arrange
        var aggregator = MakeAggregator();
        // RED: DialogueMenuPoller does not exist yet
        var poller = new DialogueMenuPoller(aggregator);

        // Act
        poller.Tick(menuIsOpen: true,  selectedIdx: 2);   // open, selection=2
        poller.Tick(menuIsOpen: true,  selectedIdx: 2);   // still open
        poller.Tick(menuIsOpen: false, selectedIdx: null); // close

        // Assert
        // RED: DialogueOptionSelected does not exist yet
        Assert.Equal(2, aggregator.Current.DialogueOptionSelected);
    }

    // PD-2
    [Fact]
    public void PD_2_PlayerScrolls_LastValueCapturedOnClose()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueMenuPoller.
         *
         * CONTRACT: Given player scrolls through options 0→1→3 then closes,
         *           When Current is read,
         *           Then aggregator.Current.DialogueOptionSelected == 3.
         *
         * BUILDER GUIDANCE: Update _lastIdx every tick the menu is open with a non-null idx.
         * Last non-null value before close is what gets recorded.
         */

        // Arrange
        var aggregator = MakeAggregator();
        // RED: DialogueMenuPoller does not exist yet
        var poller = new DialogueMenuPoller(aggregator);

        // Act
        poller.Tick(menuIsOpen: true,  selectedIdx: 0);
        poller.Tick(menuIsOpen: true,  selectedIdx: 1);
        poller.Tick(menuIsOpen: true,  selectedIdx: 3);
        poller.Tick(menuIsOpen: false, selectedIdx: null);

        // Assert
        Assert.Equal(3, aggregator.Current.DialogueOptionSelected);
    }

    // PD-3
    [Fact]
    public void PD_3_SingleTickOpenClose_WithIdx0_Records0()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueMenuPoller.
         *
         * CONTRACT: Given menu opens and closes on consecutive ticks with idx=0,
         *           When Current is read,
         *           Then aggregator.Current.DialogueOptionSelected == 0.
         *
         * BUILDER GUIDANCE: The open→close detection relies on _wasOpen; even a
         * single-tick open is sufficient to capture the value.
         */

        // Arrange
        var aggregator = MakeAggregator();
        // RED: DialogueMenuPoller does not exist yet
        var poller = new DialogueMenuPoller(aggregator);

        // Act
        poller.Tick(menuIsOpen: true,  selectedIdx: 0);
        poller.Tick(menuIsOpen: false, selectedIdx: null);

        // Assert
        Assert.Equal(0, aggregator.Current.DialogueOptionSelected);
    }

    // PD-4
    [Fact]
    public void PD_4_OpenNeverClose_NoEventFired()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueMenuPoller.
         *
         * CONTRACT: Given menu opens with idx=5 but never closes,
         *           When Current is read after several ticks,
         *           Then aggregator.Current.DialogueOptionSelected is null.
         *
         * BUILDER GUIDANCE: Fire event only on open→close transition, not while open.
         */

        // Arrange
        var aggregator = MakeAggregator();
        // RED: DialogueMenuPoller does not exist yet
        var poller = new DialogueMenuPoller(aggregator);

        // Act
        poller.Tick(menuIsOpen: true, selectedIdx: 5);
        poller.Tick(menuIsOpen: true, selectedIdx: 5);
        poller.Tick(menuIsOpen: true, selectedIdx: 5);

        // Assert
        Assert.Null(aggregator.Current.DialogueOptionSelected);
    }

    // PD-5
    [Fact]
    public void PD_5_NeverOpened_NullStored()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueMenuPoller.
         *
         * CONTRACT: Given the poller is never ticked with menuIsOpen=true,
         *           When Current is read,
         *           Then aggregator.Current.DialogueOptionSelected is null.
         *
         * BUILDER GUIDANCE: State machine starts in "closed" state; no transitions fired.
         */

        // Arrange
        var aggregator = MakeAggregator();
        // RED: DialogueMenuPoller does not exist yet
        var poller = new DialogueMenuPoller(aggregator);

        // Act — never tick with open=true
        poller.Tick(menuIsOpen: false, selectedIdx: null);

        // Assert
        Assert.Null(aggregator.Current.DialogueOptionSelected);
    }

    // PD-6
    [Fact]
    public void PD_6_OpenWithNullIdx_Close_NoEventFired()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueMenuPoller.
         *
         * CONTRACT: Given menu is open (idx=null throughout) then closes,
         *           When Current is read,
         *           Then aggregator.Current.DialogueOptionSelected is null
         *            (no event fired because selectedIdx was never non-null).
         *
         * BUILDER GUIDANCE: Only fire if _lastIdx has a value. If the caller always passes
         * null while open (unreadable addon state), do nothing on close.
         */

        // Arrange
        var aggregator = MakeAggregator();
        // RED: DialogueMenuPoller does not exist yet
        var poller = new DialogueMenuPoller(aggregator);

        // Act
        poller.Tick(menuIsOpen: true,  selectedIdx: null); // open, unreadable
        poller.Tick(menuIsOpen: false, selectedIdx: null); // close

        // Assert
        Assert.Null(aggregator.Current.DialogueOptionSelected);
    }

    // PD-7
    [Fact]
    public void PD_7_CallerFiltersNegative_SubsequentPositiveIdx_Captured()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueMenuPoller.
         *
         * CONTRACT: Given caller passes null (filtering a negative raw value), then idx=2,
         *           When menu closes,
         *           Then aggregator.Current.DialogueOptionSelected == 2.
         *
         * BUILDER GUIDANCE: The caller is responsible for mapping negative raw values to null
         * before calling Tick. The poller simply tracks the last non-null idx while open.
         */

        // Arrange
        var aggregator = MakeAggregator();
        // RED: DialogueMenuPoller does not exist yet
        var poller = new DialogueMenuPoller(aggregator);

        // Act
        poller.Tick(menuIsOpen: true,  selectedIdx: null); // negative raw → null by caller
        poller.Tick(menuIsOpen: true,  selectedIdx: 2);    // valid selection
        poller.Tick(menuIsOpen: false, selectedIdx: null);

        // Assert
        Assert.Equal(2, aggregator.Current.DialogueOptionSelected);
    }

    // PD-8
    [Fact]
    public void PD_8_TwoOpenCloseCycles_SecondValueOverridesFirst()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueMenuPoller.
         *
         * CONTRACT: Given first open/close captures 1, then second open/close captures 9,
         *           When Current is read,
         *           Then aggregator.Current.DialogueOptionSelected == 9 (last write wins).
         *
         * BUILDER GUIDANCE: OnDialogueOptionSelected is last-write-wins; second cycle
         * overwrites the first. This is correct: the engine will have consumed the first
         * value between cycles.
         */

        // Arrange
        var aggregator = MakeAggregator();
        // RED: DialogueMenuPoller does not exist yet
        var poller = new DialogueMenuPoller(aggregator);

        // Act — first cycle
        poller.Tick(menuIsOpen: true,  selectedIdx: 1);
        poller.Tick(menuIsOpen: false, selectedIdx: null);
        // Second cycle
        poller.Tick(menuIsOpen: true,  selectedIdx: 9);
        poller.Tick(menuIsOpen: false, selectedIdx: null);

        // Assert
        Assert.Equal(9, aggregator.Current.DialogueOptionSelected);
    }

    // PD-9
    [Fact]
    public void PD_9_CloseWithNoPriorOpen_NoCallToAggregator()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueMenuPoller.
         *
         * CONTRACT: Given the poller receives close ticks with no preceding open,
         *           When Current is read,
         *           Then aggregator.Current.DialogueOptionSelected remains null
         *            (aggregator methods were never called).
         *
         * BUILDER GUIDANCE: Guard the close→fire logic with _wasOpen check.
         */

        // Arrange
        var aggregator = MakeAggregator();
        // RED: DialogueMenuPoller does not exist yet
        var poller = new DialogueMenuPoller(aggregator);

        // Act — multiple close ticks, never opened
        poller.Tick(menuIsOpen: false, selectedIdx: null);
        poller.Tick(menuIsOpen: false, selectedIdx: null);

        // Assert
        Assert.Null(aggregator.Current.DialogueOptionSelected);
    }

    // PD-10
    [Fact]
    public void PD_10_OpenSelectClose_OnDialogueOptionSelectedCalledExactlyOnce()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueMenuPoller
         *      and adds OnDialogueOptionSelected to SnapshotAggregator.
         *
         * CONTRACT: Given one open→select→close sequence,
         *           When Current is read,
         *           Then aggregator.Current.DialogueOptionSelected is set exactly once
         *            (verifying that only one event fires, not multiple duplicate events).
         *
         * BUILDER GUIDANCE: Fire OnDialogueOptionSelected exactly once per close transition.
         * Reading Current twice after a single close should return the same stable value.
         */

        // Arrange
        var aggregator = MakeAggregator();
        // RED: DialogueMenuPoller does not exist yet
        var poller = new DialogueMenuPoller(aggregator);

        // Act
        poller.Tick(menuIsOpen: true,  selectedIdx: 4);
        poller.Tick(menuIsOpen: false, selectedIdx: null);

        // Assert — read twice to confirm stability
        var first  = aggregator.Current.DialogueOptionSelected;
        var second = aggregator.Current.DialogueOptionSelected;

        Assert.Equal(4, first);
        Assert.Equal(first, second);
    }
}
