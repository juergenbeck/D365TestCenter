using Microsoft.Xrm.Sdk;
using D365TestCenter.Core;

namespace D365TestCenter.CrmPlugin;

/// <summary>
/// Async-Step auf der Custom-API-Message <c>jbe_RecoverStaleChunks</c> (FB-46 / OE-12).
/// Stale-"Läuft"-Chunk-Recovery: setzt Chunks, die länger als <c>jbe_stale_chunk_seconds</c> in
/// "Läuft" stehen (provabel tot nach dem 120-s-Hard-Timeout), auf "Fortsetzen" zurück -> der Worker
/// resumed ab <c>jbe_group_cursor</c>. Loop-Breaker über <c>jbe_recoverycount</c>/<c>jbe_max_recoveries</c>.
///
/// Getaktet von einem Power-Automate-Recurrence-Flow, der die Custom API aufruft (ADR-0009
/// Takt-Option b, hier für Recovery). Die gesamte testbare Logik liegt in
/// <see cref="StaleChunkRecoveryService"/> (Core, mit Fake-Service unit-getestet); dieses Plugin ist
/// nur die dünne Glue.
///
/// <b>Warum async:</b> Service + RunCompletionService nutzen <c>try/catch</c> um OC-Updates
/// (IfRowVersionMatches). In einem SYNC-Plugin/sync-Custom-API triggert das den Sandbox-Wächter
/// (0x80040265, ADR-0005/FB-31); Async-Plugins sind davon nicht betroffen. Darum läuft die Recovery
/// als Async-Step (Custom API AllowedCustomProcessingStepType=AsyncOnly, kein Main-Plugin).
///
/// Registrierung:
///   Message:             jbe_RecoverStaleChunks (Custom API, Unbound/Global)
///   Step:                PostOperation, Async (mode=1)
///   "Delete AsyncOperation if StatusCode = Successful": an
/// </summary>
public sealed class RecoverStaleChunks : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var trace = (ITracingService)serviceProvider
            .GetService(typeof(ITracingService));
        var factory = (IOrganizationServiceFactory)serviceProvider
            .GetService(typeof(IOrganizationServiceFactory));
        // SYSTEM-Kontext (null): Zugriff auf alle Läufe/Chunks unabhängig von der Rolle des Auslösers.
        var service = factory.CreateOrganizationService(null);

        var staleSeconds = WorkerEnvironment.ReadInt(
            service, WorkerSchema.EnvStaleChunkSeconds, WorkerSchema.DefaultStaleChunkSeconds);
        var maxRecoveries = WorkerEnvironment.ReadInt(
            service, WorkerSchema.EnvMaxRecoveries, WorkerSchema.DefaultMaxRecoveries);

        try
        {
            var result = new StaleChunkRecoveryService(service, msg => trace.Trace(msg))
                .Sweep(staleSeconds, maxRecoveries);
            trace.Trace("RecoverStaleChunks: {0}", result);
        }
        catch (Exception ex)
        {
            // Async-Best-Practice (F-04): Fehler dokumentieren, NICHT werfen (kein Record-Ziel zum Markieren).
            trace.Trace("RecoverStaleChunks Fehler: {0}", ex);
        }
    }
}
