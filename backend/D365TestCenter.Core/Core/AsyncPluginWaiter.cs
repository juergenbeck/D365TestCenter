using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace itt.IntegrationTests.Core;

/// <summary>
/// Wartet auf asynchrone Plugin-Verarbeitung durch Polling auf
/// markant_fg_logging (Governance) oder markant_bridge_pf_record (Bridge).
/// </summary>
public sealed class AsyncPluginWaiter
{
    private const int PollingIntervalMs = 2000;

    private const string CdhLogging = "markant_fg_logging";
    private const string CdhLogContactLookup = "markant_contactid";
    private const string CdhLogContactSourceLookup = "markant_fg_contactsourceid";
    private const string CdhLogCreatedOn = "createdon";
    private const string CdhLogName = "markant_name";
    private const string CdhLogDiagnostics = "markant_diagnostics_text";

    private const string PlatformBridge = "markant_bridge_pf_record";
    private const string BridgeStatus = "markant_bridgestatuscode";
    private const int BridgeStatusProcessed = 595300002;

    /// <summary>
    /// Pollt auf markant_fg_logging-Einträge für einen Contact nach dem angegebenen Zeitpunkt.
    /// Gibt true zurück sobald mindestens 1 relevanter Eintrag erscheint, false bei Timeout.
    /// </summary>
    public bool WaitForGovernanceCompletion(
        IOrganizationService service,
        Guid contactId,
        DateTime since,
        int timeoutSeconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var filterSince = since.AddSeconds(-2);

        Thread.Sleep(Math.Min(PollingIntervalMs, 1500));

        while (DateTime.UtcNow < deadline)
        {
            var query = new QueryExpression(CdhLogging)
            {
                ColumnSet = new ColumnSet(
                    CdhLogName, CdhLogDiagnostics,
                    CdhLogContactLookup, CdhLogContactSourceLookup,
                    CdhLogCreatedOn),
                TopCount = 5
            };
            query.Criteria.AddCondition(CdhLogContactLookup, ConditionOperator.Equal, contactId);
            query.Criteria.AddCondition(CdhLogCreatedOn, ConditionOperator.GreaterEqual, filterSince);
            query.Orders.Add(new OrderExpression(CdhLogCreatedOn, OrderType.Descending));

            var results = service.RetrieveMultiple(query);
            if (results.Entities.Count > 0)
                return true;

            Thread.Sleep(PollingIntervalMs);
        }

        return false;
    }

    /// <summary>
    /// Pollt auf markant_fg_logging für eine bestimmte ContactSource nach dem angegebenen Zeitpunkt.
    /// Filtert zusätzlich auf die ContactSourceId, um CDH-Logs von vorherigen Governance-Läufen
    /// auszuschließen (z.B. wenn mehrere Sources kurz nacheinander erstellt werden).
    /// </summary>
    public bool WaitForGovernanceCompletionBySource(
        IOrganizationService service,
        Guid contactId,
        Guid contactSourceId,
        DateTime since,
        int timeoutSeconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var filterSince = since.AddSeconds(-2);

        Thread.Sleep(Math.Min(PollingIntervalMs, 1500));

        while (DateTime.UtcNow < deadline)
        {
            var query = new QueryExpression(CdhLogging)
            {
                ColumnSet = new ColumnSet(
                    CdhLogName, CdhLogDiagnostics,
                    CdhLogContactLookup, CdhLogContactSourceLookup,
                    CdhLogCreatedOn),
                TopCount = 5
            };
            query.Criteria.AddCondition(CdhLogContactLookup, ConditionOperator.Equal, contactId);
            query.Criteria.AddCondition(CdhLogContactSourceLookup, ConditionOperator.Equal, contactSourceId);
            query.Criteria.AddCondition(CdhLogCreatedOn, ConditionOperator.GreaterEqual, filterSince);
            query.Orders.Add(new OrderExpression(CdhLogCreatedOn, OrderType.Descending));

            var results = service.RetrieveMultiple(query);
            if (results.Entities.Count > 0)
                return true;

            Thread.Sleep(PollingIntervalMs);
        }

        return false;
    }

    /// <summary>
    /// Pollt auf markant_bridge_pf_record.markant_bridgestatuscode für einen Bridge-Record.
    /// Gibt true zurück wenn der Status auf "Processed" (595300002) wechselt, false bei Timeout.
    /// </summary>
    public bool WaitForBridgeProcessing(
        IOrganizationService service,
        Guid bridgeRecordId,
        int timeoutSeconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        Thread.Sleep(Math.Min(PollingIntervalMs, 1500));

        while (DateTime.UtcNow < deadline)
        {
            var record = service.Retrieve(
                PlatformBridge, bridgeRecordId,
                new ColumnSet(BridgeStatus));

            var status = record.GetAttributeValue<OptionSetValue>(BridgeStatus);
            if (status?.Value == BridgeStatusProcessed)
                return true;

            Thread.Sleep(PollingIntervalMs);
        }

        return false;
    }

    /// <summary>
    /// Lädt CDH-Log-Einträge für einen Contact nach einem bestimmten Zeitpunkt.
    /// </summary>
    public List<Entity> ReadCdhLogs(
        IOrganizationService service,
        Guid contactId,
        DateTime afterUtc,
        int maxRecords = 50)
    {
        var query = new QueryExpression(CdhLogging)
        {
            ColumnSet = new ColumnSet(
                CdhLogName, CdhLogDiagnostics,
                CdhLogContactLookup, CdhLogContactSourceLookup,
                CdhLogCreatedOn),
            TopCount = maxRecords
        };
        query.Criteria.AddCondition(CdhLogContactLookup, ConditionOperator.Equal, contactId);
        query.Criteria.AddCondition(CdhLogCreatedOn, ConditionOperator.GreaterEqual, afterUtc);
        query.Orders.Add(new OrderExpression(CdhLogCreatedOn, OrderType.Descending));

        return service.RetrieveMultiple(query).Entities.ToList();
    }
}
