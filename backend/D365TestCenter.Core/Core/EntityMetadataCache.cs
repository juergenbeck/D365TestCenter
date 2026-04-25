using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace D365TestCenter.Core;

/// <summary>
/// Lazy-loading cache for Dataverse entity attribute metadata.
/// Automatically determines field types (OptionSet, Lookup, DateTime, Money)
/// so that ApplyFields can wrap values correctly without hardcoded lists.
/// Falls back gracefully if metadata retrieval fails.
/// </summary>
public sealed class EntityMetadataCache
{
    private readonly IOrganizationService _service;
    private readonly Dictionary<string, EntityMetadataInfo> _cache
        = new Dictionary<string, EntityMetadataInfo>(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _log;

    public EntityMetadataCache(IOrganizationService service, Action<string>? log = null)
    {
        _service = service;
        _log = log;
    }

    /// <summary>
    /// Test-Helper: erzeugt einen Cache mit vorgegebenen Attribute-Typen, ohne
    /// echte Metadata-Abfrage. Damit lassen sich type-aware-Pfade unit-testen
    /// ohne FakeOrgService mit RetrieveEntityRequest-Implementierung.
    /// Nicht fuer Produktiv-Code gedacht — Naming-Suffix "ForTesting" macht das
    /// explizit.
    /// </summary>
    public static EntityMetadataCache CreateForTesting(
        Dictionary<string, Dictionary<string, AttributeTypeCode>> seed)
    {
        var cache = new EntityMetadataCache(new NullOrganizationService());
        foreach (var entityKvp in seed)
        {
            var info = new EntityMetadataInfo();
            foreach (var attrKvp in entityKvp.Value)
            {
                info.AttributeTypes[attrKvp.Key] = attrKvp.Value;
            }
            cache._cache[entityKvp.Key] = info;
        }
        return cache;
    }

    private sealed class NullOrganizationService : IOrganizationService
    {
        public Guid Create(Entity entity) => throw new NotImplementedException();
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => throw new NotImplementedException();
        public void Update(Entity entity) => throw new NotImplementedException();
        public void Delete(string entityName, Guid id) => throw new NotImplementedException();
        public OrganizationResponse Execute(OrganizationRequest request) => throw new NotImplementedException();
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotImplementedException();
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotImplementedException();
        public EntityCollection RetrieveMultiple(QueryBase query) => throw new NotImplementedException();
    }

    /// <summary>Loads metadata for an entity (cached per instance lifetime).</summary>
    public EntityMetadataInfo? GetMetadata(string entityLogicalName)
    {
        if (_cache.TryGetValue(entityLogicalName, out var cached))
            return cached;

        try
        {
            var request = new RetrieveEntityRequest
            {
                LogicalName = entityLogicalName,
                EntityFilters = EntityFilters.Attributes
            };

            var response = (RetrieveEntityResponse)_service.Execute(request);
            var info = EntityMetadataInfo.FromMetadata(response.EntityMetadata);
            _cache[entityLogicalName] = info;
            _log?.Invoke($"MetadataCache: {entityLogicalName} geladen ({info.AttributeTypes.Count} Attribute)");
            return info;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"MetadataCache: Metadaten für '{entityLogicalName}' nicht ladbar: {ex.Message}");
            return null;
        }
    }

    /// <summary>Gets the attribute type for a specific field.</summary>
    public AttributeTypeCode? GetAttributeType(string entityLogicalName, string attributeName)
    {
        var info = GetMetadata(entityLogicalName);
        if (info != null && info.AttributeTypes.TryGetValue(attributeName, out var type))
            return type;
        return null;
    }

    /// <summary>Gets the target entity for a lookup field.</summary>
    public string? GetLookupTarget(string entityLogicalName, string attributeName)
    {
        var info = GetMetadata(entityLogicalName);
        if (info != null && info.LookupTargets.TryGetValue(attributeName, out var target))
            return target;
        return null;
    }

