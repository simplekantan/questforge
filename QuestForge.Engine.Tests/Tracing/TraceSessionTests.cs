using System.Text.Json;
using QuestForge.Adapters;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Tracing;
using Xunit;

namespace QuestForge.Engine.Tests.Tracing;

/// <summary>
/// RED PHASE: Tests for TraceSession — the unified trace lifecycle manager.
/// All tests WILL fail because TraceSession and TraceMode do not exist yet.
///
/// TraceSession lives in QuestForge.Adapters.Tracing\TraceSession.cs.
/// TraceMode lives in QuestForge.Adapters.Tracing\TraceMode.cs.
///
/// BUILDER: Start with the simplest lifecycle test
/// (Off_OnPluginStart_FileRemainsClosedIsFileOpenFalse), then work through modes
/// in order: Off → Always → Authoring → Recording → Dedup → RunEndEvent bypass →
/// Error handling → Thread safety.
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~TraceSessionTests"
///
/// Total: 40 tests.
/// </summary>
public sealed class TraceSessionTests
{
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero);
    private static readonly string _tracesDir = Path.Combine(Path.GetTempPath(), "qf-trace-tests");

    // Helper: build a writerFactory that captures the FakeTraceWriter it created.
    private static (Func<string, ITraceWriter> factory, Func<FakeTraceWriter?> getWriter) MakeCaptureFactory()
    {
        FakeTraceWriter? captured = null;
        ITraceWriter Factory(string _)
        {
            captured = new FakeTraceWriter();
            return captured;
        }
        return (Factory, () => captured);
    }

    // Helper: build a simple writerFactory that always returns a fresh FakeTraceWriter.
    // Use MakeCaptureFactory instead when you need to inspect what was written.
    private static Func<string, ITraceWriter> MakeSimpleFactory(out FakeTraceWriter[] slot)
    {
        var list = new List<FakeTraceWriter>();
        ITraceWriter Factory(string _)
        {
            var fw = new FakeTraceWriter();
            list.Add(fw);
            return fw;
        }
        // capture reference so we can inspect after the fact
        slot = null!; // assigned lazily — caller reads list directly
        // return factory and expose list via closure; caller can read list[0] etc.
        _ = list; // suppress unused warning in some compilers
        return Factory;
    }

    private static ObservationEvent MakeObservation(
        string method = "GetPlayerZone",
        int value = 182,
        string runId = "run-001") =>
        new ObservationEvent(
            RunId: runId,
            Method: method,
            Argument: null,
            Value: JsonSerializer.SerializeToElement(value),
            At: _now);

    private static RunEndEvent MakeRunEnd(string runId = "run-001") =>
        new RunEndEvent(runId, "done", _now);

    private static RunStartEvent MakeRunStart(string runId = "run-001") =>
        new RunStartEvent(runId, 66130u, 66130u, _now);

    // =========================================================================
    // Off mode (4 tests)
    // =========================================================================

    [Fact]
    public void Off_WriteBeforeLifecycle_Dropped()
    {
        /*
         * RED: Will fail until TraceSession and TraceMode exist.
         *
         * CONTRACT: Given TraceMode.Off,
         *           When Write is called before any lifecycle method,
         *           Then the inner writer is never invoked and no exception is thrown.
         *
         * BUILDER GUIDANCE: TraceMode.Off means nothing is ever opened. All writes
         *   must be silently dropped.
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Off, _tracesDir, factory);

        // Act
        session.Write(MakeRunStart()); // must not throw

        // Assert
        Assert.Null(getWriter()); // factory must never have been called
    }

    [Fact]
    public void Off_OnPluginStart_FileRemainsClosedIsFileOpenFalse()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Off,
         *           When OnPluginStart() is called,
         *           Then IsFileOpen remains false and CurrentFilePath is null.
         */

        // Arrange
        var (factory, _) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Off, _tracesDir, factory);

        // Act
        session.OnPluginStart();

        // Assert
        Assert.False(session.IsFileOpen,        "Off mode: IsFileOpen must be false after OnPluginStart");
        Assert.Null(session.CurrentFilePath);
    }

    [Fact]
    public void Off_OnEnterAuthorMode_FileRemainsClosedIsFileOpenFalse()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Off,
         *           When OnEnterAuthorMode(questId) is called,
         *           Then IsFileOpen remains false.
         */

        // Arrange
        using var session = new TraceSession(TraceMode.Off, _tracesDir);

        // Act
        session.OnEnterAuthorMode(66130u);

        // Assert
        Assert.False(session.IsFileOpen, "Off mode: IsFileOpen must be false after OnEnterAuthorMode");
    }

    [Fact]
    public void Off_OnOpenRecordModal_FileRemainsClosedIsFileOpenFalse()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Off,
         *           When OnOpenRecordModal(questId) is called,
         *           Then IsFileOpen remains false.
         */

        // Arrange
        using var session = new TraceSession(TraceMode.Off, _tracesDir);

        // Act
        session.OnOpenRecordModal(66130u);

        // Assert
        Assert.False(session.IsFileOpen, "Off mode: IsFileOpen must be false after OnOpenRecordModal");
    }

    // =========================================================================
    // Always mode (7 tests)
    // =========================================================================

    [Fact]
    public void Always_OnPluginStart_FileOpenGateOpenFilenameMatchesPattern()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Always,
         *           When OnPluginStart() is called,
         *           Then IsFileOpen=true, IsWriteGateOpen=true, and CurrentFilePath
         *           ends with a filename matching the pattern
         *           session-\d{8}-\d{6}\.jsonl
         *
         * BUILDER GUIDANCE: Format: $"session-{at:yyyyMMdd-HHmmss}.jsonl"
         */

        // Arrange
        var (factory, _) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);

        // Act
        session.OnPluginStart();

        // Assert
        Assert.True(session.IsFileOpen,         "Always: IsFileOpen must be true after OnPluginStart");
        Assert.True(session.IsWriteGateOpen,     "Always: IsWriteGateOpen must be true after OnPluginStart");
        Assert.NotNull(session.CurrentFilePath);
        var filename = Path.GetFileName(session.CurrentFilePath);
        Assert.Matches(@"^session-\d{8}-\d{6}\.jsonl$", filename);
    }

    [Fact]
    public void Always_WriteBeforeOnPluginStart_Dropped()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Always,
         *           When Write is called before OnPluginStart(),
         *           Then the event is dropped (inner writer count = 0).
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);

        // Act
        session.Write(MakeRunStart());

        // Assert
        Assert.Null(getWriter()); // factory never called — no file open yet
    }

    [Fact]
    public void Always_WriteAfterOnPluginStart_ReachesInnerWriter()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Always with OnPluginStart() called,
         *           When Write is called,
         *           Then the event reaches the inner FakeTraceWriter.
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();

        // Act
        session.Write(MakeRunStart());

        // Assert
        var inner = getWriter();
        Assert.NotNull(inner);
        Assert.Equal(1, inner!.Count);
    }

    [Fact]
    public void Always_OnEnterAuthorModeAfterPluginStart_NoSecondFileGateUnchanged()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Always with OnPluginStart() called,
         *           When OnEnterAuthorMode(questId) is called,
         *           Then IsFileOpen remains true, CurrentFilePath is UNCHANGED,
         *           and IsWriteGateOpen remains true.
         *
         * BUILDER GUIDANCE: Always mode's file spans the entire plugin lifetime;
         *   author mode entry must not open a second file.
         */

        // Arrange
        var (factory, _) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();
        var pathBeforeAuthor = session.CurrentFilePath;

        // Act
        session.OnEnterAuthorMode(66130u);

        // Assert
        Assert.True(session.IsFileOpen,          "Always: file must still be open after OnEnterAuthorMode");
        Assert.True(session.IsWriteGateOpen,      "Always: gate must still be open after OnEnterAuthorMode");
        Assert.Equal(pathBeforeAuthor, session.CurrentFilePath);
    }

    [Fact]
    public void Always_OnExitAuthoring_FileStaysOpen()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Always with OnPluginStart() then OnEnterAuthorMode(),
         *           When OnExitAuthoring() is called,
         *           Then IsFileOpen remains true.
         */

        // Arrange
        var (factory, _) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();
        session.OnEnterAuthorMode(66130u);

        // Act
        session.OnExitAuthoring();

        // Assert
        Assert.True(session.IsFileOpen, "Always: file must stay open after OnExitAuthoring");
    }

    [Fact]
    public void Always_OnPluginStop_FileClosesSubsequentWriteDropped()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Always with OnPluginStart() called,
         *           When OnPluginStop() is called then Write is called,
         *           Then IsFileOpen=false and the event is dropped.
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();

        // Act
        session.OnPluginStop();
        var countAtStop = getWriter()?.Count ?? 0;
        session.Write(MakeRunStart());

        // Assert
        Assert.False(session.IsFileOpen, "Always: IsFileOpen must be false after OnPluginStop");
        Assert.Equal(countAtStop, getWriter()?.Count ?? 0); // nothing written after stop
    }

    [Fact]
    public void Always_RunEndEventAfterPluginStop_FileClosedEventDropped()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Always after OnPluginStop() (file closed),
         *           When Write(RunEndEvent) is called,
         *           Then the event is dropped — RunEndEvent bypasses the gate but
         *           cannot bypass a closed file.
         *
         * BUILDER GUIDANCE: RunEndEvent bypasses the write gate but NOT the
         *   "file is open" check. If the file is closed, even RunEndEvent is dropped.
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();
        session.OnPluginStop();
        var countAtStop = getWriter()?.Count ?? 0;

        // Act
        session.Write(MakeRunEnd());

        // Assert
        Assert.Equal(countAtStop, getWriter()?.Count ?? 0); // RunEndEvent must not reach writer when file is closed
    }

    // =========================================================================
    // Authoring mode (7 tests)
    // =========================================================================

    [Fact]
    public void Authoring_WriteBeforeOnEnterAuthorMode_Dropped()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Authoring,
         *           When Write is called before OnEnterAuthorMode(),
         *           Then the event is dropped.
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Authoring, _tracesDir, factory);

        // Act
        session.Write(MakeRunStart());

        // Assert
        Assert.Null(getWriter()); // factory never called
    }

    [Fact]
    public void Authoring_OnEnterAuthorMode_FileOpenGateOpenFilenameContainsQuestId()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Authoring,
         *           When OnEnterAuthorMode(66130u) is called,
         *           Then IsFileOpen=true, IsWriteGateOpen=true, and
         *           CurrentFilePath filename contains "66130".
         *
         * BUILDER GUIDANCE: Format: $"{questId}-{at:yyyyMMdd-HHmmss}.jsonl"
         */

        // Arrange
        var (factory, _) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Authoring, _tracesDir, factory);

        // Act
        session.OnEnterAuthorMode(66130u);

        // Assert
        Assert.True(session.IsFileOpen,         "Authoring: IsFileOpen must be true after OnEnterAuthorMode");
        Assert.True(session.IsWriteGateOpen,     "Authoring: IsWriteGateOpen must be true after OnEnterAuthorMode");
        Assert.NotNull(session.CurrentFilePath);
        Assert.Contains("66130", Path.GetFileName(session.CurrentFilePath));
    }

    [Fact]
    public void Authoring_WriteAfterOnEnterAuthorMode_ReachesInnerWriter()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Authoring with OnEnterAuthorMode() called,
         *           When Write is called,
         *           Then the event reaches the inner writer.
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Authoring, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);

        // Act
        session.Write(MakeRunStart());

        // Assert
        Assert.Equal(1, getWriter()!.Count);
    }

    [Fact]
    public void Authoring_OnOpenRecordModal_DoesNotChangeGate()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Authoring with file open and gate open,
         *           When OnOpenRecordModal(questId) is called,
         *           Then IsWriteGateOpen remains true (gate is unaffected in Authoring mode).
         *
         * BUILDER GUIDANCE: OnOpenRecordModal is a Recording-mode concern; in Authoring
         *   mode it is a no-op for the gate.
         */

        // Arrange
        var (factory, _) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Authoring, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);

        // Act
        session.OnOpenRecordModal(66130u);

        // Assert
        Assert.True(session.IsWriteGateOpen, "Authoring: OnOpenRecordModal must not close the gate");
        Assert.True(session.IsFileOpen,       "Authoring: file must still be open after OnOpenRecordModal");
    }

    [Fact]
    public void Authoring_OnExitAuthoring_FileClosesGateCloses()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Authoring with OnEnterAuthorMode() called,
         *           When OnExitAuthoring() is called,
         *           Then IsFileOpen=false, IsWriteGateOpen=false, CurrentFilePath=null.
         */

        // Arrange
        var (factory, _) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Authoring, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);

        // Act
        session.OnExitAuthoring();

        // Assert
        Assert.False(session.IsFileOpen,         "Authoring: IsFileOpen must be false after OnExitAuthoring");
        Assert.False(session.IsWriteGateOpen,     "Authoring: IsWriteGateOpen must be false after OnExitAuthoring");
        Assert.Null(session.CurrentFilePath);
    }

    [Fact]
    public void Authoring_RunEndEventBeforeOnExitAuthoring_WrittenNormally()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Authoring with file open and gate open,
         *           When Write(RunEndEvent) is called before OnExitAuthoring(),
         *           Then the RunEndEvent is written normally (gate is open so
         *           it passes both the gate check and the file-open check).
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Authoring, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);

        // Act
        session.Write(MakeRunEnd());

        // Assert
        Assert.Equal(1, getWriter()!.Count);
        Assert.IsType<RunEndEvent>(getWriter()!.RecordedEvents[0]);
    }

    [Fact]
    public void Authoring_SecondOnEnterAuthorModeAfterOnExitAuthoring_NewFileAndDedupCleared()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Authoring, after a complete open/close cycle,
         *           When OnEnterAuthorMode is called a second time,
         *           Then a new file is opened (IsFileOpen=true) and the dedup cache
         *           is cleared so a previously-seen observation re-emits.
         *
         * BUILDER GUIDANCE: Dedup is cleared whenever a new file is opened.
         *   This test uses WriteObservation to confirm the cache is empty after re-open.
         */

        // Arrange
        var writerList = new List<FakeTraceWriter>();
        ITraceWriter Factory(string _) { var fw = new FakeTraceWriter(); writerList.Add(fw); return fw; }
        using var session = new TraceSession(TraceMode.Authoring, _tracesDir, Factory);

        // First session: write an observation, then exit
        session.OnEnterAuthorMode(66130u);
        session.WriteObservation("GetPlayerZone", JsonSerializer.SerializeToElement(182), JsonSerializer.SerializeToElement(182), "run-001", _now);
        session.OnExitAuthoring();

        // Act: second session — same observation should re-emit because dedup was cleared
        session.OnEnterAuthorMode(66130u);
        session.WriteObservation("GetPlayerZone", JsonSerializer.SerializeToElement(182), JsonSerializer.SerializeToElement(182), "run-001", _now);

        // Assert
        Assert.True(session.IsFileOpen, "Second session: IsFileOpen must be true");
        Assert.Equal(2, writerList.Count); // Two writers created — one per session
        Assert.Equal(1, writerList[1].Count); // Dedup was cleared — observation re-emitted in second session
    }

    // =========================================================================
    // Recording mode (10 tests)
    // =========================================================================

    [Fact]
    public void Recording_WriteBeforeOnOpenRecordModal_Dropped()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Recording,
         *           When Write is called before any modal has been opened (gate closed),
         *           Then the event is dropped.
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Recording, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u); // puts session in recording context but no file yet

        // Act
        session.Write(MakeRunStart());

        // Assert
        Assert.Null(getWriter()); // factory never called — gate was closed, no file
    }

    [Fact]
    public void Recording_OnEnterAuthorMode_NoFileYetGateClosed()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Recording,
         *           When OnEnterAuthorMode(questId) is called,
         *           Then IsFileOpen=false and IsWriteGateOpen=false.
         *           (File only opens on OnOpenRecordModal.)
         */

        // Arrange
        using var session = new TraceSession(TraceMode.Recording, _tracesDir);

        // Act
        session.OnEnterAuthorMode(66130u);

        // Assert
        Assert.False(session.IsFileOpen,        "Recording: no file should open on OnEnterAuthorMode");
        Assert.False(session.IsWriteGateOpen,    "Recording: gate must be closed after OnEnterAuthorMode");
    }

    [Fact]
    public void Recording_OnOpenRecordModalFirst_FileOpenGateOpenDedupClearedFilenameContainsQuestId()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Recording with OnEnterAuthorMode() called,
         *           When OnOpenRecordModal(66130u) is called for the first time,
         *           Then IsFileOpen=true, IsWriteGateOpen=true, dedup is cleared,
         *           and CurrentFilePath filename contains "66130".
         */

        // Arrange
        var (factory, _) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Recording, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);

        // Act
        session.OnOpenRecordModal(66130u);

        // Assert
        Assert.True(session.IsFileOpen,         "Recording: IsFileOpen must be true after first OnOpenRecordModal");
        Assert.True(session.IsWriteGateOpen,     "Recording: IsWriteGateOpen must be true after first OnOpenRecordModal");
        Assert.NotNull(session.CurrentFilePath);
        Assert.Contains("66130", Path.GetFileName(session.CurrentFilePath));
    }

    [Fact]
    public void Recording_WriteDuringOpenWindow_ReachesInnerWriter()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Recording with file open and gate open,
         *           When Write is called,
         *           Then the event reaches the inner writer.
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Recording, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);
        session.OnOpenRecordModal(66130u);

        // Act
        session.Write(MakeRunStart());

        // Assert
        Assert.Equal(1, getWriter()!.Count);
    }

    [Fact]
    public void Recording_OnConfirmRecordStep_GateClosesFileStaysOpen()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Recording with gate open,
         *           When OnConfirmRecordStep() is called,
         *           Then IsWriteGateOpen=false and IsFileOpen remains true.
         */

        // Arrange
        var (factory, _) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Recording, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);
        session.OnOpenRecordModal(66130u);

        // Act
        session.OnConfirmRecordStep();

        // Assert
        Assert.False(session.IsWriteGateOpen, "Recording: gate must close after OnConfirmRecordStep");
        Assert.True(session.IsFileOpen,        "Recording: file must remain open after OnConfirmRecordStep");
    }

    [Fact]
    public void Recording_WriteAfterOnConfirmRecordStep_Dropped()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Recording after OnConfirmRecordStep (gate closed),
         *           When Write(ObservationEvent) is called,
         *           Then the event is dropped (gate is closed).
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Recording, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);
        session.OnOpenRecordModal(66130u);
        session.OnConfirmRecordStep();
        var countAtConfirm = getWriter()!.Count;

        // Act
        session.Write(MakeObservation());

        // Assert
        Assert.Equal(countAtConfirm, getWriter()!.Count); // nothing written — gate is closed
    }

    [Fact]
    public void Recording_SecondOnOpenRecordModal_GateOpensFileNotReopened()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Recording after OnConfirmRecordStep (gate closed, file open),
         *           When OnOpenRecordModal is called a second time,
         *           Then IsWriteGateOpen=true, IsFileOpen=true, and CurrentFilePath
         *           is the SAME path as after the first OnOpenRecordModal.
         */

        // Arrange
        var (factory, _) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Recording, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);
        session.OnOpenRecordModal(66130u);
        var pathAfterFirst = session.CurrentFilePath;
        session.OnConfirmRecordStep();

        // Act
        session.OnOpenRecordModal(66130u);

        // Assert
        Assert.True(session.IsWriteGateOpen,  "Recording: gate must reopen on second OnOpenRecordModal");
        Assert.True(session.IsFileOpen,        "Recording: file must still be open");
        Assert.Equal(pathAfterFirst, session.CurrentFilePath); // same file — not reopened
    }

    [Fact]
    public void Recording_WriteInSecondWindow_ReachesInnerWriter()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Recording in the second modal window (gate reopened),
         *           When Write is called,
         *           Then the event reaches the inner writer.
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Recording, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);
        session.OnOpenRecordModal(66130u);
        session.OnConfirmRecordStep();
        session.OnOpenRecordModal(66130u); // second window

        // Act
        session.Write(MakeRunStart());

        // Assert
        Assert.Equal(1, getWriter()!.Count);
    }

    [Fact]
    public void Recording_OnExitAuthoring_FileCloses()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Recording with a file open,
         *           When OnExitAuthoring() is called,
         *           Then IsFileOpen=false and CurrentFilePath=null.
         */

        // Arrange
        var (factory, _) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Recording, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);
        session.OnOpenRecordModal(66130u);

        // Act
        session.OnExitAuthoring();

        // Assert
        Assert.False(session.IsFileOpen,  "Recording: IsFileOpen must be false after OnExitAuthoring");
        Assert.Null(session.CurrentFilePath);
    }

    [Fact]
    public void Recording_RunEndEventAfterConfirm_BypassesGateReachesInnerWriter()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Recording with gate CLOSED but file still open
         *           (i.e., after OnConfirmRecordStep, before OnExitAuthoring),
         *           When Write(RunEndEvent) is called,
         *           Then the RunEndEvent bypasses the gate and reaches the inner writer.
         *
         * BUILDER GUIDANCE: Only RunEndEvent bypasses the gate. Check
         *   `evt is RunEndEvent` before the gate check.
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Recording, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);
        session.OnOpenRecordModal(66130u);
        session.OnConfirmRecordStep(); // gate now closed, file still open

        // Act
        session.Write(MakeRunEnd());

        // Assert
        Assert.Equal(1, getWriter()!.Count);
        Assert.IsType<RunEndEvent>(getWriter()!.RecordedEvents[0]);
    }

    // =========================================================================
    // Dedup (5 tests)
    // =========================================================================

    [Fact]
    public void Dedup_WriteObservationNewMethodArg_Written()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given a fresh (empty dedup cache) TraceSession with file open,
         *           When WriteObservation is called with a new (method, argument) pair,
         *           Then the observation is written to the inner writer.
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();

        // Act
        session.WriteObservation(
            "GetPlayerZone",
            JsonSerializer.SerializeToElement((object?)null),
            JsonSerializer.SerializeToElement(182),
            "run-001",
            _now);

        // Assert
        Assert.Equal(1, getWriter()!.Count);
    }

    [Fact]
    public void Dedup_WriteObservationSameMethodArgValueSecondCall_NotWritten()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given WriteObservation was called once with (method, arg, value),
         *           When the exact same call is repeated,
         *           Then the second call is suppressed (dedup hit).
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();
        var argElement  = JsonSerializer.SerializeToElement((object?)null);
        var valueElement = JsonSerializer.SerializeToElement(182);

        // Act
        session.WriteObservation("GetPlayerZone", argElement, valueElement, "run-001", _now);
        session.WriteObservation("GetPlayerZone", argElement, valueElement, "run-001", _now);

        // Assert
        Assert.Equal(1, getWriter()!.Count); // second call suppressed by dedup
    }

    [Fact]
    public void Dedup_WriteObservationSameMethodArgDifferentValue_Written()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given WriteObservation was called with (method, arg, value=182),
         *           When WriteObservation is called with the same method+arg but value=183,
         *           Then the second call is written (value changed).
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();
        var argElement = JsonSerializer.SerializeToElement((object?)null);

        // Act
        session.WriteObservation("GetPlayerZone", argElement, JsonSerializer.SerializeToElement(182), "run-001", _now);
        session.WriteObservation("GetPlayerZone", argElement, JsonSerializer.SerializeToElement(183), "run-001", _now);

        // Assert
        Assert.Equal(2, getWriter()!.Count); // value changed — second call must be written
    }

    [Fact]
    public void Dedup_ClearedOnFileOpen_SameObservationReEmitsAfterReopen()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Authoring:
         *           session opened → observation written → session closed and reopened →
         *           same observation emitted again,
         *           Then the re-opened session emits the observation (dedup cleared on open).
         */

        // Arrange
        var writerList = new List<FakeTraceWriter>();
        ITraceWriter Factory(string _) { var fw = new FakeTraceWriter(); writerList.Add(fw); return fw; }
        using var session = new TraceSession(TraceMode.Authoring, _tracesDir, Factory);
        var argElement   = JsonSerializer.SerializeToElement((object?)null);
        var valueElement = JsonSerializer.SerializeToElement(182);

        // First window — write observation
        session.OnEnterAuthorMode(66130u);
        session.WriteObservation("GetPlayerZone", argElement, valueElement, "run-001", _now);
        session.OnExitAuthoring();

        // Act — second window
        session.OnEnterAuthorMode(66130u);
        session.WriteObservation("GetPlayerZone", argElement, valueElement, "run-001", _now);

        // Assert
        Assert.Equal(2, writerList.Count);
        Assert.Equal(1, writerList[0].Count); // first session wrote once
        Assert.Equal(1, writerList[1].Count); // second session wrote once — dedup was cleared
    }

    [Fact]
    public void Dedup_Recording_NotClearedOnSecondOpenRecordModal_SameValueSuppressed()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Recording: first modal window writes an observation,
         *           OnConfirmRecordStep closes gate, second OnOpenRecordModal reopens gate
         *           (but does NOT clear dedup),
         *           When the same observation is submitted in the second window,
         *           Then it is suppressed (dedup was NOT cleared).
         *
         * BUILDER GUIDANCE: Dedup is only cleared when the file is first opened.
         *   Subsequent OnOpenRecordModal calls reopen the gate but must NOT reset dedup.
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Recording, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);
        session.OnOpenRecordModal(66130u); // first window — dedup cleared here
        var argElement   = JsonSerializer.SerializeToElement((object?)null);
        var valueElement = JsonSerializer.SerializeToElement(182);

        // Write in first window
        session.WriteObservation("GetPlayerZone", argElement, valueElement, "run-001", _now);
        session.OnConfirmRecordStep(); // gate closes

        // Act — second window (dedup NOT cleared)
        session.OnOpenRecordModal(66130u);
        session.WriteObservation("GetPlayerZone", argElement, valueElement, "run-001", _now);

        // Assert
        Assert.Equal(1, getWriter()!.Count); // second observation suppressed — dedup persists
    }

    // =========================================================================
    // RunEndEvent gate-bypass (3 tests)
    // =========================================================================

    [Fact]
    public void RunEndBypass_GateClosed_FileOpen_RunEndEventReachesWriter()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Recording with gate CLOSED and file OPEN
         *           (between OnConfirmRecordStep and OnExitAuthoring),
         *           When Write(RunEndEvent) is called,
         *           Then the RunEndEvent reaches the inner writer despite the closed gate.
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Recording, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);
        session.OnOpenRecordModal(66130u);
        session.OnConfirmRecordStep(); // gate now closed

        // Act
        session.Write(MakeRunEnd());

        // Assert
        Assert.Equal(1, getWriter()!.Count);
        Assert.IsType<RunEndEvent>(getWriter()!.RecordedEvents[0]);
    }

    [Fact]
    public void RunEndBypass_GateClosed_FileOpen_ObservationEventDropped()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given the same gate-closed / file-open state,
         *           When Write(ObservationEvent) is called,
         *           Then the ObservationEvent is dropped (only RunEndEvent bypasses the gate).
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Recording, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);
        session.OnOpenRecordModal(66130u);
        session.OnConfirmRecordStep(); // gate now closed

        // Act
        session.Write(MakeObservation());

        // Assert
        Assert.Equal(0, getWriter()!.Count); // dropped — gate is closed and ObservationEvent does not bypass it
    }

    [Fact]
    public void RunEndBypass_GateClosed_FileOpen_RunStartEventDropped()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given the same gate-closed / file-open state,
         *           When Write(RunStartEvent) is called,
         *           Then the RunStartEvent is dropped (only RunEndEvent bypasses the gate).
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Recording, _tracesDir, factory);
        session.OnEnterAuthorMode(66130u);
        session.OnOpenRecordModal(66130u);
        session.OnConfirmRecordStep(); // gate now closed

        // Act
        session.Write(MakeRunStart());

        // Assert
        Assert.Equal(0, getWriter()!.Count); // dropped — only RunEndEvent bypasses the gate
    }

    // =========================================================================
    // Error handling (3 tests)
    // =========================================================================

    [Fact]
    public void Error_WriterFactoryThrows_NoCrashIsFileOpenFalse()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given a writerFactory that throws,
         *           When OnPluginStart() is called (TraceMode.Always),
         *           Then no exception escapes the method and IsFileOpen=false.
         *
         * BUILDER GUIDANCE: Wrap writerFactory invocation in try/catch and leave
         *   IsFileOpen=false if it throws.
         */

        // Arrange
        ITraceWriter ThrowingFactory(string _) => throw new IOException("disk full");
        using var session = new TraceSession(TraceMode.Always, _tracesDir, ThrowingFactory);

        // Act — must not throw
        var exception = Record.Exception(() => session.OnPluginStart());

        // Assert
        Assert.Null(exception);
        Assert.False(session.IsFileOpen, "IsFileOpen must be false when factory threw");
    }

    [Fact]
    public void Error_InnerWriterWriteThrows_ExceptionSwallowedSessionUsable()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given an inner writer whose Write always throws,
         *           When TraceSession.Write is called,
         *           Then no exception escapes and the session remains usable
         *           (subsequent calls do not throw either).
         *
         * BUILDER GUIDANCE: Wrap inner Write in try/catch inside TraceSession.Write.
         */

        // Arrange
        var throwingWriter = new ThrowingFakeTraceWriter();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, _ => throwingWriter);
        session.OnPluginStart();

        // Act
        var ex1 = Record.Exception(() => session.Write(MakeRunStart()));
        var ex2 = Record.Exception(() => session.Write(MakeRunStart())); // second call also must not throw

        // Assert
        Assert.Null(ex1);
        Assert.Null(ex2);
    }

    [Fact]
    public void Error_DisposeCalledTwice_NoException()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given a TraceSession,
         *           When Dispose is called twice,
         *           Then no exception is thrown.
         *
         * BUILDER GUIDANCE: Use a standard _disposed guard.
         */

        // Arrange
        using var session = new TraceSession(TraceMode.Always, _tracesDir);
        session.OnPluginStart();

        // Act
        session.Dispose();
        var ex = Record.Exception(() => session.Dispose());

        // Assert
        Assert.Null(ex);
    }

    // =========================================================================
    // Thread safety (1 test)
    // =========================================================================

    [Fact]
    public async Task ThreadSafety_FiftyConcurrentWrites_AllArrivedWithoutException()
    {
        /*
         * RED: Will fail until TraceSession exists.
         *
         * CONTRACT: Given TraceMode.Always with OnPluginStart() called,
         *           When 50 concurrent calls to Write are made from parallel tasks,
         *           Then all 50 events arrive at the inner writer and no exception escapes.
         *
         * BUILDER GUIDANCE: TraceSession.Write must hold a lock while dispatching to the
         *   inner writer. FakeTraceWriter is already lock-safe on its side.
         */

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();

        const int count = 50;
        var tasks = Enumerable
            .Range(0, count)
            .Select(i => Task.Run(() => session.Write(MakeRunStart($"run-{i:D3}"))))
            .ToArray();

        // Act
        var ex = await Record.ExceptionAsync(() => Task.WhenAll(tasks));

        // Assert
        Assert.Null(ex);
        Assert.Equal(count, getWriter()!.Count);
    }

    // =========================================================================
    // Private helper: inner writer that always throws on Write
    // =========================================================================

    private sealed class ThrowingFakeTraceWriter : ITraceWriter
    {
        public void Write(TraceEvent evt) =>
            throw new InvalidOperationException("Simulated inner writer failure");
    }

    // =========================================================================
    // Debounce helpers
    // =========================================================================

    private static DecisionEvent MakeDecision(
        string stepId,
        string actionType = "navigate",
        string runId = "run-001") =>
        new DecisionEvent(runId, stepId, actionType, _now);

    private static string FormatEvents(FakeTraceWriter w) =>
        string.Join("; ", w.RecordedEvents.Select(e => $"[{e.Type}]"));

    // =========================================================================
    // Group A — decision debounce
    // (RED: A1-A8 fail because debounce is not yet implemented)
    // =========================================================================

    [Fact]
    public void A1_ConsecutiveIdenticalDecisions_CollapseToOne()
    {
        // A1: three consecutive identical DecisionEvent(run, "travel-to-x", "navigate")
        //     → inner writer receives exactly ONE decision.

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        // Act
        session.Write(MakeDecision("travel-to-x"));
        session.Write(MakeDecision("travel-to-x"));
        session.Write(MakeDecision("travel-to-x"));

        // Assert
        var inner = getWriter()!;
        Assert.Equal(1, inner.Count);
        Assert.IsType<DecisionEvent>(inner.RecordedEvents[0]);
    }

    [Fact]
    public void A2_DifferentStepId_BothWritten()
    {
        // A2: decision(run,"a","navigate") then decision(run,"b","navigate")
        //     → BOTH written (different stepId = new transition).

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        // Act
        session.Write(MakeDecision("a", "navigate"));
        session.Write(MakeDecision("b", "navigate"));

        // Assert
        var inner = getWriter()!;
        Assert.Equal(2, inner.Count);
    }

    [Fact]
    public void A3_SameStepIdDifferentActionType_BothWritten()
    {
        // A3: decision(run,"a","navigate") then decision(run,"a","interact")
        //     → BOTH written (same stepId but different actionType = new transition).

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        // Act
        session.Write(MakeDecision("a", "navigate"));
        session.Write(MakeDecision("a", "interact"));

        // Assert
        var inner = getWriter()!;
        Assert.Equal(2, inner.Count);
    }

    [Fact]
    public void A4_RecurringTransitionAfterDifferentOne_ThreeDecisionsWritten()
    {
        // A4: navigate(a) → interact(a) → navigate(a)
        //     → THREE decisions written (debounce, not full dedup; recurring nav(a)
        //     is not suppressed because interact(a) came between).

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        // Act
        session.Write(MakeDecision("a", "navigate"));
        session.Write(MakeDecision("a", "interact"));
        session.Write(MakeDecision("a", "navigate")); // recurring — must NOT be suppressed

        // Assert
        var inner = getWriter()!;
        Assert.Equal(3, inner.Count);
    }

    [Fact]
    public void A5_NonDecisionEventBetweenIdenticalDecisions_DoesNotResetDebounce()
    {
        // A5: decision(run,"a","navigate"), then ObservationEvent, then
        //     decision(run,"a","navigate") → exactly ONE decision (and the observation)
        //     written. The interleaved non-decision event must NOT reset the debounce.

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        // Act
        session.Write(MakeDecision("a", "navigate"));
        session.Write(MakeObservation());                  // non-decision between identical decisions
        session.Write(MakeDecision("a", "navigate"));      // should be suppressed

        // Assert
        var inner = getWriter()!;
        var decisions = inner.RecordedEvents.OfType<DecisionEvent>().ToList();
        var observations = inner.RecordedEvents.OfType<ObservationEvent>().ToList();
        Assert.Single(decisions);
        Assert.Single(observations);
        Assert.Equal(2, inner.Count); // 1 decision + 1 observation
    }

    [Fact]
    public void A6_NonDecisionEventsPassThroughUnchanged()
    {
        // A6: Write a RunStartEvent, ObservationEvent, RunEndEvent
        //     → all three reach the inner writer (debounce never touches non-decisions).

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        // Act
        session.Write(MakeRunStart());
        session.Write(MakeObservation());
        session.Write(MakeRunEnd()); // RunEndEvent bypasses gate — verify it is not filtered by debounce

        // Assert
        var inner = getWriter()!;
        Assert.Equal(3, inner.Count);
        Assert.IsType<RunStartEvent>(inner.RecordedEvents[0]);
        Assert.IsType<ObservationEvent>(inner.RecordedEvents[1]);
        Assert.IsType<RunEndEvent>(inner.RecordedEvents[2]);
    }

    [Fact]
    public void A7_FileReopenClearsDebounce()
    {
        // A7: QuestRun mode — OnQuestRunStart; decision(run,"a","navigate");
        //     OnQuestRunEnd; OnQuestRunStart again (new file);
        //     decision(run,"a","navigate") → the new file's writer received the decision
        //     (file reopen clears debounce — not suppressed across files).

        // Arrange: factory that accumulates all per-file writers in a list
        var writerList = new List<FakeTraceWriter>();
        ITraceWriter Factory(string _) { var fw = new FakeTraceWriter(); writerList.Add(fw); return fw; }

        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, Factory);

        // First file
        session.OnQuestRunStart(66130u);
        session.Write(MakeDecision("a", "navigate"));
        session.OnQuestRunEnd();

        // Act: second file — same decision key
        session.OnQuestRunStart(66130u);
        session.Write(MakeDecision("a", "navigate")); // must NOT be suppressed — new file cleared debounce

        // Assert
        Assert.Equal(2, writerList.Count);
        Assert.Equal(1, writerList[0].Count); // first file had one decision
        Assert.Equal(1, writerList[1].Count); // second file also wrote the decision (debounce cleared)
    }

    [Fact]
    public void A8_DifferentRunIdInAlwaysMode_BothWritten()
    {
        // A8: Always mode (single file) — decision("run1","a","navigate") then
        //     decision("run2","a","navigate") → BOTH written (key includes RunId).

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.Always, _tracesDir, factory);
        session.OnPluginStart();

        // Act
        session.Write(MakeDecision("a", "navigate", runId: "run1"));
        session.Write(MakeDecision("a", "navigate", runId: "run2")); // different RunId = new key

        // Assert
        var inner = getWriter()!;
        Assert.Equal(2, inner.Count);
    }

    [Fact]
    public void A9_ConsecutiveNullStepIdDecisions_CollapseToOne()
    {
        // A9: two consecutive DecisionEvent with StepId == null and same actionType
        //     → exactly ONE written. Pins the `StepId ?? ""` coalesce so a null-stepId
        //     (terminal/cross-step) decision is debounced like any other.

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        // Act
        session.Write(new DecisionEvent("run-001", null, "navigate", _now));
        session.Write(new DecisionEvent("run-001", null, "navigate", _now)); // suppressed

        // Assert
        var inner = getWriter()!;
        Assert.Equal(1, inner.Count);
        Assert.IsType<DecisionEvent>(inner.RecordedEvents[0]);
    }

    [Fact]
    public void A10_NullStepIdAndEmptyStepId_TreatedAsSameKey()
    {
        // A10: decision(null stepId) then decision("" stepId), same runId/actionType
        //      → exactly ONE written. The `?? ""` coalesce makes null and "" collapse to
        //      the same key; pins that against future key refactors.

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        // Act
        session.Write(new DecisionEvent("run-001", null, "navigate", _now));
        session.Write(new DecisionEvent("run-001", "", "navigate", _now)); // same key as null

        // Assert
        var inner = getWriter()!;
        Assert.Equal(1, inner.Count);
    }

    // =========================================================================
    // Group B — run.start dedup
    // (RED: B1-B3 fail because run.start dedup is not yet implemented)
    // =========================================================================

    [Fact]
    public void B1_DuplicateRunStartSameRunId_OnlyOneWritten()
    {
        // B1: two RunStartEvent with same RunId → exactly ONE written.

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        // Act
        session.Write(MakeRunStart("run-001"));
        session.Write(MakeRunStart("run-001")); // duplicate — must be suppressed

        // Assert
        var inner = getWriter()!;
        var runStarts = inner.RecordedEvents.OfType<RunStartEvent>().ToList();
        Assert.Single(runStarts);
    }

    [Fact]
    public void B2_RunStartDifferentRunIds_BothWritten()
    {
        // B2: two RunStartEvent with different RunIds → BOTH written.

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);

        // Act
        session.Write(MakeRunStart("run-001"));
        session.Write(MakeRunStart("run-002")); // different RunId = not a duplicate

        // Assert
        var inner = getWriter()!;
        var runStarts = inner.RecordedEvents.OfType<RunStartEvent>().ToList();
        Assert.Equal(2, runStarts.Count);
    }

    [Fact]
    public void B3_RunStartDedupClearedOnFileReopen()
    {
        // B3: run.start(r1), close file, reopen, run.start(r1)
        //     → reopened file received it (dedup cleared on file open).

        // Arrange
        var writerList = new List<FakeTraceWriter>();
        ITraceWriter Factory(string _) { var fw = new FakeTraceWriter(); writerList.Add(fw); return fw; }

        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, Factory);

        // First file
        session.OnQuestRunStart(66130u);
        session.Write(MakeRunStart("run-001"));
        session.OnQuestRunEnd();

        // Act: reopen file and write same RunId again
        session.OnQuestRunStart(66130u);
        session.Write(MakeRunStart("run-001")); // must NOT be suppressed — new file cleared dedup

        // Assert
        Assert.Equal(2, writerList.Count);
        Assert.Equal(1, writerList[0].Count); // first file wrote one run.start
        Assert.Equal(1, writerList[1].Count); // second file also wrote it (dedup cleared)
    }

    // =========================================================================
    // Group C — consumer-safety regression (should PASS even before implementation)
    // =========================================================================

    [Fact]
    public void C1_ObservationDedup_StillSuppressesUnchangedRepeatedValue()
    {
        // C1: existing WriteObservation dedup behavior is unchanged.
        // Regression guard: two identical WriteObservation calls → only one ObservationEvent
        // reaches the inner writer. This test exercises the pre-existing dedup and must
        // pass even now (no debounce implementation needed).

        // Arrange
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        session.OnQuestRunStart(66130u);
        var arg   = System.Text.Json.JsonSerializer.SerializeToElement((object?)null);
        var value = System.Text.Json.JsonSerializer.SerializeToElement(182);

        // Act
        session.WriteObservation("GetPlayerZone", arg, value, "run-001", _now);
        session.WriteObservation("GetPlayerZone", arg, value, "run-001", _now); // unchanged — must suppress

        // Assert
        var inner = getWriter()!;
        var observations = inner.RecordedEvents.OfType<ObservationEvent>().ToList();
        Assert.Single(observations);
    }

    // C2: EngineFixtureTests unaffected — CapturingTraceWriter bypasses TraceSession
    // entirely, so the terminal-detection loop sees every per-tick decision. This is
    // verified by running: dotnet test QuestForge.Engine.Tests --filter EngineFixture
    // No unit test is added here because it would duplicate that integration check.

    [Fact]
    public void C3_DecisionWithNoFileOpen_DroppedWithoutException()
    {
        // C3: a decision written while no file is open (gate closed) is dropped —
        // same as today. The debounce guard must NOT change drop behavior.
        // (The debounce guard runs only after the existing file-open and gate checks.)

        // Arrange: session constructed but lifecycle never called — no file open
        var (factory, getWriter) = MakeCaptureFactory();
        using var session = new TraceSession(TraceMode.QuestRun, _tracesDir, factory);
        // Intentionally do NOT call OnQuestRunStart — no file open, gate closed.

        // Act
        var ex = Record.Exception(() => session.Write(MakeDecision("a", "navigate")));

        // Assert
        Assert.Null(ex);                // must not throw
        Assert.Null(getWriter());       // factory never invoked — no file was created
    }
}
