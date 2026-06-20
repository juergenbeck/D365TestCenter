namespace D365TestCenter.Core;

/// <summary>
/// Schema-Konstanten-Vertrag fuer die ADR-0009 Worker-Ausfuehrung (Koordinator + Worker).
///
/// Single Source of Truth fuer alle Entity-/Feld-/OptionSet-Namen und EnvVar-Defaults, die
/// <see cref="CoordinatorOrchestrator"/>, <see cref="ChunkWorkerOrchestrator"/> und die duennen
/// IPlugin-Wrapper (RunCoordinator/RunChunkWorker) verdrahten. Die Werte sind exakt die aus der
/// deploy-fertigen Schema-Spezifikation (Workspace 03_implementation/schema-adr0009-phase0.md);
/// Drift hier bricht die Plugins gegen das angelegte Schema.
///
/// Prefix durchgaengig 105710xxx (NICHT die in Create-TestingEntities.ps1 veralteten 595300xxx,
/// Goldene Regel 10). Die bereits bestehenden jbe_testrun-/jbe_testrunresult-Felder und die
/// jbe_teststatus/Outcome-OptionSet-Werte sind hier mit aufgenommen, damit Orchestrator und
/// Result-Writer eine einzige Konstantenquelle haben.
/// </summary>
public static class WorkerSchema
{
    // ── Entities ─────────────────────────────────────────────────
    public const string TestRunEntity = "jbe_testrun";
    public const string TestChunkEntity = "jbe_testchunk";
    public const string TestCaseEntity = "jbe_testcase";
    public const string TestRunResultEntity = "jbe_testrunresult";
    public const string TestStepEntity = "jbe_teststep";

    // ── jbe_testrun: bestehende Felder ───────────────────────────
    public const string RunStatus = "jbe_teststatus";
    public const string RunFilter = "jbe_testcasefilter";
    public const string RunKeepRecords = "jbe_keeprecords";
    public const string RunStartedOn = "jbe_startedon";
    public const string RunCompletedOn = "jbe_completedon";
    public const string RunTotal = "jbe_total";
    public const string RunPassed = "jbe_passed";
    public const string RunFailed = "jbe_failed";
    public const string RunSummary = "jbe_testsummary";
    public const string RunFullLog = "jbe_fulllog";

    // ── jbe_testrun: neue Worker-Felder (Phase 0) ────────────────
    public const string RunChunksTotal = "jbe_chunks_total";
    public const string RunChunksDone = "jbe_chunks_done";
    public const string RunChunksFailed = "jbe_chunks_failed";
    public const string RunCoordinatorCursor = "jbe_coordinator_cursor";
    public const string RunChunkSize = "jbe_chunksize";
    public const string RunDurationMs = "jbe_durationms";
    public const string RunTotalTestMs = "jbe_totaltestms";
    public const string RunAvgTestMs = "jbe_avgtestms";
    public const string RunMedianTestMs = "jbe_mediantestms";
    public const string RunMinTestMs = "jbe_mintestms";
    public const string RunMaxTestMs = "jbe_maxtestms";
    public const string RunSlowestTestId = "jbe_slowesttestid";
    public const string RunErrored = "jbe_errored";
    public const string RunSkipped = "jbe_skipped";
    public const string RunRecordsCreated = "jbe_recordscreated";
    public const string RunContinuations = "jbe_continuations";
    public const string RunMaxConcurrent = "jbe_maxconcurrent";

    // ── jbe_testchunk: Felder ────────────────────────────────────
    public const string ChunkName = "jbe_name";
    public const string ChunkTestRunId = "jbe_testrunid";
    public const string ChunkIndex = "jbe_chunkindex";
    public const string ChunkTestIds = "jbe_testids";
    public const string ChunkStatus = "jbe_chunkstatus";
    // Worker-Continuation: Index der naechsten un-gelaufenen GRUPPE (Befund 3,
    // Gruppen-Grenzen-Continuation). Passt zu TestRunner.RunGroupsBudgeted(startGroupIndex).
    public const string ChunkGroupCursor = "jbe_group_cursor";
    public const string ChunkProcessedCount = "jbe_processedcount";
    public const string ChunkFailedCount = "jbe_failedcount";
    public const string ChunkStartedOn = "jbe_startedon";
    public const string ChunkCompletedOn = "jbe_completedon";
    public const string ChunkDurationMs = "jbe_durationms";
    public const string ChunkContinuations = "jbe_continuations";
    public const string ChunkErrorDetails = "jbe_errordetails";
    // Stale-Chunk-Recovery (FB-46, OE-12): Anker fuer "wie lange in Laeuft" (bei JEDEM OC-Claim
    // gesetzt, erster Pickup + Resume) + Loop-Breaker-Zaehler (vom Worker bei Fortschritt auf 0,
    // von der Recovery erhoeht; ueber jbe_max_recoveries -> Chunk auf Fehler statt Endlos-Resume).
    public const string ChunkLastClaimedOn = "jbe_lastclaimedon";
    public const string ChunkRecoveryCount = "jbe_recoverycount";

