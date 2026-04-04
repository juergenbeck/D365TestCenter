using System.Collections.Generic;

namespace D365TestCenter.Core.Config
{
    /// <summary>
    /// Configuration for standard Dynamics 365 environments without custom governance.
    /// Suitable for LM, ZastrPay, or any environment with only standard CRM entities.
    /// </summary>
    public class StandardCrmConfig : ITestCenterConfig
    {
        public string TestCaseEntity => "jbe_testcase";
        public string TestRunEntity => "jbe_testrun";
        public string TestRunResultEntity => "jbe_testrunresult";
        public string TestStepEntity => "jbe_teststep";

        // No governance
        public string? GovernanceSourceEntity => null;
        public string? GovernanceSourceEntitySet => null;
        public string? GovernanceLoggingEntity => null;
        public string? GovernanceContactLookup => null;
        public string? GovernanceSourceSystemField => null;
        public string? GovernanceApiName => null;
        public Dictionary<string, string>? AutoDateFields => null;

        public int PollingIntervalMs => 2000;
        public int PollingTimeoutSeconds => 120;
        public string TestDataPrefix => "JBE Test";
        public bool HasGovernance => false;
    }
}
