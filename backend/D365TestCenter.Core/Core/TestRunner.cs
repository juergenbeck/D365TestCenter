using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using System.Diagnostics;
using System.ServiceModel;
using System.Text;
using Newtonsoft.Json.Linq;
using D365TestCenter.Core.Validation;

namespace D365TestCenter.Core;

/// <summary>
/// Orchestriert die generische Integrationstestausführung:
/// Setup (Preconditions) -> Steps -> Assertions -> Cleanup.
/// </summary>
public sealed class TestRunner
{
    private readonly IOrganizationService _service;
    private readonly EntityMetadataCache _entityMetadata;
    private readonly TestDataFactory _dataFactory;
    private readonly PlaceholderEngine _placeholderEngine;
    private readonly AssertionEngine _assertionEngine;
    private readonly IPackValidator _validator;
    private readonly StringBuilder _log;

    /// <summary>
    /// Optional: Browser-based UI automation executor (ADR-0006). Null in the
    /// Plugin-Sandbox path (RunIntegrationTestsApi, RunTestsOnStatusChange) —
    /// BrowserAction steps are skipped with a clear "not supported in sandbox"
    /// message there. Non-null in the CLI path, where Microsoft.Playwright
    /// drives Chromium against Markant DEV.
    /// </summary>
    private readonly IBrowserActionExecutor? _browser;

    /// <summary>
    /// Wenn true, werden die in Steps angelegten Records nach dem Testlauf
    /// nicht gelöscht. Default false (Cleanup lief historisch immer).
    /// Wird vom Orchestrator aus jbe_testrun.jbe_keeprecords gesetzt.
    /// </summary>
    public bool KeepRecords { get; set; }

    /// <summary>
    /// OE-10: Wenn true, wird nach jedem Testfall der Primary-Name jedes angelegten
    /// Records per Retrieve erfasst und in TrackedRecord.Name abgelegt (für den
    /// sync-zephyr-Audit-Kommentar). NUR vom CLI-run-Pfad gesetzt — die
    /// Plugin-Sandbox-Pfade lassen es false, weil ein zusätzlicher Service-Call mit
    /// try/catch dort die Sandbox-Wächter-Regel verletzen würde. Default false.
    /// </summary>
    public bool CaptureRecordNames { get; set; }

    /// <summary>
    /// ADR 2026-06-28: erlaubt den async-Job-Quiescence-Wait (WaitForAsyncCompletion).
    /// NUR der headless CLI-Pfad setzt das auf true. In allen Plugin-Sandbox-Pfaden
    /// (Custom-API-Sync, Async-CRUD-Trigger, ChunkWorker) bleibt es false, weil ein langer
    /// asyncoperation-Poll das 2-min-Sandbox-Limit sprengt — der Step wird dort sauber
    /// geskippt (analog BrowserAction, das _browser==null prüft). Default false.
    /// </summary>
    public bool AllowAsyncOperationPolling { get; set; }

    /// <summary>
    /// Wird nach jedem Testfall aufgerufen (index, total, result).
    /// Ermöglicht Fortschritts-Updates im TestRun-Record.
    /// </summary>
    public event Action<int, int, TestCaseResult>? OnTestCompleted;

    public TestRunner(
        IOrganizationService service,
        IBrowserActionExecutor? browser = null,
        IPackValidator? validator = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _entityMetadata = new EntityMetadataCache(service, msg => Log($"      {msg}"));
        _dataFactory = new TestDataFactory();
        _placeholderEngine = new PlaceholderEngine();
        // FB-32: pass shared metadata cache to AssertionEngine so query-filter
        // value conversion is type-aware (GUID-shaped strings on String fields
        // stay as strings instead of being auto-converted to Guid).
        _assertionEngine = new AssertionEngine(_entityMetadata);
        // OE-6: pack validator for pre-run schema checks. Default instance is
        // metadata-free (Phase 1 rules only); callers may inject a richer
        // validator implementation later (Phase 2).
        _validator = validator ?? new PackValidator();
        _log = new StringBuilder();
        _browser = browser;
    }

    // Geteilte Kontexte für dependsOn/sharedContext
    private readonly Dictionary<string, TestContext> _sharedContexts
        = new Dictionary<string, TestContext>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _passedTestIds
        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Führt eine vollständige Testsequenz aus.</summary>
    public TestRunResult RunAll(List<TestCase> testCases)
    {
        // Datengetriebene Tests expandieren: jede dataRow wird ein eigener Testlauf
        var expandedTests = ExpandDataDrivenTests(testCases);

        var result = new TestRunResult
        {
            StartedAt = DateTime.UtcNow,
            TotalCount = expandedTests.Count
        };

        Log("=== INTEGRATIONSTEST ===");
        Log($"Testfälle: {expandedTests.Count} (davon {expandedTests.Count - testCases.Count} aus dataRows expandiert)");
        Log($"Start: {result.StartedAt:O}");

        int index = 0;
        foreach (var (tc, dataRow) in expandedTests)
        {
            index++;
            RunExpandedTest(tc, dataRow, index, expandedTests.Count, result);
        }

        result.CompletedAt = DateTime.UtcNow;
        Log($"=== ERGEBNIS: {result.PassedCount}/{result.TotalCount} bestanden, " +
            $"{result.FailedCount} fehlgeschlagen, {result.ErrorCount} Fehler, " +
            $"{result.SkippedCount} übersprungen ===");

        result.FullLog = _log.ToString();
        return result;
    }

    /// <summary>
    /// Zeitbudgetierte Gruppen-Schleife (ADR-0009 Phase 1, Befund 3). Verarbeitet die
    /// Abhängigkeits-Gruppen (aus <see cref="BuildDependencyGroups"/>) ab
    /// <paramref name="startGroupIndex"/>, prüft das Zeitbudget VOR jeder Gruppe und
    /// führt mindestens eine Gruppe pro Aufruf aus (-> Cursor schreitet garantiert fort,
    /// Terminierung gesichert). Eine Gruppe wird immer KOMPLETT ausgeführt (atomar) --
    /// so baut eine frische Worker-Instanz den dependsOn-Zustand (_passedTestIds)
    /// gruppenintern selbst auf, ohne Re-Seed (Gruppen-Grenzen-Continuation).
    /// Datengetriebene Tests werden pro Gruppe beim Lauf expandiert.
    /// <paramref name="clock"/> ist injizierbar für deterministische Mock-Budget-Tests.
    /// </summary>
    public BudgetedRunResult RunGroupsBudgeted(
        List<List<TestCase>> groups, int startGroupIndex, int budgetSeconds,
        Func<DateTime>? clock = null)
    {
        if (groups == null) throw new ArgumentNullException(nameof(groups));
        var now = clock ?? (() => DateTime.UtcNow);
        var startTime = now();

        var result = new TestRunResult { StartedAt = startTime };
        Log("=== GRUPPEN-LAUF (zeitbudgetiert) ===");
        Log($"Gruppen: {groups.Count}, Start-Index: {startGroupIndex}, Budget: {budgetSeconds}s");

        var gi = startGroupIndex < 0 ? 0 : startGroupIndex;
        var ranThisCall = 0;
        for (; gi < groups.Count; gi++)
        {
            // Budget-Check VOR jeder Gruppe -- aber mindestens eine Gruppe pro Aufruf,
            // damit der Cursor garantiert fortschreitet (Terminierung gesichert).
            if (ranThisCall > 0 && (now() - startTime).TotalSeconds >= budgetSeconds)
            {
                Log($"Budget {budgetSeconds}s erreicht vor Gruppe {gi} -> Continuation.");
                break;
            }

            // Gruppe atomar: komplett ausführen. Datengetriebene Tests pro Gruppe
            // beim Lauf expandieren (deterministisch -> stabiler Gruppen-Cursor).
            var expanded = ExpandDataDrivenTests(groups[gi]);
            Log($"-- Gruppe {gi}: {expanded.Count} Test(s) --");
            var idx = 0;
            foreach (var (tc, dataRow) in expanded)
            {
                idx++;
                RunExpandedTest(tc, dataRow, idx, expanded.Count, result);
            }
            ranThisCall++;
        }

        result.CompletedAt = now();
        result.TotalCount = result.Results.Count;
        result.FullLog = _log.ToString();

        var done = gi >= groups.Count;
        Log($"=== GRUPPEN-LAUF Ende: {ranThisCall} Gruppe(n) gelaufen, NextGroupIndex={gi}, Done={done} ===");

        return new BudgetedRunResult { NextGroupIndex = gi, Done = done, Run = result };
    }

    /// <summary>
    /// Führt einen bereits expandierten Testfall aus: enabled-/dependsOn-Skip,
    /// <see cref="ExecuteSingleTest"/>, Zähler + <c>_passedTestIds</c> aktualisieren,
    /// <see cref="OnTestCompleted"/> feuern. Gemeinsamer Kern von <see cref="RunAll"/> und
    /// <see cref="RunGroupsBudgeted"/> -- so verhalten sich beide Pfade identisch
    /// (dependsOn über <c>_passedTestIds</c>, gleiche Skip-Semantik).
    /// </summary>
    private void RunExpandedTest(
        TestCase tc, Dictionary<string, object?>? dataRow,
        int index, int total, TestRunResult result)
    {
        if (!tc.Enabled)
        {
            var skipped = new TestCaseResult
            {
                TestId = tc.Id,
                Title = tc.Title,
                Outcome = TestOutcome.Skipped
            };
            result.Results.Add(skipped);
            Log($"-- [{index}/{total}] [{tc.Id}] {tc.Title} -> ÜBERSPRUNGEN (deaktiviert) --");
            OnTestCompleted?.Invoke(index, total, skipped);
            return;
        }

        // dependsOn: überspringen wenn eine Abhängigkeit nicht bestanden hat
        if (tc.DependsOn != null && tc.DependsOn.Count > 0)
        {
            var missingDeps = tc.DependsOn.Where(dep => !_passedTestIds.Contains(dep)).ToList();
            if (missingDeps.Count > 0)
            {
                var depSkipped = new TestCaseResult
                {
                    TestId = tc.Id,
                    Title = tc.Title,
                    Outcome = TestOutcome.Skipped,
                    ErrorMessage = $"Abhängigkeit nicht erfüllt: {string.Join(", ", missingDeps)}"
                };
                result.Results.Add(depSkipped);
                Log($"-- [{index}/{total}] [{tc.Id}] ÜBERSPRUNGEN (dependsOn: {string.Join(", ", missingDeps)}) --");
                OnTestCompleted?.Invoke(index, total, depSkipped);
                return;
            }
        }

        var tcResult = ExecuteSingleTest(tc, index, total, dataRow);
        result.Results.Add(tcResult);

        if (tcResult.Outcome == TestOutcome.Passed)
            _passedTestIds.Add(tc.Id);

        switch (tcResult.Outcome)
        {
            case TestOutcome.Passed: result.PassedCount++; break;
            case TestOutcome.Failed: result.FailedCount++; break;
            case TestOutcome.Error: result.ErrorCount++; break;
            case TestOutcome.Skipped: result.SkippedCount++; break;
        }

        OnTestCompleted?.Invoke(index, total, tcResult);
    }

    /// <summary>
    /// Expandiert datengetriebene Testfälle: Ein Testfall mit N dataRows wird zu N Testfällen.
    /// Testfälle ohne dataRows bleiben unverändert.
    /// </summary>
    private static List<(TestCase tc, Dictionary<string, object?>? dataRow)> ExpandDataDrivenTests(
        List<TestCase> testCases)
    {
        var expanded = new List<(TestCase, Dictionary<string, object?>?)>();

        foreach (var tc in testCases)
        {
            if (tc.DataRows != null && tc.DataRows.Count > 0)
            {
                int rowIdx = 0;
                foreach (var row in tc.DataRows)
                {
                    rowIdx++;
                    // Erstelle eine Kopie des Testfalls mit angepasster ID/Titel
                    var rowTc = new TestCase
                    {
                        Id = $"{tc.Id}[{rowIdx}]",
                        Title = $"{tc.Title} [Zeile {rowIdx}]",
                        Description = tc.Description,
                        Category = tc.Category,
                        Tags = tc.Tags,
                        Enabled = tc.Enabled,
                        Steps = tc.Steps,
                        DependsOn = tc.DependsOn,
                        SharedContext = tc.SharedContext
                    };
                    expanded.Add((rowTc, row));
                }
            }
            else
            {
                expanded.Add((tc, null));
            }
        }

        return expanded;
    }

