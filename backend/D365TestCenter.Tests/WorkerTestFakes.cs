using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace D365TestCenter.Tests;

/// <summary>
/// In-Memory-Fake eines IOrganizationService fuer die ADR-0009 Worker-Tests
/// (Koordinator/Worker/Result-Writer). Unterstuetzt genau die Operationen, die die
/// testbaren Orchestratoren brauchen:
///   - Create / Update / Delete / Retrieve
///   - RetrieveMultiple (QueryExpression mit Equal/In-Conditions, Lookup-Vergleich per Id)
///   - Execute(UpsertRequest) mit KeyAttributeCollection (Alternate-Key-Upsert, H1/H3)
///   - Execute(UpdateRequest) mit ConcurrencyBehavior.IfRowVersionMatches (OC-Claim, A-02/C-03)
///
/// RowVersion wird pro Record als monoton steigender Zaehler simuliert und bei jedem
/// Update/Upsert inkrementiert; ein IfRowVersionMatches-Update mit veralteter RowVersion
/// wirft eine FaultException&lt;OrganizationServiceFault&gt; (ConcurrencyVersionMismatch),
/// genau wie die Plattform -- so ist der Doppel-Fire-Verlierer deterministisch testbar.
/// </summary>
public sealed class FakeDataverse : IOrganizationService
{
    private sealed class Row
    {
        public Entity Entity = null!;
        public long Version;
    }

    // key: "logicalname:guid"
    private readonly Dictionary<string, Row> _store = new();
    private readonly List<(string Entity, Guid Id)> _createLog = new();
    private readonly List<(string Entity, Guid Id)> _deleteLog = new();

    /// <summary>Optionaler Hook: wird bei jedem Create aufgerufen (z.B. um einen Watchdog-Clock voranzutreiben).</summary>
    public Action<Entity>? OnCreate { get; set; }

    /// <summary>
    /// Optionaler Hook: wird am ENDE jedes Retrieve aufgerufen (logicalName, id), nachdem die
    /// zurueckgegebene Kopie samt RowVersion erstellt wurde. Erlaubt im Test, einen konkurrierenden
    /// Fire zu simulieren (z.B. die RowVersion hochzudrehen), um den OC-Claim-Verlierer zu testen.
    /// </summary>
    public Action<string, Guid>? OnRetrieve { get; set; }

    private static string Key(string logical, Guid id) => logical + ":" + id;

    // ── Seeding / Inspection ─────────────────────────────────────

    public Entity Seed(Entity e)
    {
        if (e.Id == Guid.Empty) e.Id = Guid.NewGuid();
        var keyField = e.LogicalName + "id";
        if (!e.Contains(keyField)) e[keyField] = e.Id;
        _store[Key(e.LogicalName, e.Id)] = new Row { Entity = Clone(e), Version = 1 };
        return e;
    }

    public Entity Get(string logical, Guid id) => Clone(_store[Key(logical, id)].Entity);
    public bool Exists(string logical, Guid id) => _store.ContainsKey(Key(logical, id));
    public long VersionOf(string logical, Guid id) => _store[Key(logical, id)].Version;
    public string RowVersionOf(string logical, Guid id) => VersionOf(logical, id).ToString();

    public List<Entity> All(string logical) =>
        _store.Values.Where(r => r.Entity.LogicalName == logical).Select(r => Clone(r.Entity)).ToList();

    public int CountCreated(string logical) => _createLog.Count(x => x.Entity == logical);
    public int CountDeleted(string logical) => _deleteLog.Count(x => x.Entity == logical);
    public IReadOnlyList<(string Entity, Guid Id)> CreateLog => _createLog;

    // ── IOrganizationService ─────────────────────────────────────

    public Guid Create(Entity entity)
    {
        var id = entity.Id == Guid.Empty ? Guid.NewGuid() : entity.Id;
        entity.Id = id;
        var keyField = entity.LogicalName + "id";
        var stored = Clone(entity);
        if (!stored.Contains(keyField)) stored[keyField] = id;
        _store[Key(entity.LogicalName, id)] = new Row { Entity = stored, Version = 1 };
        _createLog.Add((entity.LogicalName, id));
        OnCreate?.Invoke(Clone(stored));
        return id;
    }

