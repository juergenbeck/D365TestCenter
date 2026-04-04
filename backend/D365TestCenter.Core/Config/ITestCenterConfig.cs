using System.Collections.Generic;

namespace D365TestCenter.Core.Config
{
    /// <summary>
    /// Configuration interface for the D365 Test Center.
    /// Allows customization of entity names, governance settings, and test data prefixes
    /// for different customer environments.
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

        // ── Governance (optional, null = no governance) ───────────────
        /// <summary>Source entity for governance (e.g. "markant_cdhcontactsource"). Null = no governance.</summary>
        string? GovernanceSourceEntity { get; }
        /// <summary>Source entity set name for OData (e.g. "markant_cdhcontactsources")</summary>
        string? GovernanceSourceEntitySet { get; }
        /// <summary>Logging entity (e.g. "markant_cdh_loggings"). Null = no governance polling.</summary>
        string? GovernanceLoggingEntity { get; }
        /// <summary>Contact lookup field on source entity (e.g. "markant_contactid")</summary>
        string? GovernanceContactLookup { get; }
        /// <summary>Source system field on source entity (e.g. "markant_sourcesystemcode")</summary>
        string? GovernanceSourceSystemField { get; }
        /// <summary>Auto-date field mappings: value field -> timestamp field</summary>
        Dictionary<string, string>? AutoDateFields { get; }
        /// <summary>Governance API name (e.g. "markant_RunFieldGovernanceForContact")</summary>
        string? GovernanceApiName { get; }

        // ── Polling ──────────────────────────────────────────────────
        /// <summary>Polling interval in milliseconds (default: 2000)</summary>
        int PollingIntervalMs { get; }
        /// <summary>Polling timeout in seconds (default: 120)</summary>
        int PollingTimeoutSeconds { get; }

        // ── Test Data ────────────────────────────────────────────────
        /// <summary>Prefix for generated test data (e.g. "JBE Test")</summary>
        string TestDataPrefix { get; }

        // ── Convenience ──────────────────────────────────────────────
        /// <summary>Whether governance is enabled (GovernanceSourceEntity is not null/empty)</summary>
        bool HasGovernance { get; }
    }
}
