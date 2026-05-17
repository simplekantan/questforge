// WHY: InventoryTraceEmissionTests uses TraceWriter directly without an explicit
// using directive. This global using makes it available across the test project,
// consistent with how TraceWriterTests.cs imports it explicitly.
global using QuestForge.Engine.Tracing;
