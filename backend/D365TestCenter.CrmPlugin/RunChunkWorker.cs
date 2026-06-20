using Microsoft.Xrm.Sdk;
using D365TestCenter.Core;

namespace D365TestCenter.CrmPlugin;

/// <summary>
/// Async-Plugin auf <c>jbe_testchunk</c> (ADR-0009 Phase 3). Worker des Fan-Out-Modells: fuehrt die
/// Tests eines Chunks ab dem Gruppen-Cursor aus, schreibt die Ergebnisse idempotent und rettet sich
/// per Self-Trigger-Continuation vor dem 120-s-Sandbox-Limit; der den letzten Chunk schliessende
/// Worker aggregiert den Lauf am Plateau.
///
/// Die gesamte testbare Logik liegt in <see cref="ChunkWorkerOrchestrator"/> (Core, mit Fake-Service
/// unit-getestet); dieses Plugin ist nur die duenne Glue. Ein Engine-Mutex-Check eruebrigt sich:
/// jbe_testchunk-Records entstehen ausschliesslich im Worker-Modus (durch den Koordinator).
///
/// Registrierung:
///   Entity:              jbe_testchunk
///   Message:             Create (PostOperation, Async)
///   Message:             Update (PostOperation, Async), FilteringAttributes: jbe_chunkstatus
///   "Delete AsyncOperation if StatusCode = Successful": an
/// </summary>
public sealed class RunChunkWorker : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var context = (IPluginExecutionContext)serviceProvider
            .GetService(typeof(IPluginExecutionContext));
        var trace = (ITracingService)serviceProvider
            .GetService(typeof(ITracingService));
        var factory = (IOrganizationServiceFactory)serviceProvider
            .GetService(typeof(IOrganizationServiceFactory));
        // SYSTEM-Kontext (null): Zugriff auf alle Testfaelle/Results unabhaengig von der Rolle.
        var service = factory.CreateOrganizationService(null);

        if (context.PrimaryEntityName != WorkerSchema.TestChunkEntity)
            return;

        var budgetSeconds = WorkerEnvironment.ReadInt(
            service, WorkerSchema.EnvBudgetSeconds, WorkerSchema.DefaultBudgetSeconds);

        try
        {
            var outcome = new ChunkWorkerOrchestrator(service, msg => trace.Trace(msg))
                .Run(context.PrimaryEntityId, budgetSeconds);
            trace.Trace("RunChunkWorker: Ergebnis {0}", outcome);
        }
        catch (Exception ex)
        {
            // Async-Best-Practice (F-04): Fehler im Chunk dokumentieren, NICHT werfen.
            trace.Trace("RunChunkWorker Fehler: {0}", ex);
            TrySetChunkError(service, context.PrimaryEntityId, ex.Message);
        }
    }

    private static void TrySetChunkError(IOrganizationService service, Guid chunkId, string message)
    {
        try
        {
            service.Update(new Entity(WorkerSchema.TestChunkEntity, chunkId)
            {
                [WorkerSchema.ChunkStatus] = new OptionSetValue(WorkerSchema.ChunkError),
                [WorkerSchema.ChunkErrorDetails] = message.Length > 100000
                    ? message.Substring(0, 100000) : message,
                [WorkerSchema.ChunkCompletedOn] = DateTime.UtcNow
            });
        }
        catch
        {
            /* Error-Update ist non-critical */
        }
    }
}
