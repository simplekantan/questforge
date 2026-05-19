using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

// WHY: QuestForge.Schema is NOT imported here because Schema.AetheryteId and
// Adapters.Types.AetheryteId would collide. All shard IDs here use Adapters.Types.AethernetId
// (the shard type, distinct from AetheryteId which is for master aetheryte crystals).

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for the AethernetDestinationSelected snapshot field and aggregator
/// handlers (Issue #25, Components 4 and 6), plus FakeGameStateProvider.GetLastAethernetDestination
/// (Component 3).
///
/// ALL tests will fail to compile or at runtime until Builder adds:
///   - GameStateSnapshot.AethernetDestinationSelected (AethernetId? non-positional property)
///   - SnapshotAggregator.OnAethernetDestinationSelected(AethernetId destination)
///   - SnapshotAggregator.OnAethernetMenuClosed()
///   - IGameStateProvider.GetLastAethernetDestination(CancellationToken)
///   - FakeGameStateProvider.LastAethernetDestination settable property
///   - FakeGameStateProvider.GetLastAethernetDestination implementation
/// </summary>
public sealed class AethernetSnapshotTests
{
    private static readonly QuestId ActiveQuest = new(2054);

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // =========================================================================
    // Component 4: GameStateSnapshot.AethernetDestinationSelected
    // =========================================================================

    [Fact]
    public void SNAP_1_AethernetDestinationSelected_IsNullByDefault()
    {
        /*
         * RED: Will fail until Builder adds AethernetDestinationSelected property.
         *
         * CONTRACT: Given a GameStateSnapshot constructed with positional args only,
         *           When AethernetDestinationSelected is read,
         *           Then it returns null.
         *
         * BUILDER GUIDANCE: Add as a non-positional init property following the same
         * pattern as KeyItemsAdded in GameStateSnapshot.cs:
         *   public AethernetId? AethernetDestinationSelected { get; init; }
         * Place after LastAethernetShardInteracted so positional constructor call sites
         * are unaffected.
         */

        var snapshot = new GameStateSnapshot(
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

        // RED: AethernetDestinationSelected does not exist yet
        Assert.Null(snapshot.AethernetDestinationSelected);
    }

    [Fact]
    public void SNAP_2_AethernetDestinationSelected_CanBeSetViaInitializer()
    {
        /*
         * RED: Will fail until Builder adds AethernetDestinationSelected property.
         *
         * CONTRACT: Given a snapshot with AethernetDestinationSelected = AethernetId(77),
         *           When the property is read,
         *           Then it returns AethernetId(77).
         *
         * BUILDER GUIDANCE: Non-positional init property; value set via object initializer.
         */

        var snapshot = new GameStateSnapshot(
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
            LastAttuned: null)
        {
            // RED: Property does not exist yet
            AethernetDestinationSelected = new AethernetId(77u)
        };

        Assert.NotNull(snapshot.AethernetDestinationSelected);
        Assert.Equal(new AethernetId(77u), snapshot.AethernetDestinationSelected.Value);
    }

    [Fact]
    public void SNAP_3_AethernetDestinationSelected_WithExpression_LeavesOriginalUnchanged()
    {
        /*
         * RED: Will fail until Builder adds AethernetDestinationSelected property.
         *
         * CONTRACT: Given original snapshot with AethernetDestinationSelected = null,
         *           When modified = original with { AethernetDestinationSelected = AethernetId(33) },
         *           Then original.AethernetDestinationSelected is still null.
         */

        var original = new GameStateSnapshot(
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

        // RED: Property does not exist yet
        var modified = original with { AethernetDestinationSelected = new AethernetId(33u) };

        Assert.Null(original.AethernetDestinationSelected);
        Assert.NotNull(modified.AethernetDestinationSelected);
        Assert.Equal(new AethernetId(33u), modified.AethernetDestinationSelected!.Value);
    }

    // =========================================================================
    // Component 6: SnapshotAggregator.OnAethernetDestinationSelected / OnAethernetMenuClosed
    // =========================================================================

    [Fact]
    public void AGG_1_FreshAggregator_AethernetDestinationSelected_IsNull()
    {
        /*
         * RED: Will fail until Builder adds AethernetDestinationSelected property to snapshot.
         *
         * CONTRACT: Given a freshly constructed SnapshotAggregator,
         *           When Current is read,
         *           Then Current.AethernetDestinationSelected is null.
         */

        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);

        // RED: Property does not exist yet
        Assert.Null(aggregator.Current.AethernetDestinationSelected);
    }

    [Fact]
    public void AGG_2_OnAethernetDestinationSelected_SetsField()
    {
        /*
         * RED: Will fail until Builder adds OnAethernetDestinationSelected method.
         *
         * CONTRACT: Given a SnapshotAggregator,
         *           When OnAethernetDestinationSelected(AethernetId(77)) is called,
         *           Then Current.AethernetDestinationSelected == AethernetId(77).
         *
         * BUILDER GUIDANCE: Add method:
         *   public void OnAethernetDestinationSelected(AethernetId destination)
         * Sets a backing field _aethernetDestinationSelected = destination.
         */

        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);

        // RED: Method does not exist yet
        aggregator.OnAethernetDestinationSelected(new AethernetId(77u));

        Assert.NotNull(aggregator.Current.AethernetDestinationSelected);
        Assert.Equal(new AethernetId(77u), aggregator.Current.AethernetDestinationSelected!.Value);
    }

