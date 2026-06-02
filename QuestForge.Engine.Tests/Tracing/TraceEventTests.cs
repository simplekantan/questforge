using System.Text.Json;
using QuestForge.Adapters.Tracing;
using Xunit;

namespace QuestForge.Engine.Tests.Tracing;

/// <summary>
/// RED PHASE: Tests for the new TraceEvent hierarchy with envelope fields (v, seq, ts),
/// RunId on base, nested Data records, and no At parameter.
///
/// Covers TS1-TS10, TS18-TS20 from TRACE_SPEC_COMPLIANCE_PLAN.md.
///
/// All tests WILL FAIL because the new type shapes do not exist yet.
/// The builder must refactor TraceEvent and all 7 derived types to make these pass.
/// </summary>
public sealed class TraceEventTests
{
    private static string FormatErrors(IEnumerable<string> issues) =>
        string.Join("; ", issues);

    // =========================================================================
    // TS1: RunStartEvent serialization shape
    // =========================================================================

    [Fact]
    public void TS01_RunStartEvent_SerializationShape()
    {
        /*
         * CONTRACT: Given a RunStartEvent with RunId="abc", V=1, Seq=0, Ts=0,
         *           Data.QuestId=65, Data.SchemaVer="1.0", Data.PluginVer="0.4.1",
         *           Data.PatchVer="7.51", Data.WallClockUtc=2026-06-02T12:00:00Z
         *           When serialized via TraceEventJsonContext.Default.TraceEvent
         *           Then the JSON contains envelope fields at top level,
         *           type-specific fields inside "data", no top-level "questId",
         *           and no "at" field.
         */

        // Arrange
        var evt = new RunStartEvent
        {
            RunId = "abc",
            V = 1,
            Seq = 0,
            Ts = 0,
            Data = new RunStartEvent.RunStartData
            {
                QuestId = 65,
                SchemaVer = "1.0",
                PluginVer = "0.4.1",
                PatchVer = "7.51",
                WallClockUtc = new DateTimeOffset(2026, 6, 2, 12, 0, 0, TimeSpan.Zero)
            }
        };

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(evt, TraceEventJsonContext.Default.TraceEvent);

        // Assert — envelope fields at top level
        Assert.Contains("\"v\":1", json);
        Assert.Contains("\"seq\":0", json);
        Assert.Contains("\"ts\":0", json);
        Assert.Contains("\"type\":\"run.start\"", json);
        Assert.Contains("\"runId\":\"abc\"", json);

        // Assert — type-specific fields inside "data"
        Assert.Contains("\"data\":{", json);
        Assert.Contains("\"questId\":65", json);
        Assert.Contains("\"pluginVer\":\"0.4.1\"", json);
        Assert.Contains("\"patchVer\":\"7.51\"", json);

        // Assert — no top-level questId (must be inside data)
        var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("questId", out _),
            "questId must NOT appear at the top level; it belongs inside 'data'");

