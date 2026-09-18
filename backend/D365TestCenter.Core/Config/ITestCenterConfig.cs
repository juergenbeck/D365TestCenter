namespace D365TestCenter.Core.Config
{
    /// <summary>
    /// Configuration interface for the D365 Test Center: entity names of the Test Center tables
    /// and the publisher-specific option set values of jbe_teststatus / jbe_testoutcome.
    /// </summary>
    public interface ITestCenterConfig
    {
        // ── Test Center Entities ──────────────────────────────────────
        /// <summary>Entity logical name for test cases (e.g. "jbe_testcase")</summary>
        string TestCaseEntity { get; }
        /// <summary>Entity logical name for test runs (e.g. "jbe_testrun")</summary>
        string TestRunEntity { get; }
        /// <summary>Entity logical name for test run results (e.g. "jbe_testrunresult")</summary>
        string TestRunResultEntity { get; }
        /// <summary>Entity logical name for test steps (e.g. "jbe_teststep")</summary>
        string TestStepEntity { get; }

        // ── OptionSet-Werte (publisher-spezifisch) ──────────────────
        /// <summary>jbe_teststatus: Geplant (z.B. 105710000 beim Publisher-Prefix 10571)</summary>
        int StatusPlanned { get; }
        /// <summary>jbe_teststatus: Wird ausgeführt</summary>
        int StatusRunning { get; }
        /// <summary>jbe_teststatus: Abgeschlossen</summary>
        int StatusCompleted { get; }
        /// <summary>jbe_teststatus: Fehlgeschlagen</summary>
        int StatusFailed { get; }
        /// <summary>jbe_testoutcome: Passed</summary>
        int OutcomePassed { get; }
        /// <summary>jbe_testoutcome: Failed</summary>
        int OutcomeFailed { get; }
        /// <summary>jbe_testoutcome: Error</summary>
        int OutcomeError { get; }
        /// <summary>jbe_testoutcome: Skipped</summary>
        int OutcomeSkipped { get; }
    }
}