    [Fact]
    public void AGG_3_OnAethernetDestinationSelected_LastWins()
    {
        /*
         * RED: Will fail until Builder adds OnAethernetDestinationSelected method.
         *
         * CONTRACT: Given OnAethernetDestinationSelected(33) then OnAethernetDestinationSelected(77),
         *           When Current is read,
         *           Then AethernetDestinationSelected == AethernetId(77).
         */

        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);

        // RED: Method does not exist yet
        aggregator.OnAethernetDestinationSelected(new AethernetId(33u));
        aggregator.OnAethernetDestinationSelected(new AethernetId(77u));

        Assert.Equal(new AethernetId(77u), aggregator.Current.AethernetDestinationSelected!.Value);
    }

    [Fact]
    public void AGG_4_OnAethernetMenuClosed_ClearsField()
    {
        /*
         * RED: Will fail until Builder adds both OnAethernetDestinationSelected and
         * OnAethernetMenuClosed methods.
         *
         * CONTRACT: Given AethernetDestinationSelected is set to AethernetId(77),
         *           When OnAethernetMenuClosed() is called,
         *           Then Current.AethernetDestinationSelected is null.
         *
         * BUILDER GUIDANCE: Add method:
         *   public void OnAethernetMenuClosed()
         * Sets backing field _aethernetDestinationSelected = null.
         */

        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);
        // RED: Methods do not exist yet
        aggregator.OnAethernetDestinationSelected(new AethernetId(77u));
        aggregator.OnAethernetMenuClosed();

        Assert.Null(aggregator.Current.AethernetDestinationSelected);
    }

    [Fact]
    public void AGG_5_AethernetDestinationSelected_SurvivesResetDeltas()
    {
        /*
         * RED: Will fail until Builder adds OnAethernetDestinationSelected and wires
         * AethernetDestinationSelected into Current.
         *
         * CONTRACT: Given AethernetDestinationSelected is set to AethernetId(77),
         *           When ResetDeltas() is called,
         *           Then AethernetDestinationSelected remains AethernetId(77).
         *
         * BUILDER GUIDANCE: ResetDeltas only clears per-action delta signals (KeyItemsAdded,
         * KeyItemsRemoved). Do NOT clear _aethernetDestinationSelected in ResetDeltas.
         * It is cleared only by OnAethernetMenuClosed.
         */

        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);
        // RED: Method does not exist yet
        aggregator.OnAethernetDestinationSelected(new AethernetId(77u));

        aggregator.ResetDeltas();

        Assert.Equal(new AethernetId(77u), aggregator.Current.AethernetDestinationSelected!.Value);
    }

    [Fact]
    public void AGG_6_OnAethernetMenuClosed_WhenNeverSet_IsNoOp()
    {
        /*
         * RED: Will fail until Builder adds OnAethernetMenuClosed method.
         *
         * CONTRACT: Given a fresh aggregator (AethernetDestinationSelected is null),
         *           When OnAethernetMenuClosed() is called,
         *           Then AethernetDestinationSelected remains null (no exception).
         */

        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);

        // RED: Method does not exist yet — must not throw
        aggregator.OnAethernetMenuClosed();

        Assert.Null(aggregator.Current.AethernetDestinationSelected);
    }

    // =========================================================================
    // Component 3: FakeGameStateProvider.GetLastAethernetDestination
    // =========================================================================

    [Fact]
    public async Task FAKE_1_GetLastAethernetDestination_DefaultReturnsNull()
    {
        /*
         * RED: Will fail until Builder adds GetLastAethernetDestination to IGameStateProvider
         * and the corresponding implementation to FakeGameStateProvider.
         *
         * CONTRACT: Given a FakeGameStateProvider with no LastAethernetDestination set,
         *           When GetLastAethernetDestination is called,
         *           Then Result.IsSuccess == true and Value is null.
         *
         * BUILDER GUIDANCE:
         *   IGameStateProvider: Task<Result<AethernetId?>> GetLastAethernetDestination(CancellationToken)
         *   FakeGameStateProvider: public AethernetId? LastAethernetDestination { get; set; }
         *   Returns Result.Ok(LastAethernetDestination) — always succeeds.
         */

        var fake = new FakeGameStateProvider();

        // RED: Method does not exist yet on IGameStateProvider or FakeGameStateProvider
        var result = await fake.GetLastAethernetDestination(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.ValueOrThrow);
    }

    [Fact]
    public async Task FAKE_2_GetLastAethernetDestination_ReturnsSetValue()
    {
        /*
         * RED: Will fail until Builder adds the new method and property.
         *
         * CONTRACT: Given FakeGameStateProvider.LastAethernetDestination = AethernetId(77),
         *           When GetLastAethernetDestination is called,
         *           Then Result.IsSuccess == true and Value == AethernetId(77).
         */

        var fake = new FakeGameStateProvider();
        // RED: Property does not exist yet
        fake.LastAethernetDestination = new AethernetId(77u);

        var result = await fake.GetLastAethernetDestination(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ValueOrThrow);
        Assert.Equal(new AethernetId(77u), result.ValueOrThrow!.Value);
    }

    [Fact]
    public async Task FAKE_3_GetLastAethernetDestination_ReturnsNullAfterClear()
    {
        /*
         * RED: Will fail until Builder adds the new method and property.
         *
         * CONTRACT: Given LastAethernetDestination was set then cleared to null,
         *           When GetLastAethernetDestination is called,
         *           Then Result.IsSuccess == true and Value is null.
         */

        var fake = new FakeGameStateProvider();
        // RED: Property does not exist yet
        fake.LastAethernetDestination = new AethernetId(33u);
        fake.LastAethernetDestination = null;

        var result = await fake.GetLastAethernetDestination(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.ValueOrThrow);
    }
}