    // ── jbe_testcase: Felder ─────────────────────────────────────
    public const string TcTestId = "jbe_testid";
    public const string TcTitle = "jbe_title";
    public const string TcDefinition = "jbe_definitionjson";
    public const string TcEnabled = "jbe_enabled";

    // ── jbe_testrunresult: Felder + Alternate Key ────────────────
    public const string ResultTestId = "jbe_testid";
    public const string ResultOutcome = "jbe_outcome";
    public const string ResultDuration = "jbe_durationms";
    public const string ResultError = "jbe_errormessage";
    public const string ResultAssertions = "jbe_assertionresults";
    public const string ResultTestRun = "jbe_testrunid";
    public const string ResultTrackedRecords = "jbe_trackedrecords";
    /// <summary>Alternate Key (jbe_testrunid, jbe_testid) -> idempotenter Result-Upsert (H1/H3).</summary>
    public const string ResultAlternateKey = "jbe_testrunresult_run_test_key";

    // ── jbe_teststep: Felder ─────────────────────────────────────
    public const string StepNumber = "jbe_stepnumber";
    public const string StepAction = "jbe_action";
    public const string StepAssertionField = "jbe_assertionfield";
    public const string StepExpected = "jbe_expectedvalue";
    public const string StepActual = "jbe_actualvalue";
    public const string StepDuration = "jbe_durationms";
    public const string StepError = "jbe_errormessage";
    public const string StepStatus = "jbe_stepstatus";
    public const string StepRunResult = "jbe_testrunresultid";
    public const string StepAlias = "jbe_alias";
    public const string StepEntity = "jbe_entity";
    public const string StepRecordId = "jbe_recordid";
    public const string StepInputData = "jbe_inputdata";
    public const string StepOutputData = "jbe_outputdata";

    // ── jbe_teststatus OptionSet (Run-Status, global, 105710xxx) ──
    public const int StatusPlanned = 105710000;   // Geplant / Pending (Koordinator-Trigger)
    public const int StatusRunning = 105710001;   // Laeuft / Running
    public const int StatusCompleted = 105710002; // Abgeschlossen / Completed
    public const int StatusError = 105710003;     // Fehler / Error
    public const int StatusSplitting = 105710004;  // Aufteilung laeuft / Splitting (Koordinator-Busy/Continuation-Flip)

    // ── jbe_chunkstatus OptionSet (global, 105710xxx) ────────────
    public const int ChunkNew = 105710000;        // Neu / New (Create-Trigger)
    public const int ChunkRunning = 105710001;    // Laeuft / Running (per OC-Claim aufgenommen)
    public const int ChunkResume = 105710002;     // Fortsetzen / Resume (Self-Trigger, Update)
    public const int ChunkProcessed = 105710003;  // Verarbeitet / Processed
    public const int ChunkError = 105710004;      // Fehler / Error (Poison-Chunk, kein Re-Trigger)

    // ── jbe_outcome OptionSet (Result) ───────────────────────────
    public const int OutcomePassed = 105710000;
    public const int OutcomeFailed = 105710001;
    public const int OutcomeError = 105710002;
    public const int OutcomeSkipped = 105710003;

    // ── jbe_stepstatus OptionSet ─────────────────────────────────
    public const int StepPassed = 105710000;
    public const int StepFailed = 105710001;

