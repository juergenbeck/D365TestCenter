using System.Collections.Generic;

namespace D365TestCenter.Core.Config
{
    /// <summary>
    /// Configuration for Markant Dynamics 365 environment.
    /// Includes CDH Field Governance with ContactSource and CDH-Logging entities.
    /// </summary>
    public class MarkantConfig : ITestCenterConfig
    {
        public string TestCaseEntity => "jbe_testcase";
        public string TestRunEntity => "jbe_testrun";
        public string TestRunResultEntity => "jbe_testrunresult";
        public string TestStepEntity => "jbe_teststep";

        public string? GovernanceSourceEntity => "markant_cdhcontactsource";
        public string? GovernanceSourceEntitySet => "markant_cdhcontactsources";
        public string? GovernanceLoggingEntity => "markant_cdh_loggings";
        public string? GovernanceContactLookup => "markant_contactid";
        public string? GovernanceSourceSystemField => "markant_sourcesystemcode";
        public string? GovernanceApiName => "markant_RunFieldGovernanceForContact";

        public Dictionary<string, string>? AutoDateFields => new Dictionary<string, string>
        {
            ["markant_firstname"] = "markant_cdh_firstname_modifiedondate",
            ["markant_lastname"] = "markant_cdh_lastname_modifiedondate",
            ["markant_emailaddress1"] = "markant_cdh_emailaddress1_modifiedondate",
            ["markant_gendercode"] = "markant_cdh_gendercode_modifiedondate",
            ["markant_jobtitle"] = "markant_cdh_markant_jobtitle_modifiedondate",
            ["markant_telephone1"] = "markant_cdh_markant_telephone1_modifiedondate",
            ["markant_telephone2"] = "markant_cdh_markant_telephone2_modifiedondate",
            ["markant_mobilephone"] = "markant_cdh_markant_mobilephone_modifiedondate",
            ["markant_middlename"] = "markant_cdh_middlename_modifiedondate",
            ["markant_academictitle"] = "markant_cdh_academictitle_modifiedondate",
            ["markant_parentcustomerid"] = "markant_cdh_parentcustomerid_modifiedondate",
            ["markant_communicationlanguageid"] = "markant_cdh_communicationlanguageid_modifiedon"
        };

        public int PollingIntervalMs => 2000;
        public int PollingTimeoutSeconds => 120;
        public string TestDataPrefix => "JBE Test";
        public bool HasGovernance => true;
    }
}
