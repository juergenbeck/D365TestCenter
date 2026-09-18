using Microsoft.Xrm.Sdk;
using D365TestCenter.Core;

namespace D365TestCenter.CrmPlugin;

/// <summary>
/// Async-Plugin auf <c>jbe_testrun</c> (ADR-0009 Phase 2). Koordinator des Worker-Modells:
/// löst den Lauf in <c>jbe_testchunk</c>-Records auf (Fan-Out), die Worker führen sie aus.
/// Ersetzt die depth-begrenzte Batch-Cascade von <see cref="RunTestsOnStatusChange"/> (Engine-Mutex
/// über die EnvVar <c>jbe_use_worker</c>, C-08: genau ein Pfad pro Run).
///
/// Die gesamte testbare Logik liegt in <see cref="CoordinatorOrchestrator"/> (Core, mit
/// Fake-Service unit-getestet); dieses Plugin ist nur die dünne Glue: Kontext + EnvVars ziehen,
/// SYSTEM-Service erzeugen, Orchestrator aufrufen, Fehler im Record dokumentieren (nicht werfen).
///
/// Registrierung:
///   Entity:              jbe_testrun
///   Message:             Create (PostOperation, Async)
///   Message:             Update (PostOperation, Async), FilteringAttributes: jbe_teststatus
///   "Delete AsyncOperation if StatusCode = Successful": an
/// </summary>
public sealed class RunCoordinator : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var context = (IPluginExecutionContext)serviceProvider
            .GetService(typeof(IPluginExecutionContext));
        var trace = (ITracingService)serviceProvider
            .GetService(typeof(ITracingService));
        var factory = (IOrganizationServiceFactory)serviceProvider
            .GetService(typeof(IOrganizationServiceFactory));
        // SYSTEM-Kontext (null): Lese-/Schreibzugriff unabhängig von der Rolle des Auslösers.
        var service = factory.CreateOrganizationService(null);

        if (context.PrimaryEntityName != WorkerSchema.TestRunEntity)
            return;

        // Engine-Mutex (C-08): nur im Worker-Modus handeln; sonst läuft die alte Cascade.
        if (!WorkerEnvironment.ReadBool(service, WorkerSchema.EnvUseWorker, false))
        {
            trace.Trace("RunCoordinator: jbe_use_worker != true -> Worker-Modell inaktiv, skip.");
            return;
        }

        var chunkSize = WorkerEnvironment.ReadInt(
            service, WorkerSchema.EnvChunkSize, WorkerSchema.DefaultChunkSize);
        var budgetSeconds = WorkerEnvironment.ReadInt(
            service, WorkerSchema.EnvBudgetSeconds, WorkerSchema.DefaultBudgetSeconds);

        try
        {
            var outcome = new CoordinatorOrchestrator(service, msg => trace.Trace(msg))
                .Run(context.PrimaryEntityId, chunkSize, budgetSeconds);
            trace.Trace("RunCoordinator: Ergebnis {0}", outcome);
        }
        catch (Exception ex)
        {
            // Async-Best-Practice (F-04): Fehler im Record dokumentieren, NICHT werfen.
            trace.Trace("RunCoordinator Fehler: {0}", ex);
            TrySetRunError(service, context.PrimaryEntityId, ex.Message);
        }
    }

    private static void TrySetRunError(IOrganizationService service, Guid testRunId, string message)
    {
        try
        {
            service.Update(new Entity(WorkerSchema.TestRunEntity, testRunId)
            {
                [WorkerSchema.RunStatus] = new OptionSetValue(WorkerSchema.StatusError),
                [WorkerSchema.RunSummary] = "Koordinator-Fehler: " +
                    (message.Length > 3000 ? message.Substring(0, 3000) : message),
                [WorkerSchema.RunCompletedOn] = DateTime.UtcNow
            });
        }
        catch
        {
            /* Error-Update ist non-critical */
        }
    }
}
