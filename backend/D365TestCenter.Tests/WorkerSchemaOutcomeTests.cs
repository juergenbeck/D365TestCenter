using D365TestCenter.Core;
using D365TestCenter.Core.Config;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Pinnt die jbe_testoutcome- und jbe_stepstatus-OptionSet-Werte in WorkerSchema gegen
/// das ECHTE globale OptionSet (Markant DEV verifiziert 2026-06-21, FB-50). Vorher waren
/// Error/Skipped in WorkerSchema vertauscht (Worker-Pfad schrieb Error/Skipped falsch).
/// Dieser Test hätte FB-50 gefangen.
/// </summary>
public class WorkerSchemaOutcomeTests
{
    [Fact]
    public void Outcome_Values_MatchRealOptionSet()
    {
        Assert.Equal(105710000, WorkerSchema.OutcomePassed);
        Assert.Equal(105710001, WorkerSchema.OutcomeFailed);
        Assert.Equal(105710002, WorkerSchema.OutcomeSkipped);
        Assert.Equal(105710003, WorkerSchema.OutcomeError);
    }

    [Fact]
    public void StepStatus_Values_MatchRealOptionSet()
    {
        Assert.Equal(105710000, WorkerSchema.StepPassed);
        Assert.Equal(105710001, WorkerSchema.StepFailed);
        Assert.Equal(105710002, WorkerSchema.StepSkipped);
    }

    [Fact]
    public void WorkerSchema_And_Config_OutcomesAgree()
    {
        // Welt A (Config/RunResultLoader) und Welt B (WorkerSchema/ChunkResultWriter)
        // müssen dasselbe Mapping verwenden, sonst kippen Error/Skipped pfadübergreifend.
        var cfg = new MarkantConfig();
        Assert.Equal(cfg.OutcomePassed, WorkerSchema.OutcomePassed);
        Assert.Equal(cfg.OutcomeFailed, WorkerSchema.OutcomeFailed);
        Assert.Equal(cfg.OutcomeSkipped, WorkerSchema.OutcomeSkipped);
        Assert.Equal(cfg.OutcomeError, WorkerSchema.OutcomeError);

        var std = new StandardCrmConfig();
        Assert.Equal(std.OutcomeSkipped, WorkerSchema.OutcomeSkipped);
        Assert.Equal(std.OutcomeError, WorkerSchema.OutcomeError);
    }

    [Theory]
    [InlineData(true, false, 105710000)]   // Success -> Passed
    [InlineData(false, false, 105710001)]  // !Success -> Failed
    [InlineData(true, true, 105710002)]    // Skipped gewinnt vor Success
    [InlineData(false, true, 105710002)]   // Skipped gewinnt auch vor !Success
    public void MapStepStatus_PrefersSkipped(bool success, bool skipped, int expected)
    {
        var sr = new StepResult { Success = success, Skipped = skipped };
        Assert.Equal(expected, WorkerSchema.MapStepStatus(sr));
    }
}