    /// <summary>Checks if a field is an OptionSet (Picklist, State, Status).</summary>
    public bool IsOptionSet(string entityLogicalName, string attributeName)
    {
        var type = GetAttributeType(entityLogicalName, attributeName);
        return type == AttributeTypeCode.Picklist
            || type == AttributeTypeCode.State
            || type == AttributeTypeCode.Status;
    }

    /// <summary>
    /// Resolves an EntitySetName (plural, Web API) to a LogicalName (singular, SDK).
    /// Examples: "accounts" -> "account", "invoicedetails" -> "invoicedetail".
    /// If the name is already a LogicalName, returns it unchanged.
    /// </summary>
    public string ResolveLogicalName(string entityNameFromJson)
    {
        // Try as-is first (might already be a LogicalName)
        if (_cache.ContainsKey(entityNameFromJson))
            return entityNameFromJson;

        // Known standard entity mappings (EntitySetName -> LogicalName)
        if (KnownEntitySetNames.TryGetValue(entityNameFromJson, out var logicalName))
            return logicalName;

        // Custom entities: EntitySetName is typically LogicalName + "es"
        if (entityNameFromJson.EndsWith("es", StringComparison.OrdinalIgnoreCase)
            && entityNameFromJson.Length > 2)
        {
            var candidate = entityNameFromJson.Substring(0, entityNameFromJson.Length - 2);
            // Verify it works by trying to load metadata
            if (GetMetadata(candidate) != null)
                return candidate;
        }

        // Fallback: strip trailing "s"
        if (entityNameFromJson.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            && entityNameFromJson.Length > 1)
        {
            var candidate = entityNameFromJson.Substring(0, entityNameFromJson.Length - 1);
            if (GetMetadata(candidate) != null)
                return candidate;
        }

        // Last resort: return as-is
        return entityNameFromJson;
    }

    private static readonly Dictionary<string, string> KnownEntitySetNames
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["accounts"] = "account",
        ["contacts"] = "contact",
        ["leads"] = "lead",
        ["tasks"] = "task",
        ["opportunities"] = "opportunity",
        ["invoices"] = "invoice",
        ["invoicedetails"] = "invoicedetail",
        ["quotes"] = "quote",
        ["quotedetails"] = "quotedetail",
        ["salesorders"] = "salesorder",
        ["salesorderdetails"] = "salesorderdetail",
        ["incidents"] = "incident",
        ["phonecalls"] = "phonecall",
        ["emails"] = "email",
        ["appointments"] = "appointment",
        ["annotations"] = "annotation",
    };

    /// <summary>Checks if a field is a Lookup (Lookup, Customer, Owner).</summary>
    public bool IsLookup(string entityLogicalName, string attributeName)
    {
        var type = GetAttributeType(entityLogicalName, attributeName);
        return type == AttributeTypeCode.Lookup
            || type == AttributeTypeCode.Customer
            || type == AttributeTypeCode.Owner;
    }
}

/// <summary>Cached metadata for a single entity.</summary>
public sealed class EntityMetadataInfo
{
    /// <summary>attributeName -> AttributeTypeCode</summary>
    public Dictionary<string, AttributeTypeCode> AttributeTypes { get; set; }
        = new Dictionary<string, AttributeTypeCode>(StringComparer.OrdinalIgnoreCase);

    /// <summary>attributeName -> targetEntityLogicalName (for Lookups only)</summary>
    public Dictionary<string, string> LookupTargets { get; set; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static EntityMetadataInfo FromMetadata(EntityMetadata entityMetadata)
    {
        var info = new EntityMetadataInfo();

        if (entityMetadata.Attributes == null)
            return info;

        foreach (var attr in entityMetadata.Attributes)
        {
            if (attr.AttributeType.HasValue && !string.IsNullOrEmpty(attr.LogicalName))
            {
                info.AttributeTypes[attr.LogicalName] = attr.AttributeType.Value;
            }

            // For lookup fields, store the first target entity
            if (attr is LookupAttributeMetadata lookupAttr
                && lookupAttr.Targets != null
                && lookupAttr.Targets.Length > 0)
            {
                info.LookupTargets[lookupAttr.LogicalName] = lookupAttr.Targets[0];
            }
        }

        return info;
    }
}
