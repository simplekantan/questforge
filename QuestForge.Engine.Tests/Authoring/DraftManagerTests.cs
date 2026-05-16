using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for DraftManager — scenarios 19-25 from PHASE_9_PLAN.md §5.2.
/// All tests will fail until Builder implements DraftManager.
/// </summary>
public sealed class DraftManagerTests
{
    private static readonly QuestId Quest2054 = new(2054);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan AutoSaveInterval = TimeSpan.FromSeconds(60);

    private static GameStateSnapshot MakeSnapshot() => new(
        CapturedAt: T0,
        Zone: new ZoneId(100),
        Position: new WorldPosition(0, 0, 0),
        ActiveQuest: Quest2054,
        QuestSequence: 0,
        QuestFlags: 0,
        QuestAccepted: false,
        QuestCompleted: false,
        LastNpcInteracted: null,
        LastNpcPosition: null,
        LastDialoguePrompt: null,
        LastDialogueAnswer: null,
        InventoryHash: 0);

    private static DraftStep MakeDraftStep(string stepId, int seqNum = 0) =>
        new(
            StepId: stepId,
            StepType: "talk",
            SequenceNumber: seqNum,
            InferredFrom: InferredFrom.QuestSequenceChange,
            ObservedBefore: MakeSnapshot(),
            ObservedAfter: MakeSnapshot(),
            SuggestedExpect: $"questSequence(2054) >= {seqNum}",
            Notes: null,
            Raw: new TalkStep { Id = stepId });

    // =========================================================================
    // Scenario 19 — DraftManager_GetOrCreate_ReturnsExistingDraft
    // =========================================================================
    [Fact]
    public async Task DraftManager_GetOrCreate_ReturnsExistingDraft()
    {
        /*
         * RED: Will fail until Builder implements DraftManager.GetOrCreate
         *
         * CONTRACT: Given FakeDraftStorage already has a draft for quest 2054,
         *           When GetOrCreate(2054) is called,
         *           Then the existing draft is returned (not a new one)
         *
         * BUILDER GUIDANCE: Load from storage first; if found return it.
         */

        // Arrange
        var storage = new FakeDraftStorage();
        var clock = new FakeClock(T0);
        var existingDraft = new QuestDraft(Quest2054, T0);
        await storage.Save(Quest2054, existingDraft, CancellationToken.None);
        var manager = new DraftManager(storage, clock, AutoSaveInterval);

        // Act
        var draft = await manager.GetOrCreate(Quest2054, CancellationToken.None);

        // Assert
        Assert.NotNull(draft);
        Assert.Equal(Quest2054, draft.QuestId);
        // Should be the same draft (or at least same questId from storage)
        // SaveCount was 1 from setup; GetOrCreate should not save
        Assert.Equal(1, storage.SaveCount);
    }

    // =========================================================================
    // Scenario 20 — DraftManager_GetOrCreate_CreatesNewDraft_WhenStorageEmpty
    // =========================================================================
    [Fact]
    public async Task DraftManager_GetOrCreate_CreatesNewDraft_WhenStorageEmpty()
    {
        /*
         * RED: Will fail until Builder implements DraftManager.GetOrCreate
         *
         * CONTRACT: Given FakeDraftStorage has no drafts,
         *           When GetOrCreate(2054) is called,
         *           Then a new QuestDraft with QuestId=2054 is created and returned
         *                (not automatically persisted)
         *
         * BUILDER GUIDANCE: When Load returns null, create a new QuestDraft.
         *                   Do NOT call Save automatically.
         */

        // Arrange
        var storage = new FakeDraftStorage();
        var clock = new FakeClock(T0);
        var manager = new DraftManager(storage, clock, AutoSaveInterval);

        // Act
        var draft = await manager.GetOrCreate(Quest2054, CancellationToken.None);

        // Assert
        Assert.NotNull(draft);
        Assert.Equal(Quest2054, draft.QuestId);
        Assert.Equal(0, storage.SaveCount); // not auto-persisted
    }

    // =========================================================================
    // Scenario 21 — DraftManager_MaybeAutoSave_Skips_WhenIntervalNotElapsed
    // =========================================================================
    [Fact]
    public async Task DraftManager_MaybeAutoSave_Skips_WhenIntervalNotElapsed()
    {
        /*
         * RED: Will fail until Builder implements DraftManager.MaybeAutoSave
         *
         * CONTRACT: Given autoSaveInterval=60s, draft is dirty at T0,
         *           When MaybeAutoSave is called at T0+30s (< 60s),
         *           Then FakeDraftStorage.SaveCount == 0
         *
         * BUILDER GUIDANCE: Track last-saved timestamp; skip if now - lastSave < autoSaveInterval.
         */

        // Arrange
        var storage = new FakeDraftStorage();
        var clock = new FakeClock(T0);
        var manager = new DraftManager(storage, clock, AutoSaveInterval);
        await manager.GetOrCreate(Quest2054, CancellationToken.None);
        manager.MarkDirty(Quest2054);

        // Act — advance 30s (less than 60s interval)
        clock.Advance(TimeSpan.FromSeconds(30));
        await manager.MaybeAutoSave(Quest2054, CancellationToken.None);

        // Assert
        Assert.Equal(0, storage.SaveCount);
    }