        // Assert — no "at" field (removed per TSC1)
        Assert.DoesNotContain("\"at\":", json);
    }

    // =========================================================================
    // TS2: RunEndEvent serialization shape
    // =========================================================================

    [Fact]
    public void TS02_RunEndEvent_SerializationShape()
    {
        /*
         * CONTRACT: Given a RunEndEvent with RunId="abc", Data.Outcome="done"
         *           When serialized
         *           Then JSON contains "type":"run.end", "runId":"abc",
         *           and "data":{"outcome":"done"}
         */

        // Arrange
        var evt = new RunEndEvent
        {
            RunId = "abc",
            Data = new RunEndEvent.RunEndData { Outcome = "done" }
        };

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(evt, TraceEventJsonContext.Default.TraceEvent);

        // Assert
        Assert.Contains("\"type\":\"run.end\"", json);
        Assert.Contains("\"runId\":\"abc\"", json);
        Assert.Contains("\"data\":{", json);
        Assert.Contains("\"outcome\":\"done\"", json);

        var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("outcome", out _),
            "outcome must NOT appear at top level");
    }

    // =========================================================================
    // TS3: ObservationEvent serialization shape
    // =========================================================================

    [Fact]
    public void TS03_ObservationEvent_SerializationShape()
    {
        /*
         * CONTRACT: Given an ObservationEvent with RunId="abc",
         *           Data.Method="GetPlayerZone", Data.Value=130 (as JsonElement)
         *           When serialized
         *           Then JSON contains "type":"observation", "runId":"abc",
         *           "data":{"method":"GetPlayerZone",...,"value":130}
         *           and does NOT contain a top-level "method" key
         */

        // Arrange
        var valueElement = JsonSerializer.SerializeToElement(130);
        var evt = new ObservationEvent
        {
            RunId = "abc",
            Data = new ObservationEvent.ObservationData
            {
                Method = "GetPlayerZone",
                Argument = null,
                Value = valueElement
            }
        };

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(evt, TraceEventJsonContext.Default.TraceEvent);

        // Assert
        Assert.Contains("\"type\":\"observation\"", json);
        Assert.Contains("\"runId\":\"abc\"", json);
        Assert.Contains("\"method\":\"GetPlayerZone\"", json);
        Assert.Contains("\"value\":130", json);

        var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("method", out _),
            "method must NOT appear at the top level; it belongs inside 'data'");
    }

    // =========================================================================
    // TS4: DecisionEvent serialization shape
    // =========================================================================

    [Fact]
    public void TS04_DecisionEvent_SerializationShape()
    {
        /*
         * CONTRACT: Given a DecisionEvent with RunId="abc",
         *           Data.StepId="go-to-npc", Data.ActionType="Navigate"
         *           When serialized
         *           Then JSON contains "type":"decision", "runId":"abc",
         *           "data":{"stepId":"go-to-npc","actionType":"Navigate"}
         */

        // Arrange
        var evt = new DecisionEvent
        {
            RunId = "abc",
            Data = new DecisionEvent.DecisionData
            {
                StepId = "go-to-npc",
                ActionType = "Navigate"
            }
        };

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(evt, TraceEventJsonContext.Default.TraceEvent);

        // Assert
        Assert.Contains("\"type\":\"decision\"", json);
        Assert.Contains("\"runId\":\"abc\"", json);
        Assert.Contains("\"data\":{", json);
        Assert.Contains("\"stepId\":\"go-to-npc\"", json);
        Assert.Contains("\"actionType\":\"Navigate\"", json);

        var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("stepId", out _),
            "stepId must NOT appear at the top level");
        Assert.False(doc.RootElement.TryGetProperty("actionType", out _),
            "actionType must NOT appear at the top level");
    }

    // =========================================================================
    // TS5: DecisionEvent with null StepId
    // =========================================================================

    [Fact]
    public void TS05_DecisionEvent_NullStepId_SerializesNullInsideData()
    {
        /*
         * CONTRACT: Given a DecisionEvent with Data.StepId = null
         *           When serialized
         *           Then JSON contains "data":{"stepId":null,"actionType":"..."}
         */

        // Arrange
        var evt = new DecisionEvent
        {
            RunId = "abc",
            Data = new DecisionEvent.DecisionData
            {
                StepId = null,
                ActionType = "AwaitUser"
            }
        };

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(evt, TraceEventJsonContext.Default.TraceEvent);

        // Assert
        Assert.Contains("\"stepId\":null", json);
    }

    // =========================================================================
    // TS6: ActionSubmittedEvent serialization shape
    // =========================================================================

    [Fact]
    public void TS06_ActionSubmittedEvent_SerializationShape()
    {
        /*
         * CONTRACT: Given an ActionSubmittedEvent with Data.ActionType="Navigate",
         *           Data.Parameters={"x":10} (JsonElement)
         *           When serialized
         *           Then JSON contains "type":"action.submitted",
         *           "data":{"actionType":"Navigate","parameters":{"x":10}}
         */

        // Arrange
        var paramsElement = JsonSerializer.SerializeToElement(new { x = 10 });
        var evt = new ActionSubmittedEvent
        {
            RunId = "abc",
            Data = new ActionSubmittedEvent.ActionSubmittedData
            {
                ActionType = "Navigate",
                Parameters = paramsElement
            }
        };

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(evt, TraceEventJsonContext.Default.TraceEvent);

        // Assert
        Assert.Contains("\"type\":\"action.submitted\"", json);
        Assert.Contains("\"actionType\":\"Navigate\"", json);
        Assert.Contains("\"parameters\":{\"x\":10}", json);
    }

    // =========================================================================
    // TS7: ActionCompletedEvent serialization shape
    // =========================================================================

    [Fact]
    public void TS07_ActionCompletedEvent_SerializationShape()
    {
        /*
         * CONTRACT: Given an ActionCompletedEvent with Data.ActionType="Navigate",
         *           Data.Outcome="Arrived"
         *           When serialized
         *           Then JSON contains "type":"action.completed",
         *           "data":{"actionType":"Navigate","outcome":"Arrived"}
         */

        // Arrange
        var evt = new ActionCompletedEvent
        {
            RunId = "abc",
            Data = new ActionCompletedEvent.ActionCompletedData
            {
                ActionType = "Navigate",
                Outcome = "Arrived"
            }
        };

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(evt, TraceEventJsonContext.Default.TraceEvent);

        // Assert
        Assert.Contains("\"type\":\"action.completed\"", json);
        Assert.Contains("\"actionType\":\"Navigate\"", json);
        Assert.Contains("\"outcome\":\"Arrived\"", json);
    }

    // =========================================================================
    // TS8: StepRecordedEvent serialization shape
    // =========================================================================

    [Fact]
    public void TS08_StepRecordedEvent_SerializationShape()
    {
        /*
         * CONTRACT: Given a StepRecordedEvent with Data.StepId="talk-npc",
         *           Data.SequenceNumber=0, Data.Step=<JsonElement>
         *           When serialized
         *           Then JSON contains "type":"step.recorded",
         *           "data":{"stepId":"talk-npc","sequenceNumber":0,"step":{...}}
         */

        // Arrange
        var stepElement = JsonSerializer.SerializeToElement(new { type = "talk", npcId = 42 });
        var evt = new StepRecordedEvent
        {
            RunId = "abc",
            Data = new StepRecordedEvent.StepRecordedData
            {
                StepId = "talk-npc",
                SequenceNumber = 0,
                Step = stepElement
            }
        };

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(evt, TraceEventJsonContext.Default.TraceEvent);

        // Assert
        Assert.Contains("\"type\":\"step.recorded\"", json);
        Assert.Contains("\"stepId\":\"talk-npc\"", json);
        Assert.Contains("\"sequenceNumber\":0", json);
        Assert.Contains("\"step\":{", json);
    }

    // =========================================================================
    // TS9: Round-trip deserialization for all 7 types
    // =========================================================================

    [Fact]
    public void TS09_RunStartEvent_RoundTrip()
    {
        /*
         * CONTRACT: Given a RunStartEvent serialized and deserialized via TraceEventJsonContext
         *           Then the result is a RunStartEvent with matching fields
         */

        var original = new RunStartEvent
        {
            RunId = "rt-001",
            V = 1,
            Seq = 3,
            Ts = 100,
            Data = new RunStartEvent.RunStartData { QuestId = 65, SchemaVer = "1.0" }
        };

        var json = JsonSerializer.Serialize<TraceEvent>(original, TraceEventJsonContext.Default.TraceEvent);
        var result = JsonSerializer.Deserialize<TraceEvent>(json, TraceEventJsonContext.Default.TraceEvent);

        var rt = Assert.IsType<RunStartEvent>(result);
        Assert.Equal("rt-001", rt.RunId);
        Assert.Equal(1, rt.V);
        Assert.Equal(3, rt.Seq);
        Assert.Equal(100, rt.Ts);
        Assert.Equal(65u, rt.Data.QuestId);
        Assert.Equal("1.0", rt.Data.SchemaVer);
    }

    [Fact]
    public void TS09_RunEndEvent_RoundTrip()
    {
        var original = new RunEndEvent
        {
            RunId = "rt-001",
            Data = new RunEndEvent.RunEndData { Outcome = "done" }
        };

        var json = JsonSerializer.Serialize<TraceEvent>(original, TraceEventJsonContext.Default.TraceEvent);
        var result = JsonSerializer.Deserialize<TraceEvent>(json, TraceEventJsonContext.Default.TraceEvent);

        var rt = Assert.IsType<RunEndEvent>(result);
        Assert.Equal("done", rt.Data.Outcome);
    }

    [Fact]
    public void TS09_ObservationEvent_RoundTrip()
    {
        var valueElement = JsonSerializer.SerializeToElement(182);
        var original = new ObservationEvent
        {
            RunId = "rt-001",
            Data = new ObservationEvent.ObservationData
            {
                Method = "GetPlayerZone",
                Argument = null,
                Value = valueElement
            }
        };

        var json = JsonSerializer.Serialize<TraceEvent>(original, TraceEventJsonContext.Default.TraceEvent);
        var result = JsonSerializer.Deserialize<TraceEvent>(json, TraceEventJsonContext.Default.TraceEvent);

        var rt = Assert.IsType<ObservationEvent>(result);
        Assert.Equal("GetPlayerZone", rt.Data.Method);
        Assert.Null(rt.Data.Argument);
        Assert.Equal(182, rt.Data.Value!.Value.GetInt32());
    }

    [Fact]
    public void TS09_DecisionEvent_RoundTrip()
    {
        var original = new DecisionEvent
        {
            RunId = "rt-001",
            Data = new DecisionEvent.DecisionData
            {
                StepId = "travel-to-npc",
                ActionType = "Navigate"
            }
        };

        var json = JsonSerializer.Serialize<TraceEvent>(original, TraceEventJsonContext.Default.TraceEvent);
        var result = JsonSerializer.Deserialize<TraceEvent>(json, TraceEventJsonContext.Default.TraceEvent);

        var rt = Assert.IsType<DecisionEvent>(result);
        Assert.Equal("travel-to-npc", rt.Data.StepId);
        Assert.Equal("Navigate", rt.Data.ActionType);
    }

    [Fact]
    public void TS09_ActionSubmittedEvent_RoundTrip()
    {
        var paramsElement = JsonSerializer.SerializeToElement(new { x = 10 });
        var original = new ActionSubmittedEvent
        {
            RunId = "rt-001",
            Data = new ActionSubmittedEvent.ActionSubmittedData
            {
                ActionType = "Navigate",
                Parameters = paramsElement
            }
        };

        var json = JsonSerializer.Serialize<TraceEvent>(original, TraceEventJsonContext.Default.TraceEvent);
        var result = JsonSerializer.Deserialize<TraceEvent>(json, TraceEventJsonContext.Default.TraceEvent);

        var rt = Assert.IsType<ActionSubmittedEvent>(result);
        Assert.Equal("Navigate", rt.Data.ActionType);
    }

    [Fact]
    public void TS09_ActionCompletedEvent_RoundTrip()
    {
        var original = new ActionCompletedEvent
        {
            RunId = "rt-001",
            Data = new ActionCompletedEvent.ActionCompletedData
            {
                ActionType = "Navigate",
                Outcome = "Arrived"
            }
        };

        var json = JsonSerializer.Serialize<TraceEvent>(original, TraceEventJsonContext.Default.TraceEvent);
        var result = JsonSerializer.Deserialize<TraceEvent>(json, TraceEventJsonContext.Default.TraceEvent);

        var rt = Assert.IsType<ActionCompletedEvent>(result);
        Assert.Equal("Navigate", rt.Data.ActionType);
        Assert.Equal("Arrived", rt.Data.Outcome);
    }

    [Fact]
    public void TS09_StepRecordedEvent_RoundTrip()
    {
        var stepElement = JsonSerializer.SerializeToElement(new { type = "talk" });
        var original = new StepRecordedEvent
        {
            RunId = "rt-001",
            Data = new StepRecordedEvent.StepRecordedData
            {
                StepId = "talk-npc",
                SequenceNumber = 2,
                Step = stepElement
            }
        };

        var json = JsonSerializer.Serialize<TraceEvent>(original, TraceEventJsonContext.Default.TraceEvent);
        var result = JsonSerializer.Deserialize<TraceEvent>(json, TraceEventJsonContext.Default.TraceEvent);

        var rt = Assert.IsType<StepRecordedEvent>(result);
        Assert.Equal("talk-npc", rt.Data.StepId);
        Assert.Equal(2, rt.Data.SequenceNumber);
    }

    // =========================================================================
    // TS10: Envelope field ordering in JSON
    // =========================================================================

    [Fact]
    public void TS10_EnvelopeFieldOrdering_VSeqTsTypeRunIdBeforeData()
    {
        /*
         * CONTRACT: Given a DecisionEvent with V=1, Seq=5, Ts=123
         *           When serialized
         *           Then the JSON starts with {"v":1,"seq":5,"ts":123,"type":"decision","runId":"..."
         *           Envelope fields appear before "data".
         */

        // Arrange
        var evt = new DecisionEvent
        {
            RunId = "order-test",
            V = 1,
            Seq = 5,
            Ts = 123,
            Data = new DecisionEvent.DecisionData
            {
                StepId = "step-a",
                ActionType = "Navigate"
            }
        };

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(evt, TraceEventJsonContext.Default.TraceEvent);

        // Assert — verify ordering: v, seq, ts appear before "data"
        var vIdx = json.IndexOf("\"v\":", StringComparison.Ordinal);
        var seqIdx = json.IndexOf("\"seq\":", StringComparison.Ordinal);
        var tsIdx = json.IndexOf("\"ts\":", StringComparison.Ordinal);
        var typeIdx = json.IndexOf("\"type\":", StringComparison.Ordinal);
        var runIdIdx = json.IndexOf("\"runId\":", StringComparison.Ordinal);
        var dataIdx = json.IndexOf("\"data\":", StringComparison.Ordinal);

        Assert.True(vIdx >= 0, "JSON must contain 'v'");
        Assert.True(seqIdx >= 0, "JSON must contain 'seq'");
        Assert.True(tsIdx >= 0, "JSON must contain 'ts'");
        Assert.True(dataIdx >= 0, "JSON must contain 'data'");

        Assert.True(vIdx < seqIdx, $"'v' (at {vIdx}) must appear before 'seq' (at {seqIdx})");
        Assert.True(seqIdx < tsIdx, $"'seq' (at {seqIdx}) must appear before 'ts' (at {tsIdx})");
        Assert.True(tsIdx < dataIdx, $"'ts' (at {tsIdx}) must appear before 'data' (at {dataIdx})");
        Assert.True(typeIdx < dataIdx, $"'type' (at {typeIdx}) must appear before 'data' (at {dataIdx})");
        Assert.True(runIdIdx < dataIdx, $"'runId' (at {runIdIdx}) must appear before 'data' (at {dataIdx})");
    }

    // =========================================================================
    // TS18: run.start metadata fields present
    // =========================================================================

    [Fact]
    public void TS18_RunStartEvent_AllMetadataFieldsPresent()
    {
        /*
         * CONTRACT: Given a RunStartEvent with PluginVer="0.4.1", PatchVer="7.51",
         *           WallClockUtc=2026-06-02T12:00:00Z, EngineConfig=null,
         *           PrecedingRunId=null, NewGamePlus=null
         *           When serialized
         *           Then JSON "data" object contains all fields: questId, schemaVer,
         *           pluginVer, patchVer, wallClockUtc, engineConfig, precedingRunId, newGamePlus
         */

        // Arrange
        var evt = new RunStartEvent
        {
            RunId = "meta-test",
            Data = new RunStartEvent.RunStartData
            {
                QuestId = 65,
                SchemaVer = "1.0",
                PluginVer = "0.4.1",
                PatchVer = "7.51",
                WallClockUtc = new DateTimeOffset(2026, 6, 2, 12, 0, 0, TimeSpan.Zero),
                EngineConfig = null,
                PrecedingRunId = null,
                NewGamePlus = null
            }
        };

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(evt, TraceEventJsonContext.Default.TraceEvent);
        var doc = JsonDocument.Parse(json);
        var dataElement = doc.RootElement.GetProperty("data");

        // Assert — all 8 fields are present in "data"
        var expectedFields = new[]
        {
            "questId", "schemaVer", "pluginVer", "patchVer",
            "wallClockUtc", "engineConfig", "precedingRunId", "newGamePlus"
        };

        var issues = new List<string>();
        foreach (var field in expectedFields)
        {
            if (!dataElement.TryGetProperty(field, out _))
                issues.Add($"Missing field: {field}");
        }
        Assert.True(issues.Count == 0, FormatErrors(issues));
    }

    // =========================================================================
    // TS19: run.start metadata with null optional fields
    // =========================================================================

    [Fact]
    public void TS19_RunStartEvent_NullOptionalFields_SerializeAsNull()
    {
        /*
         * CONTRACT: Given a RunStartEvent with only QuestId and SchemaVer set
         *           (all others null/default)
         *           When serialized
         *           Then JSON "data" object contains "pluginVer":null,
         *           "engineConfig":null, etc.
         */

        // Arrange
        var evt = new RunStartEvent
        {
            RunId = "null-test",
            Data = new RunStartEvent.RunStartData
            {
                QuestId = 65,
                SchemaVer = "1.0"
                // All other fields default to null
            }
        };

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(evt, TraceEventJsonContext.Default.TraceEvent);
        var doc = JsonDocument.Parse(json);
        var dataElement = doc.RootElement.GetProperty("data");

        // Assert — nullable fields serialize as null
        Assert.Equal(JsonValueKind.Null, dataElement.GetProperty("pluginVer").ValueKind);
        Assert.Equal(JsonValueKind.Null, dataElement.GetProperty("patchVer").ValueKind);
        Assert.Equal(JsonValueKind.Null, dataElement.GetProperty("engineConfig").ValueKind);
        Assert.Equal(JsonValueKind.Null, dataElement.GetProperty("precedingRunId").ValueKind);
        Assert.Equal(JsonValueKind.Null, dataElement.GetProperty("newGamePlus").ValueKind);
    }

    // =========================================================================
    // TS20: V defaults to 1
    // =========================================================================

    [Fact]
    public void TS20_V_DefaultsTo1()
    {
        /*
         * CONTRACT: Given a DecisionEvent constructed with default V (not explicitly set)
         *           When serialized
         *           Then JSON contains "v":1
         */

        // Arrange
        var evt = new DecisionEvent
        {
            RunId = "default-v",
            Data = new DecisionEvent.DecisionData
            {
                StepId = "step-a",
                ActionType = "Navigate"
            }
            // V is NOT set explicitly -- should default to 1
        };

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(evt, TraceEventJsonContext.Default.TraceEvent);

        // Assert
        Assert.Contains("\"v\":1", json);
    }
}