    // ── Environment Variables (Engine-Mutex + Tuning) ────────────
    /// <summary>Bool-EnvVar: true -> Worker-Modell (Koordinator/Worker), false/leer -> alte Batch-Cascade. C-08.</summary>
    public const string EnvUseWorker = "jbe_use_worker";
    /// <summary>Int-EnvVar: Ziel-Chunkgroesse in Tests (Default <see cref="DefaultChunkSize"/>). jbe_testrun.jbe_chunksize ueberschreibt pro Run.</summary>
    public const string EnvChunkSize = "jbe_chunksize";
    /// <summary>Int-EnvVar: Worker-/Koordinator-Zeitbudget in Sekunden (Default <see cref="DefaultBudgetSeconds"/>).</summary>
    public const string EnvBudgetSeconds = "jbe_worker_budget_seconds";
    /// <summary>Int-EnvVar: Schwelle in Sekunden, ab der ein "Laeuft"-Chunk als stale gilt (FB-46/OE-12, Default <see cref="DefaultStaleChunkSeconds"/>).</summary>
    public const string EnvStaleChunkSeconds = "jbe_stale_chunk_seconds";
    /// <summary>Int-EnvVar: maximale Recoveries pro Chunk ohne Fortschritt, danach Poison (Default <see cref="DefaultMaxRecoveries"/>).</summary>
    public const string EnvMaxRecoveries = "jbe_max_recoveries";

    /// <summary>
    /// Default-Chunkgroesse, wenn weder jbe_testrun.jbe_chunksize noch die EnvVar jbe_chunksize
    /// gesetzt ist. ~5-10 laut Plan (Skeptiker A-03): Test-Center-Tests dauern Sekunden, nicht ms
    /// wie Markants Records; 8 ist die Mitte (genug Fan-Out, ohne AsyncOp-Flut). NICHT 100.
    /// </summary>
    public const int DefaultChunkSize = 8;

    /// <summary>
    /// Default-Zeitbudget in Sekunden ab Ausfuehrungsbeginn, ab dem die Self-Trigger-Continuation
    /// greift (Watchdog). Headroom zum 120-s-Sandbox-Limit muss den langsamsten EINZELtest plus
    /// Result-Write-Overhead abdecken: das Budget wird VOR jeder Gruppe geprueft, die letzte Gruppe
    /// kann also bis ~budget+maxTest laufen. Der DB-Vorbild-Wert 80 (ms-grosse Items) ist fuer
    /// Test-Center-Tests (Sekunden bis ~1 min) zu hoch -- S39-Volumentest zeigte 120-s-Timeouts bei
    /// chunkSize 8 + langsamen Tests. Default daher 60; langsame Suiten per EnvVar weiter senken
    /// (jbe_worker_budget_seconds) und kleinere chunkSize waehlen. Siehe Fehlerbildkatalog
    /// (Stuck-Chunk-on-Timeout) -- ein Hard-Timeout laesst den Chunk in "Laeuft" haengen.
    /// </summary>
    public const int DefaultBudgetSeconds = 60;

    /// <summary>
    /// Default-Schwelle in Sekunden, ab der ein in "Laeuft" eingefrorener Chunk als stale gilt
    /// (FB-46/OE-12). Muss sicher ueber dem 120-s-Sandbox-Limit liegen: ein lebender Worker haelt
    /// "Laeuft" hoechstens 120 s (dann flippt er auf Verarbeitet/Fortsetzen), also ist ein Chunk,
    /// der laenger als 180 s in "Laeuft" steht, provabel tot. 60 s Headroom gegen Clock-Skew +
    /// Sweep-Latenz. Per EnvVar (jbe_stale_chunk_seconds) anpassbar.
    /// </summary>
    public const int DefaultStaleChunkSeconds = 180;

    /// <summary>
    /// Default-Obergrenze fuer Recoveries eines Chunks OHNE Fortschritt (Loop-Breaker, OE-12). Der
    /// Worker setzt jbe_recoverycount bei jeder Fortschritts-Welle auf 0; die Recovery erhoeht ihn.
    /// Uebersteigt er diesen Wert, wird der Chunk auf Fehler gesetzt statt erneut fortgesetzt --
    /// verhindert den Livelock beim irreduziblen Rest (ein Einzeltest > Wellen-Budget, ADR
    /// Entscheidung 7 vertagt). Per EnvVar (jbe_max_recoveries) anpassbar.
    /// </summary>
    public const int DefaultMaxRecoveries = 3;
}