    // =========================================================================
    // Scenario 22 — DraftManager_MaybeAutoSave_Persists_WhenIntervalElapsed
    // =========================================================================
    [Fact]
    public async Task DraftManager_MaybeAutoSave_Persists_WhenIntervalElapsed()
    {
        /*
         * RED: Will fail until Builder implements DraftManager.MaybeAutoSave
         *
         * CONTRACT: Given autoSaveInterval=60s, draft is dirty at T0,
         *           When MaybeAutoSave is called at T0+61s (>= 60s),
         *           Then FakeDraftStorage.SaveCount == 1
         *
         * BUILDER GUIDANCE: If dirty AND elapsed >= interval, call storage.Save.
         */

        // Arrange
        var storage = new FakeDraftStorage();
        var clock = new FakeClock(T0);
        var manager = new DraftManager(storage, clock, AutoSaveInterval);
        await manager.GetOrCreate(Quest2054, CancellationToken.None);
        manager.MarkDirty(Quest2054);

        // Act — advance 61s (past the 60s interval)
        clock.Advance(TimeSpan.FromSeconds(61));
        await manager.MaybeAutoSave(Quest2054, CancellationToken.None);

        // Assert
        Assert.Equal(1, storage.SaveCount);
    }

    // =========================================================================
    // Scenario 23 — DraftManager_MaybeAutoSave_Skips_WhenClean
    // =========================================================================
    [Fact]
    public async Task DraftManager_MaybeAutoSave_Skips_WhenClean()
    {
        /*
         * RED: Will fail until Builder implements DraftManager.MaybeAutoSave
         *
         * CONTRACT: Given no mutation since last save (draft is clean),
         *           When MaybeAutoSave is called at T0+100s,
         *           Then SaveCount unchanged (no save)
         *
         * BUILDER GUIDANCE: Track dirty flag; skip MaybeAutoSave when not dirty.
         */

        // Arrange
        var storage = new FakeDraftStorage();
        var clock = new FakeClock(T0);
        var manager = new DraftManager(storage, clock, AutoSaveInterval);
        await manager.GetOrCreate(Quest2054, CancellationToken.None);
        // Do NOT mark dirty — draft is clean

        // Act — advance well past interval
        clock.Advance(TimeSpan.FromSeconds(100));
        await manager.MaybeAutoSave(Quest2054, CancellationToken.None);

        // Assert
        Assert.Equal(0, storage.SaveCount);
    }

    // =========================================================================
    // Scenario 24 — BackupRotation_KeepsLatestFive
    // =========================================================================
    [Fact]
    public async Task BackupRotation_KeepsLatestFive()
    {
        /*
         * RED: Will fail until Builder implements FakeDraftStorage.CreateBackup (fully implemented)
         *      and DraftManager.SaveNow (which calls CreateBackup)
         *
         * CONTRACT: Given CreateBackup called 6 times,
         *           Then storage retains at most 5 backups; oldest (6th) is dropped
         *
         * BUILDER GUIDANCE: FakeDraftStorage.CreateBackup caps the backup list at 5.
         */

        // Arrange
        var storage = new FakeDraftStorage();
        var clock = new FakeClock(T0);
        var draft = new QuestDraft(Quest2054, T0);
        await storage.Save(Quest2054, draft, CancellationToken.None);

        // Act — create 6 backups
        for (var i = 0; i < 6; i++)
            await storage.CreateBackup(Quest2054, CancellationToken.None);

        // Assert — at most 5 backups kept
        Assert.Equal(5, storage.BackupCount(Quest2054));
    }

    // =========================================================================
    // Scenario 25 — BackupRotation_AssignsLowestNumberToMostRecent
    // =========================================================================
    [Fact]
    public async Task BackupRotation_AssignsLowestNumberToMostRecent()
    {
        /*
         * RED: Will fail until Builder implements FakeDraftStorage.CreateBackup
         *
         * CONTRACT: Given CreateBackup called 3 times (on the same draft),
         *           Then the most recent backup is at slot index 0 (.bak.001)
         *                and the oldest is at slot index 2 (.bak.003)
         *
         * BUILDER GUIDANCE: FakeDraftStorage backup list index 0 = most recent = .bak.001.
         */

        // Arrange
        var storage = new FakeDraftStorage();
        var clock = new FakeClock(T0);

        // Create 3 distinct draft states
        var draft1 = new QuestDraft(Quest2054, T0);
        draft1.AddStep(MakeDraftStep("step-first"), T0);
        await storage.Save(Quest2054, draft1, CancellationToken.None);
        await storage.CreateBackup(Quest2054, CancellationToken.None); // backup 1 = draft with step-first

        var draft2 = new QuestDraft(Quest2054, T0);
        draft2.AddStep(MakeDraftStep("step-second"), T0);
        await storage.Save(Quest2054, draft2, CancellationToken.None);
        await storage.CreateBackup(Quest2054, CancellationToken.None); // backup 1 = draft2, old backup → 2

        var draft3 = new QuestDraft(Quest2054, T0);
        draft3.AddStep(MakeDraftStep("step-third"), T0);
        await storage.Save(Quest2054, draft3, CancellationToken.None);
        await storage.CreateBackup(Quest2054, CancellationToken.None); // backup 1 = draft3, old → 2, oldest → 3

        // Assert — 3 backups; index 0 is the most recent (draft3), index 2 is oldest (draft1)
        Assert.Equal(3, storage.BackupCount(Quest2054));

        var mostRecent = storage.GetBackup(Quest2054, 0);
        var oldest = storage.GetBackup(Quest2054, 2);

        Assert.NotNull(mostRecent);
        Assert.NotNull(oldest);

        // Most recent backup should contain "step-third"
        Assert.Equal("step-third", mostRecent.Steps[0].StepId);
        // Oldest backup should contain "step-first"
        Assert.Equal("step-first", oldest.Steps[0].StepId);
    }
}