    public void Update(Entity entity)
    {
        ApplyUpdate(entity, enforceVersion: false);
    }

    public void Delete(string entityName, Guid id)
    {
        _store.Remove(Key(entityName, id));
        _deleteLog.Add((entityName, id));
    }

    public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet)
    {
        if (!_store.TryGetValue(Key(entityName, id), out var row))
            throw MakeFault(-2147220969, $"{entityName} With Id = {id} Does Not Exist");
        var projected = ProjectColumns(row, columnSet);
        OnRetrieve?.Invoke(entityName, id);
        return projected;
    }

    public EntityCollection RetrieveMultiple(QueryBase query)
    {
        if (query is not QueryExpression qe)
            throw new NotImplementedException("FakeDataverse.RetrieveMultiple: nur QueryExpression unterstuetzt");

        IEnumerable<Row> rows = _store.Values.Where(r => r.Entity.LogicalName == qe.EntityName);
        rows = rows.Where(r => MatchesFilter(r.Entity, qe.Criteria));

        var list = rows.Select(r => ProjectColumns(r, qe.ColumnSet)).ToList();
        return new EntityCollection(list);
    }

    public OrganizationResponse Execute(OrganizationRequest request)
    {
        switch (request)
        {
            case UpsertRequest upsert:
                return DoUpsert(upsert);
            case UpdateRequest upd when upd.ConcurrencyBehavior == ConcurrencyBehavior.IfRowVersionMatches:
                ApplyUpdate(upd.Target, enforceVersion: true);
                return new UpdateResponse();
            case UpdateRequest upd:
                ApplyUpdate(upd.Target, enforceVersion: false);
                return new UpdateResponse();
            case CreateRequest cr:
                var newId = Create(cr.Target);
                var cResp = new CreateResponse();
                cResp.Results["id"] = newId;
                return cResp;
            case RetrieveMultipleRequest rm:
                var rmResp = new RetrieveMultipleResponse();
                rmResp.Results["EntityCollection"] = RetrieveMultiple(rm.Query);
                return rmResp;
            default:
                throw new NotImplementedException($"FakeDataverse.Execute: {request.RequestName ?? request.GetType().Name}");
        }
    }

    // ── intern ───────────────────────────────────────────────────

    private UpsertResponse DoUpsert(UpsertRequest upsert)
    {
        var target = upsert.Target;
        Row? existing = null;

        if (target.KeyAttributes != null && target.KeyAttributes.Count > 0)
        {
            existing = _store.Values.FirstOrDefault(r =>
                r.Entity.LogicalName == target.LogicalName &&
                target.KeyAttributes.All(ka => AttrEquals(r.Entity, ka.Key, ka.Value)));
        }
        else if (target.Id != Guid.Empty && _store.TryGetValue(Key(target.LogicalName, target.Id), out var byId))
        {
            existing = byId;
        }

        bool created;
        Guid id;
        if (existing != null)
        {
            id = existing.Entity.Id;
            foreach (var kv in target.Attributes) existing.Entity[kv.Key] = kv.Value;
            existing.Version++;
            created = false;
        }
        else
        {
            var e = Clone(target);
            e.KeyAttributes.Clear();
            id = e.Id == Guid.Empty ? Guid.NewGuid() : e.Id;
            e.Id = id;
            var keyField = e.LogicalName + "id";
            if (!e.Contains(keyField)) e[keyField] = id;
            // Alternate-Key-Attribute als echte Attribute materialisieren (damit spaetere
            // Lookups per Equal-Condition sie finden).
            if (target.KeyAttributes != null)
                foreach (var ka in target.KeyAttributes)
                    if (!e.Contains(ka.Key)) e[ka.Key] = ka.Value;
            _store[Key(e.LogicalName, id)] = new Row { Entity = e, Version = 1 };
            _createLog.Add((e.LogicalName, id));
            created = true;
        }

        var resp = new UpsertResponse();
        resp.Results["Target"] = new EntityReference(target.LogicalName, id);
        resp.Results["RecordCreated"] = created;
        return resp;
    }

    private void ApplyUpdate(Entity entity, bool enforceVersion)
    {
        if (!_store.TryGetValue(Key(entity.LogicalName, entity.Id), out var row))
            throw MakeFault(-2147220969, $"{entity.LogicalName} With Id = {entity.Id} Does Not Exist");

        if (enforceVersion)
        {
            var expected = entity.RowVersion;
            if (expected == null || expected != row.Version.ToString())
                throw MakeFault(-2147088253, // ConcurrencyVersionMismatch (0x80060003)
                    $"OptimisticConcurrency: RowVersion mismatch on {entity.LogicalName} {entity.Id}");
        }

        foreach (var kv in entity.Attributes) row.Entity[kv.Key] = kv.Value;
        row.Version++;
    }

    private static bool MatchesFilter(Entity e, FilterExpression filter)
    {
        if (filter == null) return true;
        var conds = filter.Conditions.Select(c => ConditionMatches(e, c));
        var subs = filter.Filters.Select(f => MatchesFilter(e, f));
        var all = conds.Concat(subs);
        return filter.FilterOperator == LogicalOperator.Or ? (all.Any() ? all.Any(x => x) : true) : all.All(x => x);
    }

    private static bool ConditionMatches(Entity e, ConditionExpression cond)
    {
        if (!e.Contains(cond.AttributeName))
            return cond.Operator == ConditionOperator.Null;

        var actual = e[cond.AttributeName];
        switch (cond.Operator)
        {
            case ConditionOperator.Equal:
                return ValueEquals(actual, cond.Values.FirstOrDefault());
            case ConditionOperator.NotEqual:
                return !ValueEquals(actual, cond.Values.FirstOrDefault());
            case ConditionOperator.In:
                return cond.Values.Any(v => ValueEquals(actual, v));
            case ConditionOperator.NotNull:
                return actual != null;
            case ConditionOperator.Null:
                return actual == null;
            default:
                throw new NotImplementedException($"FakeDataverse: ConditionOperator {cond.Operator} nicht unterstuetzt");
        }
    }

    private static bool AttrEquals(Entity e, string attr, object? value)
        => e.Contains(attr) && ValueEquals(e[attr], value);

    private static bool ValueEquals(object? actual, object? expected)
    {
        if (actual is EntityReference er)
        {
            if (expected is EntityReference er2) return er.Id == er2.Id;
            if (expected is Guid g) return er.Id == g;
            if (expected is string s && Guid.TryParse(s, out var gs)) return er.Id == gs;
            return false;
        }
        if (actual is OptionSetValue osv)
        {
            if (expected is OptionSetValue osv2) return osv.Value == osv2.Value;
            if (expected is int i) return osv.Value == i;
            return false;
        }
        if (actual is bool ab && expected is bool eb) return ab == eb;
        return Equals(actual, expected);
    }

    private static Entity ProjectColumns(Row row, ColumnSet columns)
    {
        // Vereinfachung: immer alle Attribute zurueckgeben (Tests lesen gezielt).
        var clone = Clone(row.Entity);
        clone.RowVersion = row.Version.ToString();
        return clone;
    }

    private static Entity Clone(Entity e)
    {
        var c = new Entity(e.LogicalName) { Id = e.Id };
        foreach (var kv in e.Attributes) c[kv.Key] = kv.Value;
        if (e.KeyAttributes != null)
            foreach (var ka in e.KeyAttributes) c.KeyAttributes[ka.Key] = ka.Value;
        c.RowVersion = e.RowVersion;
        return c;
    }

    private static FaultException<OrganizationServiceFault> MakeFault(int code, string msg)
        => new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault { ErrorCode = code, Message = msg }, new FaultReason(msg));

    // ── ungenutzt ────────────────────────────────────────────────
    public void Associate(string en, Guid id, Relationship rel, EntityReferenceCollection rc) => throw new NotImplementedException();
    public void Disassociate(string en, Guid id, Relationship rel, EntityReferenceCollection rc) => throw new NotImplementedException();
}
