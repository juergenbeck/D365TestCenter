using System.Collections.Generic;

namespace D365TestCenter.Core.Config
{
    /// <summary>
    /// Configuration for Markant Dynamics 365 environment.
    /// Field Governance uses the Q2 namespace (seit 01.04.2026): markant_fg_* tables.
    /// Q1-Entities (markant_cdhcontactsource, markant_cdh_logging) existieren teils
    /// noch parallel, sind aber nicht autoritativ.
    /// </summary>
    public class MarkantConfig : ITestCenterConfig
    {
        public string TestCaseEntity => "jbe_testcase";
        public string TestRunEntity => "jbe_testrun";
        public string TestRunResultEntity => "jbe_testrunresult";
        public string TestStepEntity => "jbe_teststep";

        // Q2-Namen seit 01.04.2026 (Drift-Report Session 08, 2026-04-22)
        public string? GovernanceSourceEntity => "markant_fg_contactsource";
        public string? GovernanceSourceEntitySet => "markant_fg_contactsources";
        public string? GovernanceLoggingEntity => "markant_fg_loggings";
        public string? GovernanceContactLookup => "markant_contactid";
        public string? GovernanceSourceSystemField => "markant_fg_sourcesystemcode";
        // ACHTUNG: Custom API wurde am 2026-04-21 geloescht. Neu-Deployment offen
        // (CDH-20260421-001). Solange die API fehlt, schlaegt GovernanceApi-
        // basierter Code fehl. Workaround: markant_fg_requestcode auf contact setzen.
        public string? GovernanceApiName => "markant_RunFieldGovernanceForContact";

        // AutoDateField-Muster Q2: markant_<field>_modifiedon (ohne cdh_, ohne _date).
        // Einzige Ausnahme: markant_raw_external_status_modifiedondate.
        public Dictionary<string, string>? AutoDateFields => new Dictionary<string, string>
        {
            ["markant_firstname"] = "markant_firstname_modifiedon",
            ["markant_lastname"] = "markant_lastname_modifiedon",
            ["markant_middlename"] = "markant_middlename_modifiedon",
            ["markant_academictitle"] = "markant_academictitle_modifiedon",
            ["markant_emailaddress1"] = "markant_emailaddress1_modifiedon",
            ["markant_telephone1"] = "markant_telephone1_modifiedon",
            ["markant_telephone2"] = "markant_telephone2_modifiedon",
            ["markant_mobilephone"] = "markant_mobilephone_modifiedon",
            ["markant_jobtitle"] = "markant_jobtitle_modifiedon",
            ["markant_gender"] = "markant_gender_modifiedon",
            ["markant_parentcustomerid"] = "markant_parentcustomerid_modifiedon",
            ["markant_externalid"] = "markant_externalid_modifiedon",
            ["markant_communicationlanguage"] = "markant_communicationlanguage_modifiedon",
            ["markant_kin"] = "markant_kin_modifiedon",
            ["markant_is_markant_employee"] = "markant_is_markant_employee_modifiedon",
            ["markant_ismarkantemployee"] = "markant_ismarkantemployee_modifiedon",
            ["markant_external_status_aggregation"] = "markant_external_status_aggregation_modifiedon",
            ["markant_raw_external_status"] = "markant_raw_external_status_modifiedondate"
        };

        public int PollingIntervalMs => 2000;
        public int PollingTimeoutSeconds => 120;
        public string TestDataPrefix => "JBE Test";

        // OptionSet-Werte für Markant-Umgebungen.
        // Auf allen Markant-Umgebungen verwendet der Publisher "jbe" den
        // OptionValuePrefix 10571 (siehe deploy-config.json:
        // publisherOptionValuePrefix=10571). Identisch mit StandardCrmConfig.
        //
        // Falls eine neue Markant-Umgebung mit anderem Prefix deployed wird
        // (z.B. 59530), diese Werte hier anpassen ODER Metadata-Lookup beim
        // Connect einbauen (Goldene Regel 10: Dataverse fragen, nicht raten).
        public int StatusPlanned => 105710000;
        public int StatusRunning => 105710001;
        public int StatusCompleted => 105710002;
        public int StatusFailed => 105710003;
        public int OutcomePassed => 105710000;
        public int OutcomeFailed => 105710001;
        // Achtung: jbe_testoutcome-OptionSet hat historisch 105710002 = Skipped.
        // Error wurde erst in v5.4 als 105710003 ergänzt. Deshalb diese Reihenfolge.
        public int OutcomeSkipped => 105710002;
        public int OutcomeError => 105710003;

        public bool HasGovernance => true;
    }
}