    /// <summary>
    /// Bildet Abhängigkeits-Gruppen (Connected Components) über die Tests, damit der
    /// Fan-Out-Koordinator jede Gruppe komplett in EINEN Chunk legt und der Worker den
    /// Continuation-Cursor auf Gruppen-Grenzen setzen kann (ADR-0009, Befund 3,
    /// Gruppen-Grenzen-Continuation). Eine frische Worker-Instanz, die an einer
    /// Gruppen-Grenze startet, baut den dependsOn-Zustand (_passedTestIds) gruppenintern
    /// selbst auf -- kein Re-Seed nötig.
    ///
    /// Kanten: (1) jeder dependsOn-Verweis auf einen Test IM SET; (2) gemeinsamer,
    /// nicht-leerer sharedContext (defensive Affinitäts-Kante -- heute kein
    /// Laufzeit-Effekt, _sharedContexts ist totes Feld, aber vorausschauend geführt).
    /// Ein dependsOn auf einen Test AUSSERHALB des Sets erzeugt KEINE Kante (der Test
    /// wird zur Laufzeit ohnehin übersprungen, _passedTestIds enthält ihn nie).
    ///
    /// Reihenfolge (deterministisch -> stabiler Cursor-Anker über Continuations):
    /// Gruppen nach dem kleinsten Eingabe-Index ihrer Mitglieder; innerhalb einer
    /// Gruppe die Eingabe-Reihenfolge (Ausführungsreihenfolge, dependsOn-Ziel vor dem
    /// Abhängigen). ID-Vergleich case-insensitive (wie _passedTestIds).
    /// </summary>
    public static List<List<TestCase>> BuildDependencyGroups(List<TestCase> tests)
    {
        if (tests == null) throw new ArgumentNullException(nameof(tests));
        var n = tests.Count;
        if (n == 0) return new List<List<TestCase>>();

        // Union-Find über die Eingabe-Indizes.
        var parent = new int[n];
        for (var i = 0; i < n; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]]; // Pfad-Halbierung
                x = parent[x];
            }
            return x;
        }

        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb) return;
            // Kleinerer Index wird Wurzel -> hält die Min-Index-Reihenfolge stabil.
            if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
        }

        // ID -> erster Index (case-insensitive, wie _passedTestIds).
        var idToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < n; i++)
        {
            var id = tests[i].Id;
            if (!string.IsNullOrEmpty(id) && !idToIndex.ContainsKey(id))
                idToIndex[id] = i;
        }

        // Kante 1: dependsOn auf einen Test IM SET.
        for (var i = 0; i < n; i++)
        {
            var deps = tests[i].DependsOn;
            if (deps == null) continue;
            foreach (var dep in deps)
            {
                if (!string.IsNullOrEmpty(dep) && idToIndex.TryGetValue(dep, out var j))
                    Union(i, j);
            }
        }

        // Kante 2: gemeinsamer, nicht-leerer sharedContext (defensive Affinitäts-Kante).
        var firstInContext = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < n; i++)
        {
            var ctx = tests[i].SharedContext;
            if (string.IsNullOrWhiteSpace(ctx)) continue;
            if (firstInContext.TryGetValue(ctx, out var first))
                Union(i, first);
            else
                firstInContext[ctx] = i;
        }

        // Komponenten nach Wurzel sammeln; Mitglieder in Eingabe-Reihenfolge (i aufsteigend).
        var byRoot = new Dictionary<int, List<TestCase>>();
        var rootOrder = new List<int>(); // Wurzeln in Reihenfolge ihres ersten Auftretens (= Min-Index)
        for (var i = 0; i < n; i++)
        {
            var root = Find(i);
            if (!byRoot.TryGetValue(root, out var members))
            {
                members = new List<TestCase>();
                byRoot[root] = members;
                rootOrder.Add(root);
            }
            members.Add(tests[i]);
        }

        return rootOrder.Select(r => byRoot[r]).ToList();
    }

    /// <summary>
    /// Packt Abhängigkeits-Gruppen (aus <see cref="BuildDependencyGroups"/>) in Chunks der
    /// Zielgröße <paramref name="chunkSize"/> Tests, OHNE je eine Gruppe zu trennen
    /// (ADR-0009 Phase 2, Koordinator). Greedy in Reihenfolge: eine Gruppe kommt in den
    /// aktuellen Chunk, solange sie hineinpasst; sonst beginnt ein neuer Chunk. Eine
    /// Gruppe, die allein größer als <paramref name="chunkSize"/> ist, bildet ihren
    /// eigenen (übergroßen) Chunk -- sie kann nicht geteilt werden (dependsOn-Affinität).
    ///
    /// Jeder Chunk ist die Konkatenation seiner Gruppen (flache Testliste). Der Worker
    /// re-deriviert die Gruppen aus den Chunk-Test-IDs deterministisch neu
    /// (<see cref="BuildDependencyGroups"/> auf der Chunk-Teilmenge liefert dieselben
    /// Connected Components, weil ein Chunk nur VOLLSTÄNDIGE Komponenten enthält).
    /// <paramref name="chunkSize"/> &lt; 1 wird auf 1 geklemmt.
    /// </summary>
    public static List<List<TestCase>> BuildChunks(List<List<TestCase>> groups, int chunkSize)
    {
        if (groups == null) throw new ArgumentNullException(nameof(groups));
        if (chunkSize < 1) chunkSize = 1;

        var chunks = new List<List<TestCase>>();
        List<TestCase>? current = null;

        foreach (var group in groups)
        {
            if (group.Count == 0) continue; // leere Gruppe defensiv überspringen

            // Neuer Chunk, wenn keiner offen ist ODER die Gruppe nicht mehr hineinpasst.
            // Eine übergroße Einzelgruppe landet so allein in einem frischen Chunk
            // (sie kann nicht geteilt werden) und der nächste Gruppe-Add öffnet wieder
            // einen neuen Chunk.
            if (current == null || current.Count + group.Count > chunkSize)
            {
                current = new List<TestCase>();
                chunks.Add(current);
            }
            current.AddRange(group);
        }

        return chunks;
    }

    /// <summary>
    /// Rechnet das Run-Aggregat einmal am Plateau aus den Result-Records (ADR-0009 B.5,
    /// Plan Phase 3). Outcome-Split (Total/Passed/Failed/Errored/Skipped), Dauer-Verteilung
    /// (avg/median/min/max/slowest) über die ausgeführten Tests (Outcome != Skipped) und
    /// die Summe der getrackten angelegten Records. Pure -> testbar. Median wie die
    /// Cold-Start-Heuristik (<c>sorted[count/2]</c>).
    /// </summary>
    public static RunAggregate ComputeRunAggregate(IEnumerable<TestCaseResult> results)
    {
        if (results == null) throw new ArgumentNullException(nameof(results));
        var list = results as IList<TestCaseResult> ?? results.ToList();

        var agg = new RunAggregate { Total = list.Count };
        foreach (var r in list)
        {
            switch (r.Outcome)
            {
                case TestOutcome.Passed: agg.Passed++; break;
                case TestOutcome.Failed: agg.Failed++; break;
                case TestOutcome.Error: agg.Errored++; break;
                case TestOutcome.Skipped: agg.Skipped++; break;
            }
            agg.RecordsCreated += r.TrackedRecords?.Count ?? 0;
        }

        // Dauer-Verteilung NUR über die ausgeführten Tests (Outcome != Skipped);
        // Skipped haben Dauer 0 und würden Min/Avg/Median verzerren.
        var executed = list.Where(r => r.Outcome != TestOutcome.Skipped).ToList();
        if (executed.Count > 0)
        {
            var durations = executed.Select(r => r.DurationMs).OrderBy(d => d).ToList();
            agg.TotalTestMs = durations.Sum();
            agg.AvgTestMs = (int)(agg.TotalTestMs / executed.Count);
            agg.MedianTestMs = (int)durations[durations.Count / 2]; // obere Mitte, wie Cold-Start
            agg.MinTestMs = (int)durations[0];
            agg.MaxTestMs = (int)durations[durations.Count - 1];

            // Langsamster Test: höchste Dauer, erster bei Gleichstand.
            var slowest = executed[0];
            foreach (var r in executed)
                if (r.DurationMs > slowest.DurationMs) slowest = r;
            agg.SlowestTestId = slowest.TestId;
        }

        return agg;
    }

    private TestCaseResult ExecuteSingleTest(
        TestCase tc, int index, int total,
        Dictionary<string, object?>? dataRow = null)
    {
        var sw = Stopwatch.StartNew();
        var tcResult = new TestCaseResult { TestId = tc.Id, Title = tc.Title };
        TestContext? ctx = null;
        var testStartUtc = DateTime.UtcNow;

        Log($"-- [{index}/{total}] [{tc.Id}] {tc.Title} --");

        try
        {
            // OE-6: static pre-run validation. Schema and pattern findings are
            // reported BEFORE any service call runs. Warning/Info findings are
            // logged for visibility; Error findings abort the test with
            // Outcome=Error so the user does not waste a 44s WaitForRecord
            // timeout on a filter typo or a missing requestName.
            // Pre-run validation stays free of service calls (the OE-6 value:
            // catch a filter/name bug WITHOUT a 44s WaitForRecord timeout). The
            // metadata-aware Phase-2 rules (OE-8/Backlog J), which DO need a
            // RetrieveEntity call, run only in the explicit CLI `validate --env`
            // offline-lint path, not here on every run.
            var validation = _validator.ValidateOne(tc);
            LogValidationFindings(validation);
            if (validation.HasErrors)
            {
                tcResult.Outcome = TestOutcome.Error;
                tcResult.ErrorMessage = FormatValidationErrors(validation);
                sw.Stop();
                tcResult.DurationMs = sw.ElapsedMilliseconds;
                Log($"  -> FEHLER: Pre-Run-Validation hat {validation.ErrorCount} Error-Befund(e). Test wird nicht ausgeführt.");
                return tcResult;
            }

            ctx = new TestContext { TestStartUtc = testStartUtc, TestId = tc.Id };
            ctx.CurrentDataRow = dataRow;

            Log("  Teststeps ausführen...");
            ExecuteSteps(tc, ctx, tcResult);

            // Outcome-Bestimmung (ADR-0004):
            //  - Exception in einem Step mit OnError="stop" (Default für Non-Assert)
            //    führt zu Outcome=Error (catch-Zweig unten).
            //  - Kein Step hat Success=false → Passed.
            //  - Mindestens ein Step hat Success=false → Failed. Das umfasst:
            //    Assert-Failure (OnError=continue per Default) und Non-Assert-
            //    Step-Failure mit explizitem OnError=continue.
            // ADR-0011: Skipped-Steps (condition nicht erfüllt) zählen NICHT als
            // Failure. Outcome=Skipped, wenn der Test Assert-Steps DEFINIERT, aber
            // KEINER ausgeführt wurde (alle condition-geskippt) -- sonst wäre er
            // grün ohne Prüfung. Bestandstests ohne condition sind unberührt:
            // dort wird nie geskippt (anyAssertExecuted == hasAsserts), und ein
            // assert-loser Smoke (hasAsserts=false) bleibt Passed.
            var anyFailed = tcResult.StepResults.Any(s => !s.Success && !s.Skipped);
            var assertSteps = tcResult.StepResults
                .Where(s => string.Equals(s.Action, "Assert", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var allAssertsSkipped = assertSteps.Count > 0 && assertSteps.All(s => s.Skipped);

            tcResult.Outcome = anyFailed ? TestOutcome.Failed
                : allAssertsSkipped ? TestOutcome.Skipped
                : TestOutcome.Passed;

            var outcomeLabel = tcResult.Outcome switch
            {
                TestOutcome.Failed => "FEHLGESCHLAGEN",
                TestOutcome.Skipped => "ÜBERSPRUNGEN (alle Asserts condition-geskippt)",
                _ => "BESTANDEN"
            };
            Log($"  -> {outcomeLabel}");
        }
        catch (Exception ex)
        {
            tcResult.Outcome = TestOutcome.Error;
            tcResult.ErrorMessage = ex.Message;
            Log($"  -> FEHLER: {ex.Message}");
        }
        finally
        {
            if (ctx != null)
            {
                // B5: TrackedRecords aus ctx.CreatedEntities VOR dem Cleanup
                // einfrieren — danach werden sie ggf. gelöscht.
                tcResult.TrackedRecords = ctx.CreatedEntities
                    .Select(e => new TrackedRecord
                    {
                        Entity = e.EntityName,
                        Id = e.Id,
                        Alias = ctx.Records
                            .Where(kv => kv.Value.Id == e.Id)
                            .Select(kv => kv.Key)
                            .FirstOrDefault()
                    })
                    .ToList();

                // OE-10: Primary-Namen der angelegten Records erfassen — VOR dem Cleanup,
                // sonst sind die Records gelöscht. Nur im CLI-run-Pfad (CaptureRecordNames).
                if (CaptureRecordNames)
                    CaptureTrackedRecordNames(tcResult.TrackedRecords);

                try
                {
                    Log("  Cleanup...");
                    Cleanup(ctx, tcResult);
                }
                catch (Exception ex)
                {
                    Log($"  Cleanup-Fehler (nicht kritisch): {ex.Message}");
                }

                // A7: Plugin-Trace-Logs seit Test-Start in das Log einbinden,
                // damit jbe_fulllog Plugin-Diagnostik enthält. Schreib-Plugin
                // (PluginTraceLogSetting=All oder Exception) muss aktiv sein,
                // sonst sind keine Records vorhanden — kein Fehler, nur leer.
                CapturePluginTraceLogs(testStartUtc, tc.Id);
            }

            sw.Stop();
            tcResult.DurationMs = sw.ElapsedMilliseconds;
            Log($"  Dauer: {tcResult.DurationMs}ms");
        }

        return tcResult;
    }

    /// <summary>
    /// OE-10: Befüllt TrackedRecord.Name mit dem Primary-Name jedes angelegten
    /// Records. Ein Retrieve pro Record (die Records existieren noch, Cleanup kommt
    /// danach). Graceful: ein nicht ladbarer Name bleibt null. Nur im CLI-run-Pfad
    /// aufgerufen (CaptureRecordNames), daher ist das try/catch um den Service-Call
    /// hier zulässig — kein Plugin-Sandbox-Kontext, in dem ein gefangener Fault die
    /// Transaktion vergiften würde.
    /// </summary>
    private void CaptureTrackedRecordNames(List<TrackedRecord> tracked)
    {
        foreach (var tr in tracked)
        {
            try
            {
                var logical = _entityMetadata.ResolveLogicalName(tr.Entity);
                var primaryName = _entityMetadata.GetMetadata(logical)?.PrimaryNameAttribute;
                if (string.IsNullOrWhiteSpace(primaryName)) continue;
                var rec = _service.Retrieve(logical, tr.Id, new ColumnSet(primaryName));
                tr.Name = rec.GetAttributeValue<string>(primaryName);
            }
            catch
            {
                // Name ist optional für den Audit-Kommentar; ein Fehler (Record vom
                // Plugin gelöscht, Berechtigung, ...) darf den Lauf nicht stören.
            }
        }
    }

    /// <summary>
    /// A7 / ZastrPay-Feedback: Plugin-Trace-Logs seit Test-Start aus der
    /// plugintracelog-Tabelle holen und in den runner-internen Log-Builder
    /// schreiben. Im Sync-Plugin läuft das via SandboxSafeOrganizationService
    /// (Wrapper, ADR-0005). Funktioniert nur wenn PluginTraceLogSetting auf
    /// All oder Exception steht — sonst leeres Ergebnis (kein Fehler).
    /// </summary>
    private void CapturePluginTraceLogs(DateTime testStartUtc, string testId)
    {
        try
        {
            var query = new QueryExpression("plugintracelog")
            {
                ColumnSet = new ColumnSet("typename", "messageblock", "createdon",
                    "performanceexecutionduration"),
                Criteria = new FilterExpression
                {
                    Conditions = {
                        new ConditionExpression("createdon", ConditionOperator.GreaterEqual, testStartUtc)
                    }
                },
                Orders = { new OrderExpression("createdon", OrderType.Ascending) },
                TopCount = 200
            };
            var results = _service.RetrieveMultiple(query);
            if (results.Entities.Count == 0)
            {
                Log($"      Plugin-Trace-Logs: 0 Einträge seit Teststart " +
                    $"(PluginTraceLogSetting evtl. inaktiv).");
                return;
            }
            Log($"      Plugin-Trace-Logs ({results.Entities.Count} Einträge seit Teststart):");
            foreach (var e in results.Entities)
            {
                var typeName = e.GetAttributeValue<string>("typename") ?? "<?>";
                var createdOn = e.GetAttributeValue<DateTime>("createdon");
                var duration = e.GetAttributeValue<int?>("performanceexecutionduration") ?? 0;
                var msg = e.GetAttributeValue<string>("messageblock") ?? "";
                // Zur Begrenzung der Log-Größe: Plugin-Trace-Body auf 2000 Zeichen kürzen.
                if (msg.Length > 2000) msg = msg.Substring(0, 2000) + "...[truncated]";
                Log($"      [{createdOn:HH:mm:ss.fff}] {typeName} ({duration}ms)");
                foreach (var line in msg.Split('\n'))
                {
                    Log($"        {line.TrimEnd('\r')}");
                }
            }
        }
        catch (Exception ex)
        {
            // Trace-Capture darf den Test nicht scheitern lassen. Mit
            // SandboxSafe-Wrapper (v5.3.4) wird das eine managed
            // Exception, kein Sandbox-Wachter-Verstoß.
            Log($"      Plugin-Trace-Capture nicht möglich: {ex.Message}");
        }
    }

    /// <summary>
    /// OE-6: write validator findings into the runner log. Warning and Info
    /// findings are emitted for visibility (test still runs); Error findings
    /// are emitted before the run is aborted.
    /// </summary>
    private void LogValidationFindings(ValidationReport report)
    {
        if (report.Findings.Count == 0) return;
        Log($"  Pack-Validation: {report.ErrorCount} Error, {report.WarningCount} Warning, {report.InfoCount} Info.");
        foreach (var f in report.Findings)
        {
            var loc = f.StepNumber.HasValue ? $"Step {f.StepNumber.Value}" : "(test)";
            var suggestion = string.IsNullOrEmpty(f.Suggestion) ? "" : $" -- {f.Suggestion}";
            Log($"    [{f.Severity}] {loc} {f.Code}: {f.Message}{suggestion}");
        }
    }

    /// <summary>
    /// OE-6: render error-severity findings as a single human-readable string
    /// for <see cref="TestCaseResult.ErrorMessage"/>. Warning/Info entries are
    /// excluded — they are already in the runner log.
    /// </summary>
    private static string FormatValidationErrors(ValidationReport report)
    {
        var sb = new StringBuilder();
        sb.Append("Pre-run validation failed with ").Append(report.ErrorCount).Append(" error(s):");
        foreach (var f in report.Findings.Where(f => f.Severity == ValidationSeverity.Error))
        {
            var loc = f.StepNumber.HasValue ? $"Step {f.StepNumber.Value}" : "(test)";
            sb.Append("\n  ").Append(loc).Append(' ').Append(f.Code).Append(": ").Append(f.Message);
            if (!string.IsNullOrEmpty(f.Suggestion))
            {
                sb.Append(" -- ").Append(f.Suggestion);
            }
        }
        return sb.ToString();
    }

    // ================================================================
    //  Phase 1: Setup (Preconditions)
    // ================================================================
    //  Teststeps ausführen (einheitliche Liste ab ADR-0004)
    // ================================================================

    /// <summary>
    /// Ausführung aller Steps in JSON-Reihenfolge. Pro Step wird ein
    /// StepResult angehängt (Erfolg oder Fehler). Fehlerverhalten:
    /// onError="stop" (Default für Non-Assert) wirft weiter und beendet
    /// den Test mit Outcome=Error. onError="continue" (Default für Assert,
    /// override sonst) schluckt die Exception — Test läuft weiter und ist
    /// am Ende Failed, wenn mindestens ein Step nicht Success=true war.
    /// </summary>
    private void ExecuteSteps(TestCase tc, TestContext ctx, TestCaseResult tcResult)
    {
        foreach (var step in tc.Steps)
        {
            Log($"    Step {step.StepNumber}: {step.Description} [{step.Action}]");

            // StepResult erzeugen bevor die Action läuft, damit auch bei Fehler
            // ein Eintrag in tcResult.StepResults landet. Der Orchestrator
            // persistiert diese Liste pro Step als jbe_teststep-Record.
            var stepResult = new StepResult
            {
                StepNumber = step.StepNumber,
                Action = step.Action,
                Alias = step.Alias,
                Entity = step.Entity,
                Description = string.IsNullOrEmpty(step.Description)
                    ? step.Action
                    : step.Description
            };
            var stepSw = Stopwatch.StartNew();

            // ADR-0011: Lauf-Bedingung VOR jeder Ausführung prüfen (auch vor dem
            // sandbox-safe expectFailure-Pfad). Nicht erfüllt -> Step übersprungen
            // (Skipped, kein Failure). Unaufgelöster Platzhalter oder unbekannter
            // Operator -> harter Fehler (Outcome=Error, KEIN stiller Skip), unabhängig
            // von onError: eine kaputte Bedingung ist ein Test-Defekt.
            if (step.Condition != null)
            {
                bool conditionMet;
                string conditionDesc;
                try
                {
                    conditionMet = EvaluateStepCondition(step.Condition, ctx, out conditionDesc);
                }
                catch (Exception condEx)
                {
                    stepResult.Success = false;
                    stepResult.Message = condEx.Message;
                    Log($"      condition-Fehler: {condEx.Message}");
                    stepSw.Stop();
                    stepResult.DurationMs = stepSw.ElapsedMilliseconds;
                    tcResult.StepResults.Add(stepResult);
                    throw;
                }

                if (!conditionMet)
                {
                    stepResult.Skipped = true;
                    stepResult.Success = true; // Skipped zählt nicht als Failure (Outcome-Logik filtert Skipped)
                    stepResult.SkipReason = conditionDesc;
                    stepResult.Message = $"Übersprungen (condition nicht erfüllt): {conditionDesc}";
                    Log($"      SKIP (condition): {conditionDesc}");
                    stepSw.Stop();
                    stepResult.DurationMs = stepSw.ElapsedMilliseconds;
                    tcResult.StepResults.Add(stepResult);
                    continue;
                }
                Log($"      condition erfüllt: {conditionDesc}");
            }

            // ADR-0005 (FB-31): Steps mit expectFailure/expectException auf primitiven
            // Service-Aktionen (Create/Update/Delete/ExecuteRequest) laufen über den
            // sandbox-sicheren Pfad. ExecuteMultipleRequest mit ContinueOnError=true
            // verhindert, dass der Sandbox-Wachter "ISV reduced transaction count"
            // (0x80040265) wirft, weil das Plugin keine Service-Exception fängt —
            // Faults landen in ExecuteMultipleResponse.Responses[0].Fault.
            var isAssertOuter = step.Action.Equals("Assert", StringComparison.OrdinalIgnoreCase);
            var hasExpectFailureOuter = !isAssertOuter &&
                ((step.ExpectFailure ?? false) || step.ExpectException != null);

            if (hasExpectFailureOuter && IsSandboxSafeAction(step.Action))
            {
                try
                {
                    var resolvedFieldsSb = ResolveFieldValues(
                        _dataFactory.ResolveTemplateData(step.Fields, ctx), ctx);
                    ExecuteStepInSandboxBoundary(step, ctx, resolvedFieldsSb, stepResult);
                }
                catch (Exception ex)
                {
                    // Nur wenn der Sandbox-Pfad selbst einen Fehler hat (z.B. unbekannter
                    // Action-Typ, ResolveFieldValues wirft). Sandbox-Wurf von dem reinen
                    // Step-Service-Call kommt nicht hier an — er ist im Fault-Pfad.
                    stepResult.Success = false;
                    stepResult.Message = ex.Message;
                    Log($"      Sandbox-Pfad-Fehler: {ex.Message}");

                    var onErrorSb = step.OnError?.ToLowerInvariant();
                    if (onErrorSb != "continue")
                    {
                        stepSw.Stop();
                        stepResult.DurationMs = stepSw.ElapsedMilliseconds;
                        tcResult.StepResults.Add(stepResult);
                        throw;
                    }
                }
                finally
                {
                    if (!string.IsNullOrEmpty(stepResult.Alias) && ctx.Records.TryGetValue(stepResult.Alias!, out var recSb))
                    {
                        stepResult.RecordId = recSb.Id;
                        if (string.IsNullOrEmpty(stepResult.Entity))
                            stepResult.Entity = recSb.EntityName;
                    }
                    stepSw.Stop();
                    stepResult.DurationMs = stepSw.ElapsedMilliseconds;
                    tcResult.StepResults.Add(stepResult);
                }
                continue;
            }

            try
            {
                var resolvedFields = ResolveFieldValues(
                    _dataFactory.ResolveTemplateData(step.Fields, ctx), ctx);

                switch (step.Action.ToUpperInvariant())
                {
                    case "CREATERECORD":
                        StepCreateGenericRecord(step, ctx, resolvedFields);
                        break;

                    case "UPDATERECORD":
                        StepUpdateGenericRecord(step, ctx, resolvedFields);
                        break;

                    case "DELETERECORD":
                        StepDeleteGenericRecord(step, ctx);
                        break;

                    case "FINDRECORD":
                    case "WAITFORRECORD":
                        StepWaitForRecord(step, ctx);
                        break;

                    case "WAITFORFIELDVALUE":
                        StepWaitForFieldValue(step, ctx);
                        break;

                    case "WAITFORNOTEXISTS":
                        StepWaitForNotExists(step, ctx);
                        break;

                    case "WAITFORASYNCCOMPLETION":
                        // ADR 2026-06-28: async-Job-Quiescence-Wait, CLI-/Core-only. Im
                        // Plugin-Sandbox-Pfad (Custom-API-Sync, Async-CRUD-Trigger, ChunkWorker)
                        // würde ein langer asyncoperation-Poll das 2-min-Sandbox-Limit sprengen ->
                        // sauber skippen (kein Failure), analog BrowserAction. Nur der headless
                        // CLI-Pfad setzt AllowAsyncOperationPolling=true.
                        if (!AllowAsyncOperationPolling)
                        {
                            Log("      SKIP WaitForAsyncCompletion: nur im CLI-Pfad unterstützt " +
                                "(asyncoperation-Poll im Plugin-Sandbox-2-min-Limit ungeeignet). Siehe ADR 2026-06-28.");
                            stepResult.Message = "WaitForAsyncCompletion skipped: CLI-only (asyncoperation polling " +
                                "is not safe in the Plugin-Sandbox path). Use the CLI path for async-chain tests.";
                        }
                        else
                        {
                            StepWaitForAsyncCompletion(step, ctx);
                        }
                        break;

                    case "ASSERTENVIRONMENT":
                        StepAssertEnvironment(step, ctx);
                        break;

                    case "EXECUTEREQUEST":
                    case "CALLCUSTOMAPI":   // Legacy-Alias (ADR-0007)
                    case "EXECUTEACTION":   // Legacy-Alias (ADR-0007)
                        StepExecuteRequest(step, ctx, resolvedFields);
                        break;

                    case "RETRIEVERECORD":
                        StepRetrieveRecord(step, ctx);
                        break;

                    case "SETENVIRONMENTVARIABLE":
                        StepSetEnvironmentVariable(step, ctx);
                        break;

                    case "RETRIEVEENVIRONMENTVARIABLE":
                        StepRetrieveEnvironmentVariable(step, ctx);
                        break;

                    case "ASSERT":
                        StepAssert(step, ctx, stepResult);
                        break;

                    case "WAIT":
                        var waitSecs = step.WaitSeconds ?? step.TimeoutSeconds;
                        Log($"      Warte {waitSecs}s...");
                        Thread.Sleep(waitSecs * 1000);
                        break;

                    case "DELAY":
                        var delayMs = step.DelayMs ?? 500;
                        Log($"      Delay {delayMs}ms...");
                        Thread.Sleep(delayMs);
                        break;

                    case "BROWSERACTION":
                        // ADR-0006: BrowserAction is CLI-only. The Plugin-Sandbox path
                        // injects null and skips with a clear message — no failure,
                        // no broken test run. The CLI path injects a Playwright-backed
                        // executor that handles the step.
                        if (_browser == null)
                        {
                            Log($"      SKIP BrowserAction (operation={step.Operation ?? "?"}): " +
                                $"not supported in this execution path (likely Plugin-Sandbox). See ADR-0006.");
                            stepResult.Message = "BrowserAction skipped: not supported in the Plugin-Sandbox path. " +
                                "Use the CLI path with --browser-state for UI tests.";
                        }
                        else
                        {
                            Log($"      BrowserAction operation={step.Operation ?? "?"}");
                            try
                            {
                                // Async-over-sync because TestRunner is sync-by-design (Plugin-compatible).
                                // The BrowserActionExecutor manages browser lifecycle internally and yields
                                // quickly between operations.
                                _browser.ExecuteAsync(step, ctx).GetAwaiter().GetResult();
                            }
                            catch
                            {
                                // ADR-0006 Phase 1d: Capture diagnostics into StepResult so the
                                // Orchestrator can upload them to jbe_testrunresult File-fields.
                                stepResult.Diagnostics = _browser.LastDiagnostics;
                                throw;
                            }
                        }
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Unbekannte Step-Action: {step.Action}");
                }

                // expectFailure / expectException: bei Non-Assert-Actions
                // muss eine Exception gekommen sein. Wenn nicht: Fail.
                var isAssert = step.Action.Equals("Assert", StringComparison.OrdinalIgnoreCase);
                var expectFailure = !isAssert &&
                    ((step.ExpectFailure ?? false) || step.ExpectException != null);

                if (expectFailure)
                {
                    stepResult.Success = false;
                    stepResult.Message = "Expected exception but action succeeded.";
                    stepResult.ExpectedDisplay = step.ExpectException != null
                        ? "Exception matching expectException"
                        : "Any exception";
                    stepResult.ActualDisplay = "<no exception>";
                }
                else if (!isAssert)
                {
                    // Standard-Fall: kein expectFailure, Action lief durch = success.
                    // Assert setzt Success selbst (auch im Fail-Case).
                    stepResult.Success = true;
                }

                // Enrich mit RecordId aus ctx (falls Alias bekannt wurde)
                if (!string.IsNullOrEmpty(stepResult.Alias) && ctx.Records.TryGetValue(stepResult.Alias, out var rec))
                {
                    stepResult.RecordId = rec.Id;
                    if (string.IsNullOrEmpty(stepResult.Entity))
                        stepResult.Entity = rec.EntityName;
                }
            }
            catch (Exception ex)
            {
                var isAssertCatch = step.Action.Equals("Assert", StringComparison.OrdinalIgnoreCase);
                var expectFailureCatch = !isAssertCatch &&
                    ((step.ExpectFailure ?? false) || step.ExpectException != null);

                if (expectFailureCatch)
                {
                    // Exception war erwartet. Optional gegen Spec matchen.
                    // A13-Fix (Plugin v5.3.2): bei FAILED-Match die actualException
                    // mit Type + ErrorCode-Hinweis aufbereiten, damit der Test-Autor
                    // sofort erkennt ob er eine Plattform- oder Plugin-Exception hat.
                    var (ok, reason) = EvaluateExpectException(step.ExpectException, ex);
                    var exType = ex.GetType().Name;
                    var actualCode = ExtractErrorCode(ex);
                    var actualMsg = Truncate(ex.Message ?? "", 300);
                    var actualSummary = string.IsNullOrEmpty(actualCode)
                        ? $"{exType}: {actualMsg}"
                        : $"{exType} [{actualCode}]: {actualMsg}";

                    stepResult.Success = ok;
                    stepResult.ExpectedDisplay = step.ExpectException != null
                        ? FormatExpectException(step.ExpectException)
                        : "Any exception";
                    stepResult.ActualDisplay = actualSummary;
                    stepResult.Message = ok
                        ? $"OK: Expected exception caught — {actualSummary}"
                        : $"expectException-Match fehlgeschlagen. {reason} | Tatsächlich: {actualSummary}";
                    Log($"      expectFailure: {(ok ? "OK" : "MISMATCH")} -- {actualSummary}");
                    continue; // zum nächsten Step, StepResult wird im finally geschrieben
                }

                stepResult.Success = false;
                stepResult.Message = ex.Message;

                // OnError: Default per Action-Typ
                //  - Assert: "continue" (Failure ist normales Ergebnis)
                //  - Alle anderen: "stop" (Exception beendet den Test als Error)
                var onError = step.OnError?.ToLowerInvariant();
                var defaultStop = !step.Action.Equals("Assert", StringComparison.OrdinalIgnoreCase);
                var shouldStop = onError switch
                {
                    "continue" => false,
                    "stop" => true,
                    _ => defaultStop
                };

                if (shouldStop) throw;
                Log($"      Fehler (onError=continue): {ex.Message}");
            }
            finally
            {
                stepSw.Stop();
                stepResult.DurationMs = stepSw.ElapsedMilliseconds;
                tcResult.StepResults.Add(stepResult);
            }
        }
    }

    // ================================================================
    //  Assert als Action (ADR-0004)
    // ================================================================

    private void StepAssert(TestStep step, TestContext ctx, StepResult stepResult)
    {
        // TestStep → internes TestAssertion-Objekt für die AssertionEngine.
        // Entity ist im JSON typischerweise als EntitySetName (Plural, Web-API-Form)
        // angegeben, z.B. "markant_fg_contactsources". Die AssertionEngine arbeitet
        // mit QueryExpression/IOrganizationService und braucht den LogicalName
        // (Singular). Deshalb hier auf LogicalName ummapppen.
        var resolvedEntity = string.IsNullOrEmpty(step.Entity)
            ? step.Entity
            : ResolveEntity(step.Entity);

        var assertion = new TestAssertion
        {
            Target = step.Target ?? "Query",
            Field = step.Field ?? "",
            Entity = resolvedEntity,
            RecordRef = step.RecordRef,
            Filter = step.Filter,
            Operator = step.Operator ?? "Equals",
            Value = step.Value,
            Description = step.Description
        };

        var assertResult = _assertionEngine.Evaluate(assertion, ctx, _service);

        // Ergebnis in StepResult übertragen
        stepResult.Success = assertResult.Passed;
        stepResult.Message = assertResult.Message;
        stepResult.AssertField = assertion.Field;
        stepResult.ExpectedDisplay = assertResult.ExpectedDisplay;
        stepResult.ActualDisplay = assertResult.ActualDisplay;
        if (!string.IsNullOrEmpty(assertion.Description))
            stepResult.Description = assertion.Description;

        var icon = assertResult.Passed ? "(OK)" : "(FAIL)";
        Log($"      Assert {icon} {assertion.Field} {assertion.Operator} {assertion.Value}");
    }

    // ================================================================
    //  Step-Condition (ADR-0011): config-adaptives Überspringen
    // ================================================================

    private static readonly System.Text.RegularExpressions.Regex _unresolvedPlaceholder =
        new(@"\{[^{}]+\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Wertet eine Step-Condition aus (ADR-0011). Genau eine Form, Vorrang
    /// all (AND) vor any (OR) vor Einfachklausel. <paramref name="description"/>
    /// trägt eine lesbare Zusammenfassung (Skip-Grund). Wirft bei unaufgelöstem
    /// Platzhalter oder unbekanntem Operator (-> Outcome=Error, kein stiller Skip).
    /// </summary>
    private bool EvaluateStepCondition(StepCondition cond, TestContext ctx, out string description)
    {
        if (cond.All != null && cond.All.Count > 0)
        {
            var parts = cond.All.Select(c => (clause: c, met: EvaluateClause(c, ctx))).ToList();
            description = "all[" + string.Join(" AND ", parts.Select(p => DescribeClause(p.clause, p.met))) + "]";
            return parts.All(p => p.met);
        }
        if (cond.Any != null && cond.Any.Count > 0)
        {
            var parts = cond.Any.Select(c => (clause: c, met: EvaluateClause(c, ctx))).ToList();
            description = "any[" + string.Join(" OR ", parts.Select(p => DescribeClause(p.clause, p.met))) + "]";
            return parts.Any(p => p.met);
        }
        var single = EvaluateClause(cond, ctx);
        description = DescribeClause(cond, single);
        return single;
    }

    /// <summary>
    /// Wertet eine einzelne Vergleichsklausel über den geteilten
    /// <see cref="ValueComparator"/> aus. left/right werden aufgelöst (mit
    /// Unaufgelöst-Prüfung); unbekannter Operator wirft.
    /// </summary>
    private bool EvaluateClause(StepConditionClause c, TestContext ctx)
    {
        var left = ResolveConditionSide(c.Left, ctx);
        var right = ResolveConditionSide(c.Right, ctx);
        if (!ValueComparator.TryEvaluate(c.Operator, left, right, out var passed))
            throw new InvalidOperationException(
                $"condition: Unbekannter Operator '{c.Operator ?? "<null>"}'. " +
                $"Erlaubt: {string.Join(", ", ValueComparator.SupportedOperators)}.");
        return passed;
    }

    /// <summary>
    /// Löst einen condition-Wert (left/right) über die PlaceholderEngine auf und
    /// erzwingt, dass kein Platzhalter überbleibt. Ein unbekannter/falsch
    /// geschriebener Alias lässt {x.fields.y} wörtlich stehen (die PlaceholderEngine
    /// wirft dort NICHT, ADR-0011 Korrektur 3) -- das würde sonst zu einem stillen,
    /// falsch-grünen Skip führen. Darum hier hart prüfen und werfen.
    /// </summary>
    private string ResolveConditionSide(string? raw, TestContext ctx)
    {
        if (string.IsNullOrEmpty(raw)) return raw ?? "";
        var resolved = _placeholderEngine.Resolve(raw, ctx);
        if (_unresolvedPlaceholder.IsMatch(resolved))
            throw new InvalidOperationException(
                $"condition: Platzhalter nicht aufgelöst: '{raw}' -> '{resolved}'. " +
                "Ein unbekannter oder falsch geschriebener Alias macht den Vergleich sonst " +
                "still falsch (ADR-0011: kein falsch-grüner Skip durch Alias-Tippfehler).");
        return resolved;
    }

    private static string DescribeClause(StepConditionClause c, bool met)
        => $"'{c.Left}' {c.Operator} '{c.Right}' = {met}";

    // ================================================================
    //  Generische Actions
    // ================================================================

    private static readonly GenericRecordWaiter _recordWaiter = new GenericRecordWaiter();

    /// <summary>Löst EntitySetName (Plural, Web API) zu LogicalName (Singular, SDK) auf.</summary>
    private string ResolveEntity(string? entityNameFromJson)
    {
        if (string.IsNullOrWhiteSpace(entityNameFromJson))
            throw new InvalidOperationException("Entity-Name fehlt.");
        return _entityMetadata.ResolveLogicalName(entityNameFromJson);
    }

    private void StepCreateGenericRecord(
        TestStep step, TestContext ctx, Dictionary<string, object?> resolvedFields)
    {
        var entityName = ResolveEntity(step.Entity);
        var alias = step.Alias ?? $"record_{step.StepNumber}";

        var entity = new Entity(entityName);
        ApplyFields(entity, resolvedFields, ctx);

        var id = _service.Create(entity);
        ctx.RegisterRecord(alias, entityName, id);
        Log($"      CreateRecord [{alias}] in '{entityName}': {id}");

        // Auto-Retrieve: wenn columns definiert, Server-generierte Felder laden
        if (step.Columns != null && step.Columns.Count > 0)
        {
            var retrieved = _service.Retrieve(entityName, id, new ColumnSet(step.Columns.ToArray()));
            ctx.FoundRecords[alias] = retrieved;
            Log($"      Auto-Retrieve: {step.Columns.Count} Spalten für [{alias}] geladen");
        }
    }

    private void StepUpdateGenericRecord(
        TestStep step, TestContext ctx, Dictionary<string, object?> resolvedFields)
    {
        var alias = step.RecordRef ?? step.Alias
            ?? throw new InvalidOperationException("UpdateRecord benötigt 'recordRef' oder 'alias'.");

        // {RECORD:alias}-Platzhalter in recordRef auflösen
        if (alias.StartsWith("{RECORD:", StringComparison.OrdinalIgnoreCase) && alias.EndsWith("}"))
            alias = alias.Substring("{RECORD:".Length, alias.Length - "{RECORD:".Length - 1);

        var recordId = ctx.ResolveRecordId(alias);
        var entityName = step.Entity != null ? ResolveEntity(step.Entity) : ctx.ResolveRecordEntityName(alias);

        var entity = new Entity(entityName, recordId);
        ApplyFields(entity, resolvedFields, ctx, allowNull: true);
        _service.Update(entity);
        Log($"      UpdateRecord [{alias}] in '{entityName}': {recordId}");
    }

    private void StepDeleteGenericRecord(TestStep step, TestContext ctx)
    {
        var alias = step.RecordRef ?? step.Alias
            ?? throw new InvalidOperationException("DeleteRecord benötigt 'recordRef' oder 'alias'.");

        if (alias.StartsWith("{RECORD:", StringComparison.OrdinalIgnoreCase) && alias.EndsWith("}"))
            alias = alias.Substring("{RECORD:".Length, alias.Length - "{RECORD:".Length - 1);

        var recordId = ctx.ResolveRecordId(alias);
        var entityName = step.Entity != null ? ResolveEntity(step.Entity) : ctx.ResolveRecordEntityName(alias);

        _service.Delete(entityName, recordId);
        Log($"      DeleteRecord [{alias}] in '{entityName}': {recordId}");
    }

    private void StepWaitForRecord(TestStep step, TestContext ctx)
    {
        var entityName = ResolveEntity(step.Entity);
        var filters = step.Filter
            ?? throw new InvalidOperationException("WaitForRecord benötigt 'filter'.");
        var alias = step.Alias ?? $"found_{step.StepNumber}";

        // Platzhalter in Filter-Werten auflösen
        var resolvedFilters = new List<FilterCondition>();
        foreach (var f in filters)
        {
            var resolvedValue = f.Value;
            if (resolvedValue is string s)
                resolvedValue = _placeholderEngine.Resolve(s, ctx);
            resolvedFilters.Add(new FilterCondition { Field = f.Field, Operator = f.Operator, Value = resolvedValue });
        }

        var columns = step.Columns?.ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var found = _recordWaiter.WaitForRecord(
            _service, entityName, resolvedFilters, columns,
            step.TimeoutSeconds, step.PollingIntervalMs,
            msg => Log($"      {msg}"),
            orderBy: step.OrderBy,
            top: step.Top,
            metadataCache: _entityMetadata);

        sw.Stop();

        if (found == null)
            throw new InvalidOperationException(
                $"WaitForRecord: Kein Record in '{entityName}' gefunden (Timeout: {step.TimeoutSeconds}s).");

        // trackForCleanup:false - ein per FindRecord/WaitForRecord GEFUNDENER Bestands-Record
        // ist kein vom Test erzeugter Record und darf nicht in die Cleanup-Löschliste. Sonst
        // löscht der Cleanup geteilte Stammdaten (z.B. den gelesenen markant_fg_fieldconfig).
        ctx.RegisterRecord(alias, entityName, found.Id, trackForCleanup: false);
        ctx.FoundRecords[alias] = found;
        Log($"      WaitForRecord [{alias}] gefunden: {found.Id} ({sw.ElapsedMilliseconds}ms)");

        // Performance-Assertion
        if (step.MaxDurationMs.HasValue && sw.ElapsedMilliseconds > step.MaxDurationMs.Value)
            throw new InvalidOperationException(
                $"WaitForRecord [{alias}]: Dauer {sw.ElapsedMilliseconds}ms überschreitet maxDurationMs={step.MaxDurationMs.Value}ms");
    }

    private void StepWaitForFieldValue(TestStep step, TestContext ctx)
    {
        var alias = step.RecordRef ?? step.Alias
            ?? throw new InvalidOperationException("WaitForFieldValue benötigt 'recordRef' oder 'alias'.");

        if (alias.StartsWith("{RECORD:", StringComparison.OrdinalIgnoreCase) && alias.EndsWith("}"))
            alias = alias.Substring("{RECORD:".Length, alias.Length - "{RECORD:".Length - 1);

        var recordId = ctx.ResolveRecordId(alias);
        var entityName = step.Entity != null ? ResolveEntity(step.Entity) : ctx.ResolveRecordEntityName(alias);
        var fieldName = step.Fields.Keys.FirstOrDefault()
            ?? throw new InvalidOperationException("WaitForFieldValue: 'field' in fields fehlt.");
        var expectedValue = step.ExpectedValue
            ?? throw new InvalidOperationException("WaitForFieldValue benötigt 'expectedValue'.");

        // Platzhalter im erwarteten Wert auflösen
        if (expectedValue is string s)
            expectedValue = _placeholderEngine.Resolve(s, ctx);

        var success = _recordWaiter.WaitForFieldValue(
            _service, entityName, recordId, fieldName, expectedValue,
            step.TimeoutSeconds, step.PollingIntervalMs,
            msg => Log($"      {msg}"));

        if (!success)
            throw new InvalidOperationException(
                $"WaitForFieldValue: '{fieldName}' hat den erwarteten Wert nicht erreicht (Timeout: {step.TimeoutSeconds}s).");

        Log($"      WaitForFieldValue [{alias}].{fieldName} = {expectedValue} erreicht");
    }

    private void StepWaitForNotExists(TestStep step, TestContext ctx)
    {
        var entityName = ResolveEntity(step.Entity);
        var filters = step.Filter
            ?? throw new InvalidOperationException("WaitForNotExists benötigt 'filter'.");

        // Platzhalter in Filter-Werten auflösen (wie StepWaitForRecord)
        var resolvedFilters = new List<FilterCondition>();
        foreach (var f in filters)
        {
            var resolvedValue = f.Value;
            if (resolvedValue is string s)
                resolvedValue = _placeholderEngine.Resolve(s, ctx);
            resolvedFilters.Add(new FilterCondition { Field = f.Field, Operator = f.Operator, Value = resolvedValue });
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var gone = _recordWaiter.WaitForRecordAbsence(
            _service, entityName, resolvedFilters,
            step.TimeoutSeconds, step.PollingIntervalMs,
            msg => Log($"      {msg}"),
            metadataCache: _entityMetadata);

        sw.Stop();

        if (!gone)
            throw new InvalidOperationException(
                $"WaitForNotExists: Record in '{entityName}' existiert noch (Timeout: {step.TimeoutSeconds}s).");

        Log($"      WaitForNotExists [{entityName}] verschwunden ({sw.ElapsedMilliseconds}ms)");

        // Performance-Assertion (Lösch-Frist), analog StepWaitForRecord
        if (step.MaxDurationMs.HasValue && sw.ElapsedMilliseconds > step.MaxDurationMs.Value)
            throw new InvalidOperationException(
                $"WaitForNotExists [{entityName}]: Dauer {sw.ElapsedMilliseconds}ms überschreitet maxDurationMs={step.MaxDurationMs.Value}ms");
    }

    /// <summary>
    /// WaitForAsyncCompletion (ADR 2026-06-28): wartet deterministisch auf das ENDE der durch
    /// vorherige Steps ausgelösten async-Plugin-Kette (asyncoperation-Quiescence), statt auf einen
    /// geratenen fachlichen Endzustand mit festem Timeout. Korreliert über regardingobjectid auf
    /// die Test-Records. Nur im CLI-Pfad erreichbar (Dispatch skippt sonst).
    /// </summary>
    private void StepWaitForAsyncCompletion(TestStep step, TestContext ctx)
    {
        // Optionale Verengung auf bestimmte Trigger-Records (regardingobjectid). Default: keine
        // Verengung -> reines Zeitfenster, das auch Folge-Jobs auf vom Plugin ERZEUGTEN Records
        // (umsatzplan, rollup/actioncard) fängt. Ein festes regobj-Set würde solche Wellen
        // verpassen und Falsch-Grün melden (Befund LM DEV 2026-06-28).
        List<Guid>? regardingIds = null;
        if (step.Aliases != null && step.Aliases.Count > 0)
        {
            regardingIds = new List<Guid>();
            foreach (var a in step.Aliases)
                regardingIds.Add(ctx.ResolveRecordId(a));  // wirft bei Alias-Tippfehler -> kein Falsch-Grün
        }
        else if (!string.IsNullOrWhiteSpace(step.Alias))
        {
            regardingIds = new List<Guid> { ctx.ResolveRecordId(step.Alias) };
        }

        var lookbackSeconds = step.LookbackSeconds ?? 20;
        var stableChecks = step.StableChecks ?? 3;
        var initialWaitMs = step.InitialWaitMs ?? 2000;
        // Zeitfenster-Start: jetzt minus Rückblick. Der Step steht direkt nach der Trigger-Operation,
        // der Rückblick fängt die ausgelöste Kette sicher.
        var windowStartUtc = DateTime.UtcNow.AddSeconds(-lookbackSeconds);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var quiescent = _recordWaiter.WaitForAsyncQuiescence(
            _service, windowStartUtc, regardingIds,
            step.TimeoutSeconds, step.PollingIntervalMs, stableChecks, initialWaitMs,
            msg => Log($"      {msg}"));

        sw.Stop();

        if (!quiescent)
            throw new InvalidOperationException(
                $"WaitForAsyncCompletion: async-Jobs nicht zur Ruhe gekommen (Timeout: {step.TimeoutSeconds}s).");

        var scope = regardingIds != null ? $"{regardingIds.Count} Record(s) + Zeitfenster" : "Zeitfenster";
        Log($"      WaitForAsyncCompletion: Quiescence nach {sw.ElapsedMilliseconds}ms ({scope}, lookback {lookbackSeconds}s)");

        // Performance-Assertion (analog StepWaitForRecord)
        if (step.MaxDurationMs.HasValue && sw.ElapsedMilliseconds > step.MaxDurationMs.Value)
            throw new InvalidOperationException(
                $"WaitForAsyncCompletion: Dauer {sw.ElapsedMilliseconds}ms überschreitet maxDurationMs={step.MaxDurationMs.Value}ms");
    }

    // ================================================================
    //  ExecuteRequest: kanonische SDK-Message-Aktion (ADR-0007)
    //  Aliasse: CallCustomApi, ExecuteAction (Verb-Aliasse im Switch).
    //  Schema-Aliasse: ActionName/ApiName (-> RequestName), Entity
    //  (-> RequestName Fallback), Parameters (-> Fields Fallback).
    // ================================================================

    private void StepExecuteRequest(
        TestStep step, TestContext ctx, Dictionary<string, object?> resolvedFields)
    {
        // ADR-0007 Fallback-Kette für den SDK-Message-Namen.
        // RequestName ist kanonisch; ActionName/ApiName sind Legacy-Aliasse;
        // Entity wird als letzter Fallback akzeptiert, um Pre-v5.3.7
        // CallCustomApi-Packs ohne Migration weiter zu unterstützen.
        var requestName = step.RequestName
            ?? step.ActionName
            ?? step.ApiName
            ?? step.Entity
            ?? throw new InvalidOperationException(
                "ExecuteRequest braucht 'requestName' (oder Legacy-Alias " +
                "'actionName'/'apiName'/'entity', siehe ADR-0007).");

        // ADR-0007 Parameter-Map: Wenn 'parameters' gesetzt ist, gewinnt
        // es gegen 'fields' (Legacy-Schema). Sonst die in ExecuteSteps
        // bereits aufgelösten Fields-Werte.
        var parameterMap = (step.Parameters != null && step.Parameters.Count > 0)
            ? _dataFactory.ResolveTemplateData(step.Parameters, ctx)
            : resolvedFields;

        var request = new OrganizationRequest(requestName);

        // Felder als typisierte Parameter auflösen
        foreach (var kvp in parameterMap)
        {
            if (kvp.Value == null) continue;
            request[kvp.Key] = ResolveTypedValue(kvp.Value, ctx);
        }

        var response = _service.Execute(request);
        Log($"      ExecuteRequest '{requestName}' ausgeführt");
        HandleExecuteRequestResponse(step, ctx, response);
        WaitAfterExecuteRequest(step);
    }

    private void HandleExecuteRequestResponse(TestStep step, TestContext ctx, OrganizationResponse response)
    {
        // A4 / ZastrPay-Feedback: outputAlias macht den OrganizationResponse
        // mit nativen Typen unter Alias verfügbar für {alias.outputs.X}
        // und {alias.outputs.X[type=Y]}-Platzhalter.
        if (!string.IsNullOrEmpty(step.OutputAlias) && response.Results.Count > 0)
        {
            var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in response.Results)
            {
                outputs[kvp.Key] = kvp.Value;
            }
            ctx.OutputAliases[step.OutputAlias!] = outputs;
            Log($"      Response: {response.Results.Count} Output-Werte unter outputAlias='{step.OutputAlias}' gespeichert");
        }

        // Backward-Compat: bestehender Alias-Pfad legt String-Repräsentationen
        // in GeneratedValues ab (alte Tests nutzen das ggf. über {alias.response.X}-
        // Pattern, das aber keine offizielle Resolver-Syntax hatte).
        if (!string.IsNullOrEmpty(step.Alias) && response.Results.Count > 0)
        {
            foreach (var kvp in response.Results)
            {
                var strVal = kvp.Value switch
                {
                    EntityReference er => er.Id.ToString(),
                    OptionSetValue osv => osv.Value.ToString(),
                    EntityCollection ec => ec.Entities.Count.ToString(),
                    Guid g => g.ToString(),
                    _ => kvp.Value?.ToString() ?? ""
                };
                ctx.GeneratedValues[$"{step.Alias}.response.{kvp.Key}"] = strVal;
            }
            Log($"      Response: {response.Results.Count} Werte unter [{step.Alias}] gespeichert (legacy)");
        }
    }

    private void WaitAfterExecuteRequest(TestStep step)
    {
        if (step.WaitSeconds.HasValue && step.WaitSeconds.Value > 0)
        {
            Log($"      Warte {step.WaitSeconds.Value}s nach ExecuteRequest...");
            Thread.Sleep(step.WaitSeconds.Value * 1000);
        }
    }

    /// <summary>
    /// Löst einen Feldwert auf. Wenn der Wert ein JObject mit "$type" ist,
    /// wird der entsprechende SDK-Typ erzeugt (EntityReference, OptionSetValue, etc.).
    /// Primitive Werte werden per ConvertValue konvertiert.
    /// </summary>
    private object ResolveTypedValue(object value, TestContext ctx)
    {
        if (value is string s) return s;
        if (value is not JToken token) return ConvertValue(value);

        // Primitive JTokens
        switch (token.Type)
        {
            case JTokenType.String:
                return _placeholderEngine.Resolve(token.Value<string>()!, ctx);
            case JTokenType.Integer:
                return token.Value<int>();
            case JTokenType.Boolean:
                return token.Value<bool>();
            case JTokenType.Float:
                return token.Value<decimal>();
            case JTokenType.Null:
                return null!;
        }

        // Typisierte Objekte: JObject mit $type
        if (token is JObject obj && obj.ContainsKey("$type"))
        {
            var typeName = obj["$type"]!.Value<string>()!;
            return typeName.ToUpperInvariant() switch
            {
                "ENTITYREFERENCE" => ResolveEntityReferenceParam(obj, ctx),
                "GUID" => ResolveGuidParam(obj, ctx),
                "GUIDARRAY" => ResolveGuidArrayParam(obj, ctx),
                "OPTIONSETVALUE" => new OptionSetValue(obj["value"]!.Value<int>()),
                "MONEY" => new Money(obj["value"]!.Value<decimal>()),
                "ENTITY" => ResolveEntityParam(obj, ctx),
                "ENTITYCOLLECTION" => ResolveEntityCollectionParam(obj, ctx),
                _ => throw new InvalidOperationException(
                    $"Unbekannter $type: '{typeName}'. " +
                    $"Erlaubt: EntityReference, Guid, GuidArray, OptionSetValue, Money, Entity, EntityCollection")
            };
        }

        return ConvertValue(token);
    }

    private EntityReference ResolveEntityReferenceParam(JObject obj, TestContext ctx)
    {
        var entityName = obj["entity"]!.Value<string>()!;
        entityName = _entityMetadata.ResolveLogicalName(entityName);
        Guid id;
        if (obj.ContainsKey("ref"))
        {
            var refAlias = _placeholderEngine.Resolve(obj["ref"]!.Value<string>()!, ctx);
            id = ctx.ResolveRecordId(refAlias);
        }
        else if (obj.ContainsKey("id"))
        {
            var idStr = _placeholderEngine.Resolve(obj["id"]!.Value<string>()!, ctx);
            id = Guid.Parse(idStr);
        }
        else
            throw new InvalidOperationException(
                "EntityReference braucht 'ref' (Alias) oder 'id' (GUID).");
        return new EntityReference(entityName, id);
    }

    private Guid ResolveGuidParam(JObject obj, TestContext ctx)
    {
        if (obj.ContainsKey("ref"))
            return ctx.ResolveRecordId(obj["ref"]!.Value<string>()!);
        var valStr = _placeholderEngine.Resolve(obj["value"]!.Value<string>()!, ctx);
        return Guid.Parse(valStr);
    }

    private Guid[] ResolveGuidArrayParam(JObject obj, TestContext ctx)
    {
        var refs = obj["refs"] as JArray
            ?? throw new InvalidOperationException("GuidArray braucht 'refs' (Array von Alias-Namen).");
        return refs.Select(r => ctx.ResolveRecordId(r.Value<string>()!)).ToArray();
    }

    private Entity ResolveEntityParam(JObject obj, TestContext ctx)
    {
        var entityName = obj["entity"]!.Value<string>()!;
        entityName = _entityMetadata.ResolveLogicalName(entityName);
        var entity = new Entity(entityName);
        if (obj.ContainsKey("fields") && obj["fields"] is JObject fieldsObj)
        {
            foreach (var prop in fieldsObj.Properties())
            {
                var val = ResolveTypedValue(prop.Value, ctx);
                entity[prop.Name] = val;
            }
        }
        return entity;
    }

    private EntityCollection ResolveEntityCollectionParam(JObject obj, TestContext ctx)
    {
        var collection = new EntityCollection();
        if (obj.ContainsKey("entities") && obj["entities"] is JArray items)
        {
            foreach (var item in items)
                collection.Entities.Add((Entity)ResolveTypedValue(item, ctx));
        }
        return collection;
    }

    // ================================================================
    //  RetrieveRecord: Record neu laden (für {alias.fields.x} in Steps)
    // ================================================================

    private void StepRetrieveRecord(TestStep step, TestContext ctx)
    {
        var alias = step.Alias ?? step.RecordRef
            ?? throw new InvalidOperationException(
                "RetrieveRecord braucht 'alias' (eines bereits erstellten Records).");

        // {RECORD:alias}-Wrapper entfernen falls vorhanden
        if (alias.StartsWith("{RECORD:", StringComparison.OrdinalIgnoreCase) && alias.EndsWith("}"))
            alias = alias.Substring("{RECORD:".Length, alias.Length - "{RECORD:".Length - 1);

        if (!ctx.Records.TryGetValue(alias, out var record))
            throw new InvalidOperationException(
                $"RetrieveRecord: Alias '{alias}' nicht im Kontext gefunden. " +
                $"Verfügbar: [{string.Join(", ", ctx.Records.Keys)}]");

        var columns = step.Columns;
        var columnSet = (columns != null && columns.Count > 0)
            ? new ColumnSet(columns.ToArray())
            : new ColumnSet(true);

        var retrieved = _service.Retrieve(record.EntityName, record.Id, columnSet);
        ctx.FoundRecords[alias] = retrieved;

        Log($"      RetrieveRecord [{alias}] in '{record.EntityName}': " +
            $"{retrieved.Attributes.Count} Attribute geladen");
    }

    // ================================================================
    //  EnvironmentVariable-Actions (Set / Retrieve)
    //  Siehe D365TestCenter-Workspace/03_implementation/envvar-handling-in-tests.md
    // ================================================================

    private void StepSetEnvironmentVariable(TestStep step, TestContext ctx)
    {
        var schemaName = step.SchemaName
            ?? throw new InvalidOperationException(
                "SetEnvironmentVariable braucht 'schemaName'.");
        var value = step.Value
            ?? throw new InvalidOperationException(
                "SetEnvironmentVariable braucht 'value'.");
        var targetRaw = (step.Target ?? "effective").Trim();
        var targetUpper = targetRaw.ToUpperInvariant();

        var definition = RetrieveEnvVarDefinition(schemaName);
        var valueRecord = RetrieveEnvVarValueRecord(definition.Id);

        // Resolve target
        string resolvedTarget = targetUpper switch
        {
            "EFFECTIVE" => valueRecord != null ? "currentValue" : "defaultValue",
            "CURRENTVALUE" => "currentValue",
            "DEFAULTVALUE" => "defaultValue",
            _ => throw new InvalidOperationException(
                $"SetEnvironmentVariable: Unbekanntes target '{step.Target}'. " +
                "Erlaubt: effective, currentValue, defaultValue.")
        };

        // Snapshot für Auto-Restore — IMMER erstellen (FB-30 Fix, Plugin v5.3.1).
        // Vorher wurde der Snapshot nur bei gesetztem alias erstellt; das führte
        // dazu dass Tests ohne alias den EnvVar-Wert dauerhaft veränderten und
        // nachfolgende Tests kippten (Markant-Pack `gdpr-dyn9148.json`,
        // `markant_gdpr_pseudonym_enabled` blieb auf 'false' hängen).
        // alias ist nur noch für explizites Referenzieren des Snapshot-Objekts
        // in nachfolgenden Steps relevant; das Auto-Restore selbst ist Default.
        var snap = new EnvVarSnapshot
        {
            SchemaName = schemaName,
            DefinitionId = definition.Id,
            ResolvedTarget = resolvedTarget,
            ValueRecordExistedBefore = valueRecord != null,
            ValueRecordId = valueRecord?.Id,
            OriginalValue = valueRecord?.GetAttributeValue<string>("value"),
            OriginalDefaultValue = resolvedTarget == "defaultValue"
                ? definition.GetAttributeValue<string>("defaultvalue")
                : null
        };
        ctx.EnvVarSnapshots.Add(snap);

        // Schreiben
        if (resolvedTarget == "currentValue")
        {
            if (valueRecord != null)
            {
                var upd = new Entity("environmentvariablevalue", valueRecord.Id);
                upd["value"] = value;
                _service.Update(upd);
            }
            else
            {
                var create = new Entity("environmentvariablevalue");
                create["environmentvariabledefinitionid"] =
                    new EntityReference("environmentvariabledefinition", definition.Id);
                create["value"] = value;
                create["schemaname"] = schemaName;
                var newId = _service.Create(create);
                if (snap != null) snap.ValueRecordId = newId;
            }
        }
        else
        {
            // defaultValue: PATCH auf Definition. Erzeugt Unmanaged Active Layer
            // auf Managed-Envs (in Dataverse normal, kein Warn-Case).
            var upd = new Entity("environmentvariabledefinition", definition.Id);
            upd["defaultvalue"] = value;
            _service.Update(upd);
        }

        Log($"      SetEnvironmentVariable [{schemaName}] target={resolvedTarget} value='{value}'" +
            (snap != null ? " (Snapshot für Auto-Restore erzeugt)" : ""));
    }

    private void StepRetrieveEnvironmentVariable(TestStep step, TestContext ctx)
    {
        var schemaName = step.SchemaName
            ?? throw new InvalidOperationException(
                "RetrieveEnvironmentVariable braucht 'schemaName'.");
        var alias = step.Alias
            ?? throw new InvalidOperationException(
                "RetrieveEnvironmentVariable braucht 'alias'.");
        var sourceRaw = (step.Source ?? "effective").Trim();
        var sourceUpper = sourceRaw.ToUpperInvariant();

        var definition = RetrieveEnvVarDefinition(schemaName);
        var valueRecord = RetrieveEnvVarValueRecord(definition.Id);

        string? resolvedValue;
        string resolvedSource;

        switch (sourceUpper)
        {
            case "EFFECTIVE":
                if (valueRecord != null)
                {
                    resolvedValue = valueRecord.GetAttributeValue<string>("value");
                    resolvedSource = "currentValue";
                }
                else
                {
                    resolvedValue = definition.GetAttributeValue<string>("defaultvalue");
                    resolvedSource = "defaultValue";
                }
                break;

            case "CURRENTVALUE":
                resolvedValue = valueRecord?.GetAttributeValue<string>("value");
                resolvedSource = "currentValue";
                break;

            case "DEFAULTVALUE":
                resolvedValue = definition.GetAttributeValue<string>("defaultvalue");
                resolvedSource = "defaultValue";
                break;

            default:
                throw new InvalidOperationException(
                    $"RetrieveEnvironmentVariable: Unbekanntes source '{step.Source}'. " +
                    "Erlaubt: effective, currentValue, defaultValue.");
        }

        // Virtuelles Entity ins FoundRecords-Registry legen damit
        // {alias.fields.value} als Platzhalter auflösbar ist.
        var virt = new Entity("environmentvariablevalue");
        if (valueRecord != null) virt.Id = valueRecord.Id;
        virt["value"] = resolvedValue;
        virt["schemaname"] = schemaName;
        virt["resolvedsource"] = resolvedSource;
        ctx.FoundRecords[alias] = virt;

        Log($"      RetrieveEnvironmentVariable [{schemaName}] source={resolvedSource} " +
            $"value='{resolvedValue ?? "<null>"}' -> alias '{alias}'");
    }

    /// <summary>
    /// Holt die environmentvariabledefinition per schemaname. Wirft wenn nicht gefunden.
    /// </summary>
    private Entity RetrieveEnvVarDefinition(string schemaName)
    {
        var query = new QueryExpression("environmentvariabledefinition")
        {
            TopCount = 1,
            ColumnSet = new ColumnSet("environmentvariabledefinitionid", "schemaname",
                "displayname", "defaultvalue", "type")
        };
        query.Criteria.AddCondition("schemaname", ConditionOperator.Equal, schemaName);
        var results = _service.RetrieveMultiple(query);
        if (results.Entities.Count == 0)
        {
            throw new InvalidOperationException(
                $"Environment variable definition '{schemaName}' nicht gefunden in dieser Umgebung.");
        }
        return results.Entities[0];
    }

    // ================================================================
    //  expectFailure / expectException Helpers (1b)
    //  Siehe D365TestCenter-Workspace/03_implementation/expectfailure-feature.md
    // ================================================================

    /// <summary>
    /// Prüft ob die tatsächlich gefangene Exception der expectException-Spec
    /// entspricht. Ohne Spec ("irgendein Fehler reicht") liefert immer (true, "").
    /// Mehrere gesetzte Felder werden mit AND verknüpft.
    /// messageContains und messageMatches sind exklusiv (Validation).
    /// </summary>
    public static (bool Ok, string Reason) EvaluateExpectException(
        ExpectExceptionSpec? spec, Exception ex)
    {
        if (spec == null) return (true, "");

        if (!string.IsNullOrEmpty(spec.MessageContains) &&
            !string.IsNullOrEmpty(spec.MessageMatches))
        {
            return (false,
                "expectException: messageContains und messageMatches können nicht gleichzeitig gesetzt sein.");
        }

        if (!string.IsNullOrEmpty(spec.MessageContains))
        {
            if ((ex.Message ?? "").IndexOf(spec.MessageContains, StringComparison.OrdinalIgnoreCase) < 0)
                return (false,
                    $"Exception-Message enthält '{spec.MessageContains}' nicht. Actual: '{Truncate(ex.Message ?? "", 200)}'");
        }

        if (!string.IsNullOrEmpty(spec.MessageMatches))
        {
            try
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(
                    ex.Message ?? "", spec.MessageMatches,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    return (false,
                        $"Exception-Message matcht Regex '{spec.MessageMatches}' nicht. Actual: '{Truncate(ex.Message ?? "", 200)}'");
                }
            }
            catch (ArgumentException regexEx)
            {
                return (false, $"Ungültiger Regex in expectException.messageMatches: {regexEx.Message}");
            }
        }

        if (!string.IsNullOrEmpty(spec.ErrorCode))
        {
            var actualCode = ExtractErrorCode(ex);
            if (!string.Equals(actualCode, spec.ErrorCode, StringComparison.OrdinalIgnoreCase))
                return (false,
                    $"Error-Code '{spec.ErrorCode}' erwartet, actual '{actualCode ?? "<none>"}'");
        }

        if (spec.HttpStatus.HasValue)
        {
            var actualStatus = ExtractHttpStatus(ex);
            if (actualStatus != spec.HttpStatus.Value)
                return (false,
                    $"HTTP-Status {spec.HttpStatus.Value} erwartet, actual {actualStatus?.ToString() ?? "<none>"}");
        }

        return (true, "");
    }

    /// <summary>
    /// Zieht den Dataverse-Error-Code aus einer Exception (FaultException oder
    /// eingebettete Infos im Message). Null wenn nicht extrahierbar.
    /// </summary>
    private static string? ExtractErrorCode(Exception ex)
    {
        var cur = ex;
        while (cur != null)
        {
            // SDK-Pfad: FaultException<OrganizationServiceFault>
            var faultDetailProp = cur.GetType().GetProperty("Detail");
            if (faultDetailProp != null)
            {
                var detail = faultDetailProp.GetValue(cur);
                if (detail is OrganizationServiceFault fault)
                {
                    return $"0x{fault.ErrorCode:X8}";
                }
            }

            // Fallback: Message enthält oft "0x8004...":
            var match = System.Text.RegularExpressions.Regex.Match(
                cur.Message ?? "", @"0x[0-9A-Fa-f]{8}");
            if (match.Success) return match.Value;

            cur = cur.InnerException;
        }
        return null;
    }

    /// <summary>
    /// Zieht einen HTTP-Status aus einer Web-API-basierten Exception.
    /// Bei SDK-Calls meist null. Wir lesen die StatusCode-Property per
    /// Reflection (kommt erst mit .NET 5 auf HttpRequestException, während
    /// D365TestCenter.Core ein netstandard2.0-Assembly ist).
    /// Fallback: Message-Scan nach "HTTP XXX" oder "Status Code: XXX".
    /// </summary>
    private static int? ExtractHttpStatus(Exception ex)
    {
        var cur = ex;
        while (cur != null)
        {
            // Reflection-basierte Extraktion einer StatusCode-Property
            var statusProp = cur.GetType().GetProperty("StatusCode");
            if (statusProp != null)
            {
                var val = statusProp.GetValue(cur);
                if (val is int i) return i;
                if (val != null)
                {
                    // HttpStatusCode enum (System.Net) hat numeric value
                    try { return (int)Convert.ChangeType(val, typeof(int)); }
                    catch { /* ignore */ }
                }
            }

            // Message-Fallback: suche 3-stellige HTTP-Statuscodes
            var m = System.Text.RegularExpressions.Regex.Match(
                cur.Message ?? "", @"\b(4\d\d|5\d\d)\b");
            if (m.Success && int.TryParse(m.Value, out var parsed))
                return parsed;

            cur = cur.InnerException;
        }
        return null;
    }

    // ================================================================
    //  Sandbox-safe Step-Execution (ADR-0005, FB-31)
    // ================================================================

    /// <summary>
    /// Aktionstypen die im Sandbox-safe-Pfad als Single-Service-Call ausgeführt
    /// werden können. ADR-0005 (FB-31): expectFailure/expectException auf diesen
    /// Aktionen wird via ExecuteMultipleRequest gewrappt.
    /// </summary>
    private static bool IsSandboxSafeAction(string action)
    {
        return action.Equals("CreateRecord", StringComparison.OrdinalIgnoreCase)
            || action.Equals("UpdateRecord", StringComparison.OrdinalIgnoreCase)
            || action.Equals("DeleteRecord", StringComparison.OrdinalIgnoreCase)
            || action.Equals("ExecuteRequest", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Führt einen Step mit expectFailure/expectException sandbox-sicher aus.
    /// Der eigentliche Service-Call läuft via ExecuteMultipleRequest mit
    /// ContinueOnError=true — der Sandbox-Wachter "ISV reduced transaction count"
    /// wirft nicht, weil das Plugin keine Service-Exception fängt. Faults
    /// landen als ExecuteMultipleResponse.Responses[0].Fault und werden gegen
    /// die ExpectExceptionSpec gematcht.
    /// </summary>
    private void ExecuteStepInSandboxBoundary(
        TestStep step, TestContext ctx,
        Dictionary<string, object?> resolvedFields,
        StepResult stepResult)
    {
        OrganizationRequest req;
        switch (step.Action.ToUpperInvariant())
        {
            case "CREATERECORD":
                req = BuildCreateRequestFromStep(step, ctx, resolvedFields);
                break;
            case "UPDATERECORD":
                req = BuildUpdateRequestFromStep(step, ctx, resolvedFields);
                break;
            case "DELETERECORD":
                req = BuildDeleteRequestFromStep(step, ctx);
                break;
            case "EXECUTEREQUEST":
                req = BuildExecuteRequestFromStep(step, ctx, resolvedFields);
                break;
            default:
                throw new InvalidOperationException(
                    $"ExecuteStepInSandboxBoundary: Action '{step.Action}' wird im Sandbox-Pfad nicht unterstützt.");
        }

        var (response, fault) = ExecuteSandboxSafe(req);

        if (fault != null)
        {
            // Erwartete Exception ist eingetreten — Fault gegen Spec matchen.
            // Wrap als InvalidPluginExecutionException mit eingebettetem Error-Code,
            // damit EvaluateExpectException + ExtractErrorCode greifen können.
            var faultException = FaultToException(fault);
            var (ok, reason) = EvaluateExpectException(step.ExpectException, faultException);
            var actualSummary = $"OrganizationServiceFault [0x{fault.ErrorCode:X8}]: {Truncate(fault.Message ?? "", 300)}";

            stepResult.Success = ok;
            stepResult.ExpectedDisplay = step.ExpectException != null
                ? FormatExpectException(step.ExpectException)
                : "Any exception";
            stepResult.ActualDisplay = actualSummary;
            stepResult.Message = ok
                ? $"OK: Expected exception caught (sandbox-safe) — {actualSummary}"
                : $"expectException-Match fehlgeschlagen. {reason} | Tatsächlich: {actualSummary}";
            Log($"      expectFailure (sandbox-safe): {(ok ? "OK" : "MISMATCH")} -- {actualSummary}");
        }
        else
        {
            // Action ging durch — kein Fault, expectFailure ist verletzt.
            stepResult.Success = false;
            stepResult.Message = "Expected exception but action succeeded.";
            stepResult.ExpectedDisplay = step.ExpectException != null
                ? "Exception matching expectException"
                : "Any exception";
            stepResult.ActualDisplay = "<no exception>";
            Log($"      expectFailure (sandbox-safe): MISS — Aktion lief erfolgreich durch");
        }
    }

    /// <summary>
    /// Führt einen einzelnen OrganizationRequest in einem ExecuteMultipleRequest-
    /// Envelope aus. Fehler aus dem inneren Request landen NORMALERWEISE als Fault
    /// in der ExecuteMultipleResponse-Slot, nicht als Exception — das Plugin fängt
    /// nichts und der Sandbox-Wachter wirft keinen 0x80040265 (ADR-0005).
    ///
    /// AUSNAHME (Markant Session 13, 2026-05-16): Bei Custom-APIs mit Pattern 1
    /// (PluginType direkt am customapi.plugintypeid verknüpft, Stage 30
    /// MainOperation) wrappt die Plattform den Plugin-Fault als
    /// FaultException&lt;OrganizationServiceFault&gt; am ExecuteMultipleRequest-
    /// Endpoint und propagiert ihn als Exception, statt in den Fault-Slot der
    /// ExecuteMultipleResponse zu legen. Diese Variante fangen wir hier explizit
    /// und konvertieren sie in den normalen Fault-Slot-Pfad, damit
    /// ExecuteStepInSandboxBoundary den expectException-Matcher korrekt
    /// aufruft (statt dass die Exception ins äußere Catch propagiert und
    /// Outcome=Error meldet).
    ///
    /// Andere Exception-Typen (Netzwerk, Timeout, generische Plattform-Fehler)
    /// werden bewusst NICHT gefangen — sie sollen weiter propagieren und vom
    /// äußeren Catch als echter Test-Fehler behandelt werden.
    /// </summary>
    private (OrganizationResponse? Response, OrganizationServiceFault? Fault) ExecuteSandboxSafe(
        OrganizationRequest req)
    {
        var emReq = new ExecuteMultipleRequest
        {
            Settings = new ExecuteMultipleSettings
            {
                ContinueOnError = true,
                ReturnResponses = true
            },
            Requests = new OrganizationRequestCollection { req }
        };

        ExecuteMultipleResponse emResp;
        try
        {
            emResp = (ExecuteMultipleResponse)_service.Execute(emReq);
        }
        catch (FaultException<OrganizationServiceFault> faultEx)
        {
            // Plattform-Variante Custom-API Pattern 1 Stage 30 MainOperation:
            // Fault kommt am Endpoint statt im Slot. Auf Fault-Slot-Pfad umleiten,
            // damit der Aufrufer (ExecuteStepInSandboxBoundary) expectException
            // sauber matcht und Outcome=Passed statt Outcome=Error meldet.
            return (null, faultEx.Detail);
        }

        if (emResp.Responses == null || emResp.Responses.Count == 0)
        {
            return (null, null);
        }
        var first = emResp.Responses[0];
        return (first.Response, first.Fault);
    }

    /// <summary>
    /// Baut einen OrganizationServiceFault in eine Exception um, die
    /// EvaluateExpectException + ExtractErrorCode konsumieren können. Der
    /// ErrorCode wird in die Message eingebettet, damit der Regex-Fallback in
    /// ExtractErrorCode greift (cur.Detail ist hier nicht gesetzt, weil wir
    /// bewusst keine FaultException-Hierarchie aufbauen — D365TestCenter.Core
    /// ist netstandard2.0 ohne System.ServiceModel-Default).
    /// </summary>
    private static Exception FaultToException(OrganizationServiceFault fault)
    {
        var errorCodeHex = $"0x{fault.ErrorCode:X8}";
        var msg = $"{errorCodeHex}: {fault.Message}";
        return new InvalidPluginExecutionException(msg);
    }

    private CreateRequest BuildCreateRequestFromStep(TestStep step, TestContext ctx, Dictionary<string, object?> resolvedFields)
    {
        var entityName = ResolveEntity(step.Entity);
        var entity = new Entity(entityName);
        ApplyFields(entity, resolvedFields, ctx);
        return new CreateRequest { Target = entity };
    }

    private UpdateRequest BuildUpdateRequestFromStep(
        TestStep step, TestContext ctx, Dictionary<string, object?> resolvedFields)
    {
        var alias = step.RecordRef ?? step.Alias
            ?? throw new InvalidOperationException(
                "UpdateRecord (sandbox-safe) benötigt 'recordRef' oder 'alias'.");
        if (alias.StartsWith("{RECORD:", StringComparison.OrdinalIgnoreCase) && alias.EndsWith("}"))
            alias = alias.Substring("{RECORD:".Length, alias.Length - "{RECORD:".Length - 1);

        var recordId = ctx.ResolveRecordId(alias);
        var entityName = step.Entity != null ? ResolveEntity(step.Entity) : ctx.ResolveRecordEntityName(alias);

        var entity = new Entity(entityName, recordId);
        ApplyFields(entity, resolvedFields, ctx, allowNull: true);
        return new UpdateRequest { Target = entity };
    }

    private DeleteRequest BuildDeleteRequestFromStep(TestStep step, TestContext ctx)
    {
        var alias = step.RecordRef ?? step.Alias
            ?? throw new InvalidOperationException(
                "DeleteRecord (sandbox-safe) benötigt 'recordRef' oder 'alias'.");
        if (alias.StartsWith("{RECORD:", StringComparison.OrdinalIgnoreCase) && alias.EndsWith("}"))
            alias = alias.Substring("{RECORD:".Length, alias.Length - "{RECORD:".Length - 1);

        var recordId = ctx.ResolveRecordId(alias);
        var entityName = step.Entity != null ? ResolveEntity(step.Entity) : ctx.ResolveRecordEntityName(alias);

        return new DeleteRequest { Target = new EntityReference(entityName, recordId) };
    }

    private OrganizationRequest BuildExecuteRequestFromStep(
        TestStep step, TestContext ctx, Dictionary<string, object?> resolvedFields)
    {
        var requestName = step.RequestName
            ?? throw new InvalidOperationException(
                "ExecuteRequest (sandbox-safe) braucht 'requestName'.");

        var request = new OrganizationRequest(requestName);
        foreach (var kvp in resolvedFields)
        {
            if (kvp.Value == null) continue;
            request[kvp.Key] = ResolveTypedValue(kvp.Value, ctx);
        }
        return request;
    }

    private static string FormatExpectException(ExpectExceptionSpec spec)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(spec.MessageContains)) parts.Add($"messageContains='{spec.MessageContains}'");
        if (!string.IsNullOrEmpty(spec.MessageMatches)) parts.Add($"messageMatches=/{spec.MessageMatches}/");
        if (!string.IsNullOrEmpty(spec.ErrorCode)) parts.Add($"errorCode={spec.ErrorCode}");
        if (spec.HttpStatus.HasValue) parts.Add($"httpStatus={spec.HttpStatus.Value}");
        return parts.Count > 0 ? string.Join(" AND ", parts) : "Any exception";
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
    }

    // ================================================================
    //  EnvVar Helpers (1a)
    // ================================================================

    /// <summary>
    /// Holt den environmentvariablevalue-Record per Definition-Lookup. Null wenn nicht vorhanden.
    /// </summary>
    private Entity? RetrieveEnvVarValueRecord(Guid definitionId)
    {
        var query = new QueryExpression("environmentvariablevalue")
        {
            TopCount = 1,
            ColumnSet = new ColumnSet("environmentvariablevalueid", "value", "schemaname")
        };
        query.Criteria.AddCondition("environmentvariabledefinitionid", ConditionOperator.Equal, definitionId);
        var results = _service.RetrieveMultiple(query);
        return results.Entities.Count > 0 ? results.Entities[0] : null;
    }

    // ================================================================
    //  Pre-Flight-Diagnostics
    // ================================================================

    private void StepAssertEnvironment(TestStep step, TestContext ctx)
    {
        var checks = step.Filter
            ?? throw new InvalidOperationException("AssertEnvironment benötigt 'filter' mit Prüfungen.");

        var failures = new List<string>();

        foreach (var check in checks)
        {
            var checkType = check.Operator?.ToLowerInvariant() ?? "";
            var checkValue = check.Value?.ToString() ?? "";

            switch (checkType)
            {
                case "entityexists":
                    try
                    {
                        var testQuery = new QueryExpression(check.Field) { TopCount = 1, ColumnSet = new ColumnSet(false) };
                        _service.RetrieveMultiple(testQuery);
                        Log($"      OK: Entity '{check.Field}' existiert");
                    }
                    catch
                    {
                        failures.Add($"Entity '{check.Field}' existiert nicht in dieser Umgebung");
                    }
                    break;

                case "fieldexists":
                    try
                    {
                        var fieldQuery = new QueryExpression(check.Field) { TopCount = 1 };
                        fieldQuery.ColumnSet = new ColumnSet(checkValue);
                        _service.RetrieveMultiple(fieldQuery);
                        Log($"      OK: Feld '{checkValue}' auf '{check.Field}' existiert");
                    }
                    catch
                    {
                        failures.Add($"Feld '{checkValue}' auf Entity '{check.Field}' existiert nicht");
                    }
                    break;

                case "toggleactive":
                    var envVarFetch = $@"<fetch top='1'>
                        <entity name='environmentvariabledefinition'>
                            <attribute name='schemaname' />
                            <attribute name='defaultvalue' />
                            <filter>
                                <condition attribute='schemaname' operator='eq' value='{checkValue}' />
                            </filter>
                            <link-entity name='environmentvariablevalue' from='environmentvariabledefinitionid'
                                to='environmentvariabledefinitionid' link-type='outer' alias='val'>
                                <attribute name='value' />
                            </link-entity>
                        </entity>
                    </fetch>";
                    var envResult = _service.RetrieveMultiple(new FetchExpression(envVarFetch));
                    if (envResult.Entities.Count == 0)
                    {
                        failures.Add($"Umgebungsvariable '{checkValue}' nicht gefunden");
                    }
                    else
                    {
                        var defaultVal = envResult.Entities[0].GetAttributeValue<string>("defaultvalue") ?? "";
                        var currentVal = envResult.Entities[0].Contains("val.value")
                            ? ((AliasedValue)envResult.Entities[0]["val.value"]).Value?.ToString() ?? ""
                            : "";
                        var effective = !string.IsNullOrEmpty(currentVal) ? currentVal : defaultVal;
                        var isActive = IsToggleActive(effective);
                        if (isActive)
                            Log($"      OK: Toggle '{checkValue}' ist aktiv (Wert: {effective})");
                        else
                            failures.Add($"Toggle '{checkValue}' ist NICHT aktiv (Wert: {effective})");
                    }
                    break;

                case "pluginregistered":
                    var pluginFetch = $@"<fetch top='1'>
                        <entity name='sdkmessageprocessingstep'>
                            <attribute name='name' />
                            <attribute name='statecode' />
                            <link-entity name='plugintype' from='plugintypeid' to='plugintypeid' link-type='inner' alias='pt'>
                                <filter>
                                    <condition attribute='name' operator='like' value='%{checkValue}%' />
                                </filter>
                            </link-entity>
                        </entity>
                    </fetch>";
                    var pluginResult = _service.RetrieveMultiple(new FetchExpression(pluginFetch));
                    if (pluginResult.Entities.Count > 0)
                    {
                        var state = pluginResult.Entities[0].GetAttributeValue<OptionSetValue>("statecode");
                        if (state?.Value == 0)
                            Log($"      OK: Plugin '{checkValue}' registriert und aktiv");
                        else
                            failures.Add($"Plugin '{checkValue}' ist registriert aber DEAKTIVIERT");
                    }
                    else
                    {
                        failures.Add($"Plugin '{checkValue}' ist NICHT registriert");
                    }
                    break;

                default:
                    failures.Add($"Unbekannter Check-Typ: '{checkType}'");
                    break;
            }
        }

        if (failures.Count > 0)
        {
            var message = $"Pre-Flight fehlgeschlagen:\n  - {string.Join("\n  - ", failures)}";
            Log($"      FAIL: {message}");
            throw new InvalidOperationException(message);
        }

        Log($"      Alle {checks.Count} Pre-Flight-Checks bestanden");
    }

    private static bool IsToggleActive(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var lower = value.Trim().ToLowerInvariant();
        return lower == "1" || lower == "ja" || lower == "yes" || lower == "true"
            || lower == "aktiv" || lower == "active" || lower == "enabled"
            || lower == "ein" || lower == "on" || lower == "wahr";
    }

    // ================================================================
    //  Cleanup
    // ================================================================

    private void Cleanup(TestContext ctx, TestCaseResult tcResult)
    {
        var toDelete = ctx.CreatedEntities.AsEnumerable().Reverse().ToList();
        var envSnapshots = ctx.EnvVarSnapshots.AsEnumerable().Reverse().ToList();

        if (toDelete.Count == 0 && envSnapshots.Count == 0) return;

        // 1 StepResult pro Test-Cleanup-Phase (nicht pro Record), damit der
        // Steps-Tab die Cleanup-Zusammenfassung als eine Zeile zeigt statt
        // einer potentiell langen Liste von Delete-Einträgen.
        var cleanupResult = new StepResult
        {
            StepNumber = 9000,
            Action = "Cleanup",
            Description = "Cleanup"
        };
        var sw = Stopwatch.StartNew();

        // KeepRecords=true: Testdaten bewusst behalten. Cleanup-StepResult
        // trotzdem schreiben, damit die Cleanup-Zeile im Steps-Tab sichtbar
        // bleibt und dokumentiert, warum nichts gelöscht wurde.
        if (KeepRecords)
        {
            sw.Stop();
            Log($"    Cleanup übersprungen (keeprecords=true, {toDelete.Count} Records behalten, " +
                $"{envSnapshots.Count} EnvVar-Snapshots nicht restored)");
            cleanupResult.Description = $"Cleanup übersprungen: {toDelete.Count} Records + " +
                $"{envSnapshots.Count} EnvVar-Snapshots behalten (keeprecords=true)";
            cleanupResult.Success = true;
            cleanupResult.DurationMs = sw.ElapsedMilliseconds;
            tcResult.StepResults.Add(cleanupResult);
            return;
        }

        // EnvVar-Restore ZUERST (damit selbst bei Record-Delete-Fehlern die
        // Umgebung wieder sauber ist und nachfolgende Tests nicht kippen).
        int envRestored = 0, envFailed = 0;
        var firstEnvError = "";
        foreach (var snap in envSnapshots)
        {
            try
            {
                RestoreEnvVarSnapshot(snap);
                envRestored++;
            }
            catch (Exception ex)
            {
                envFailed++;
                if (string.IsNullOrEmpty(firstEnvError))
                    firstEnvError = $"EnvVar {snap.SchemaName}: {ex.Message}";
                Log($"    EnvVar-Restore fehlgeschlagen: {snap.SchemaName} -- {ex.Message}");
            }
        }

        int deleted = 0, failed = 0;
        var firstError = "";

        foreach (var item in toDelete)
        {
            try
            {
                _service.Delete(item.EntityName, item.Id);
                deleted++;
            }
            catch (Exception ex)
            {
                failed++;
                if (string.IsNullOrEmpty(firstError)) firstError = $"{item.EntityName} {item.Id}: {ex.Message}";
                Log($"    Löschen fehlgeschlagen: {item.EntityName} {item.Id} -- {ex.Message}");
            }
        }

        sw.Stop();
        Log($"    Cleanup: {deleted} gelöscht, {failed} fehlgeschlagen" +
            (envSnapshots.Count > 0 ? $", {envRestored} EnvVars restored, {envFailed} EnvVar-Fehler" : ""));

        cleanupResult.Description = $"Cleanup: {deleted} gelöscht, {failed} fehlgeschlagen" +
            (envSnapshots.Count > 0 ? $", {envRestored}/{envSnapshots.Count} EnvVars restored" : "");
        cleanupResult.Success = failed == 0 && envFailed == 0;
        cleanupResult.Message = (failed > 0) ? firstError
            : (envFailed > 0) ? firstEnvError : null;
        cleanupResult.DurationMs = sw.ElapsedMilliseconds;
        tcResult.StepResults.Add(cleanupResult);
    }

    /// <summary>
    /// Stellt den Vor-Set-Zustand einer EnvironmentVariable wieder her.
    /// Siehe D365TestCenter-Workspace/03_implementation/envvar-handling-in-tests.md Abschnitt 6.1.
    /// </summary>
    private void RestoreEnvVarSnapshot(EnvVarSnapshot snap)
    {
        if (snap.ResolvedTarget == "currentValue")
        {
            if (snap.ValueRecordExistedBefore)
            {
                // Wert auf Original zurückschreiben
                if (!snap.ValueRecordId.HasValue)
                    throw new InvalidOperationException(
                        $"Snapshot {snap.SchemaName}: ValueRecordId fehlt trotz ValueRecordExistedBefore.");
                var upd = new Entity("environmentvariablevalue", snap.ValueRecordId.Value);
                upd["value"] = snap.OriginalValue;
                _service.Update(upd);
            }
            else
            {
                // Neu erstellten Value-Record wieder löschen
                if (snap.ValueRecordId.HasValue)
                    _service.Delete("environmentvariablevalue", snap.ValueRecordId.Value);
            }
        }
        else if (snap.ResolvedTarget == "defaultValue")
        {
            // DefaultValue auf Original zurückschreiben. Der Unmanaged Active
            // Layer auf der Definition bleibt bestehen, enthält aber jetzt
            // wieder den Wert des darunter liegenden Managed-Layers (neutralisiert).
            // Wer den Layer komplett abräumen will: RemoveActiveCustomization
            // mit LogicalName=environmentvariabledefinition + Id, aber nicht Teil
            // des Auto-Cleanups (Nebenwirkungen bei Parallel-Änderungen).
            var upd = new Entity("environmentvariabledefinition", snap.DefinitionId);
            upd["defaultvalue"] = snap.OriginalDefaultValue;
            _service.Update(upd);
        }
        else
        {
            throw new InvalidOperationException(
                $"EnvVarSnapshot mit unbekanntem ResolvedTarget '{snap.ResolvedTarget}' kann nicht restored werden.");
        }
    }

    // ================================================================
    //  Hilfsmethoden
    // ================================================================

    private void ApplyFields(
        Entity entity, Dictionary<string, object?> fields,
        TestContext ctx, bool allowNull = false)
    {
        foreach (var kvp in fields)
        {
            var key = kvp.Key;
            var value = kvp.Value;
            if (value == null)
            {
                if (allowNull) entity[key] = null;
                continue;
            }

            // Web API @odata.bind Lookup-Syntax: "feldname_target@odata.bind" = "/entitysets(guid)"
            if (key.EndsWith("@odata.bind", StringComparison.OrdinalIgnoreCase))
            {
                var strVal = ConvertValue(value)?.ToString() ?? "";
                var bindResult = ParseODataBind(key, strVal);
                if (bindResult.HasValue)
                {
                    entity[bindResult.Value.FieldName] =
                        new EntityReference(bindResult.Value.TargetEntity, bindResult.Value.Id);
                    continue;
                }
            }

            // ADR-2026-06-27: $type-Feldwerte (EntityCollection/Entity/EntityReference/
            // OptionSetValue/Money/Guid) über den rekursiven ResolveTypedValue auflösen —
            // derselbe Resolver wie im ExecuteRequest-Pfad. Schaltet activityparty-Partylists
            // (z.B. "requiredattendees") und andere Deep-Insert-Strukturen im CreateRecord/
            // UpdateRecord frei. Ein roher Wert ohne $type fällt unverändert in ConvertValue.
            if (value is JObject typedObj && typedObj.ContainsKey("$type"))
            {
                entity[key] = ResolveTypedValue(typedObj, ctx);
                continue;
            }

            var converted = ConvertValue(value);

            // Metadata-basierte Typerkennung für alle Felder
            var attrType = _entityMetadata.GetAttributeType(entity.LogicalName, key);
            if (attrType != null)
            {
                switch (attrType.Value)
                {
                    case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Lookup:
                    case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Customer:
                    case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Owner:
                        if (converted is string sv && Guid.TryParse(sv, out var g))
                        {
                            var target = _entityMetadata.GetLookupTarget(entity.LogicalName, key);
                            if (target != null)
                                converted = new EntityReference(target, g);
                        }
                        break;

                    case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Money:
                        converted = converted switch
                        {
                            decimal d => new Money(d),
                            int i2 => new Money(i2),
                            double d2 => new Money((decimal)d2),
                            string ms when decimal.TryParse(ms,
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var md)
                                => new Money(md),
                            _ => converted
                        };
                        break;

                    case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Decimal:
                    case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Double:
                        converted = converted switch
                        {
                            int i4 => (decimal)i4,
                            double d3 => (decimal)d3,
                            string ds when decimal.TryParse(ds,
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var dd)
                                => dd,
                            _ => converted
                        };
                        break;

                    case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.DateTime:
                        if (converted is string ds2)
                        {
                            // Versuche verschiedene DateTime-Formate
                            if (DateTime.TryParse(ds2, System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AdjustToUniversal, out var dtVal))
                                converted = dtVal;
                            else if (ds2.Contains("_") && ds2.Length >= 15)
                            {
                                // ITT TIMESTAMP-Format: "20260406_143000_123"
                                var parts = ds2.Split('_');
                                if (parts.Length >= 2 && parts[0].Length == 8 && parts[1].Length >= 6)
                                {
                                    var dtStr = $"{parts[0].Substring(0,4)}-{parts[0].Substring(4,2)}-{parts[0].Substring(6,2)}T{parts[1].Substring(0,2)}:{parts[1].Substring(2,2)}:{parts[1].Substring(4,2)}Z";
                                    if (DateTime.TryParse(dtStr, System.Globalization.CultureInfo.InvariantCulture,
                                        System.Globalization.DateTimeStyles.AdjustToUniversal, out var dtVal2))
                                        converted = dtVal2;
                                }
                            }
                        }
                        break;

                    case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Picklist:
                    case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.State:
                    case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Status:
                        if (converted is not OptionSetValue)
                        {
                            converted = converted switch
                            {
                                int i3 => new OptionSetValue(i3),
                                long l => new OptionSetValue((int)l),
                                decimal dec => new OptionSetValue((int)dec),
                                string ps when int.TryParse(ps, out var pi)
                                    => new OptionSetValue(pi),
                                _ => converted
                            };
                        }
                        break;
                }
            }

            entity[key] = converted;
        }
    }

    /// <summary>
    /// Parst Web API @odata.bind Syntax: "parentcustomerid_account@odata.bind" = "/accounts(guid)"
    /// Gibt den SDK-Feldnamen, die Ziel-Entity und die GUID zurück.
    /// </summary>
    private (string FieldName, string TargetEntity, Guid Id)? ParseODataBind(string bindKey, string bindValue)
    {
        // Feldnamen extrahieren: @odata.bind-Suffix entfernen
        var fieldPart = bindKey.Substring(0, bindKey.Length - "@odata.bind".Length);

        // Strategie: Ziel-Entity immer aus dem Bind-VALUE extrahieren (am zuverlässigsten)
        // Der Wert hat das Format "/accounts(guid)" oder "/lm_bestellunges(guid)"
        var entitySetFromValue = ExtractEntityFromBindValue(bindValue);

        string fieldName;
        string targetEntity;

        if (entitySetFromValue != null)
        {
            // EntitySetName aus dem Wert zu LogicalName auflösen
            targetEntity = _entityMetadata.ResolveLogicalName(entitySetFromValue);

            // Feldname: versuche das Ziel-Entity-Suffix vom Feldteil abzuschneiden
            // "parentcustomerid_account" -> "parentcustomerid" ("_account" entfernen)
            // "lm_bestellungid" -> "lm_bestellungid" (kein Standard-Entity-Suffix)
            var suffix = "_" + targetEntity;
            if (fieldPart.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                fieldName = fieldPart.Substring(0, fieldPart.Length - suffix.Length);
            else
                fieldName = fieldPart; // Custom Entity Lookup: Feldname IST der vollständige Feldteil
        }
        else
        {
            // Fallback: am letzten Unterstrich aufteilen
            var lastUnderscore = fieldPart.LastIndexOf('_');
            if (lastUnderscore > 0)
            {
                fieldName = fieldPart.Substring(0, lastUnderscore);
                targetEntity = fieldPart.Substring(lastUnderscore + 1);
            }
            else
            {
                fieldName = fieldPart;
                targetEntity = fieldPart;
            }
        }

        // EntitySetName zu LogicalName auflösen
        targetEntity = _entityMetadata.ResolveLogicalName(targetEntity);

        // GUID aus dem Wert extrahieren: "/accounts(5c013fbf-...)"
        var match = System.Text.RegularExpressions.Regex.Match(
            bindValue, @"\(([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\)");
        if (match.Success && Guid.TryParse(match.Groups[1].Value, out var id))
        {
            return (fieldName, targetEntity, id);
        }

        return null;
    }

    private static string? ExtractEntityFromBindValue(string bindValue)
    {
        // Entity-Set-Name aus dem "/accounts(guid)"-Pattern extrahieren
        var match = System.Text.RegularExpressions.Regex.Match(bindValue, @"^/?(\w+)\(");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static object ConvertValue(object value)
    {
        if (value is JToken jt)
        {
            return jt.Type switch
            {
                JTokenType.String => jt.Value<string>()!,
                JTokenType.Integer => jt.Value<int>(),
                JTokenType.Float => (object)jt.Value<decimal>(),
                JTokenType.Boolean => jt.Value<bool>(),
                JTokenType.Null => null!,
                _ => jt.ToString()
            };
        }

        // Int64 -> Int32 für Dataverse-Kompatibilität
        if (value is long l) return (int)l;

        return value;
    }

    private Dictionary<string, object?> ResolveFieldValues(
        Dictionary<string, object?> fields, TestContext ctx)
    {
        var resolved = new Dictionary<string, object?>(
            fields, StringComparer.OrdinalIgnoreCase);

        foreach (var key in resolved.Keys.ToList())
        {
            string? strVal = resolved[key] switch
            {
                string s => s,
                JToken { Type: JTokenType.String } jt => jt.Value<string>(),
                _ => null
            };

            if (strVal == null) continue;

            // Platzhalter auflösen über die PlaceholderEngine
            strVal = _placeholderEngine.Resolve(strVal, ctx);

            resolved[key] = strVal;
        }

        return resolved;
    }

    private void Log(string message)
    {
        _log.AppendLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");
    }
}
