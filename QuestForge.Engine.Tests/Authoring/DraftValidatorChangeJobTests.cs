using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: DraftValidator tests for ChangeJobStep.
/// Spec: docs/CHANGE_JOB_STEP_PLAN.md -- scenarios CJ15-CJ16.
///
/// CJ15: W1 suppressed for ChangeJobStep without Expect (requires W1 guard extension).
/// CJ16: E18 fires for ChangeJobStep with JobId == 0 (already landed in #122, should pass).
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~DraftValidatorChangeJobTests"
/// </summary>
public sealed class DraftValidatorChangeJobTests
{
    // =========================================================================
    // CJ15 -- W1 suppressed for ChangeJobStep without Expect
    // =========================================================================

    [Fact]
    public void Validate_ChangeJobStep_NoExpect_W1Suppressed_CJ15()
    {
        // CONTRACT: Given ChangeJobStep { JobId = 32, Expect = null },
        //           When DraftValidator.Validate(draft),
        //           Then warnings does NOT contain W1 (implicit postcondition).
        //           And errors does NOT contain E18 (JobId is non-zero).

        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("change-job-no-expect-cj15", 2,
            new ChangeJobStep
            {
                Id = "change-job-no-expect-cj15",
                JobId = 32,
                Expect = null  // missing Expect -- but W1 is suppressed per CJ-9
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        // W1 must NOT fire for ChangeJobStep (implicit postcondition makes it non-spin-loop)
        Assert.DoesNotContain(result.Warnings, w => w.Code == "W1");
        // No errors for well-formed JobId
        Assert.DoesNotContain(result.Errors, e => e.Code == "E18");
    }

    // =========================================================================
    // CJ16 -- E18 fires for ChangeJobStep with JobId == 0
    // =========================================================================

    [Fact]
    public void Validate_ChangeJobStep_JobIdZero_E18Fires_CJ16()
    {
        // CONTRACT: Given ChangeJobStep { JobId = 0 },
        //           When DraftValidator.Validate(draft),
        //           Then errors contains E18 for the change-job step.

        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("change-job-zero-cj16", 2,
            new ChangeJobStep
            {
                Id = "change-job-zero-cj16",
                JobId = 0
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        // E18 fires for ChangeJobStep.JobId == 0
        Assert.Contains(result.Errors, e => e.Code == "E18");
    }
}
