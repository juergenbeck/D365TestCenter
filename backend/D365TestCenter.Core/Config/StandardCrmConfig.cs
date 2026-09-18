namespace D365TestCenter.Core.Config
{
    /// <summary>
    /// Default configuration: Test Center tables with the jbe publisher (OptionValuePrefix 10571).
    /// Used by the CLI and by the Custom-API plugins.
    /// </summary>
    public class StandardCrmConfig : ITestCenterConfig
    {
        public string TestCaseEntity => "jbe_testcase";
        public string TestRunEntity => "jbe_testrun";
        public string TestRunResultEntity => "jbe_testrunresult";
        public string TestStepEntity => "jbe_teststep";

        // OptionSet-Werte: Standard-Publisher (jbe, OptionValuePrefix 10571)
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
    }
}
