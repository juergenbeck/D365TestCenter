using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using D365TestCenter.Core.Config;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace D365TestCenter.Cli;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("D365 Test Center CLI - Integration testing for Dynamics 365");

        // Shared options
        var orgOption = new Option<string>("--org", "Dataverse organization URL") { IsRequired = true };
        var clientIdOption = new Option<string>("--client-id", "Azure AD App Client ID");
        var clientSecretOption = new Option<string>("--client-secret", "Azure AD App Client Secret");
        var tenantIdOption = new Option<string>("--tenant-id", "Azure AD Tenant ID");
        var tokenOption = new Option<string>("--token", "Bearer token (alternative to client credentials)");
        var interactiveOption = new Option<bool>("--interactive", () => false, "Use interactive browser login");

        // ── run command ──────────────────────────────────────────
        var runCommand = new Command("run", "Execute test cases against a Dataverse environment");
        runCommand.AddOption(orgOption);
        runCommand.AddOption(clientIdOption);
        runCommand.AddOption(clientSecretOption);
        runCommand.AddOption(tenantIdOption);
        runCommand.AddOption(tokenOption);
        runCommand.AddOption(interactiveOption);
        runCommand.AddOption(new Option<string>("--filter", () => "*", "Test case filter (wildcard on test ID)"));
        runCommand.AddOption(new Option<bool>("--keep-records", () => false, "Keep test data after run"));
        runCommand.AddOption(new Option<string>("--config", () => "standard", "Config profile: standard, markant"));
        runCommand.Handler = CommandHandler.Create<string, string?, string?, string?, string?, bool, string, bool, string>(RunTests);
        rootCommand.AddCommand(runCommand);

        // ── status command ───────────────────────────────────────
        var statusCommand = new Command("status", "Check status of recent test runs");
        statusCommand.AddOption(orgOption);
        statusCommand.AddOption(clientIdOption);
        statusCommand.AddOption(clientSecretOption);
        statusCommand.AddOption(tenantIdOption);
        statusCommand.AddOption(tokenOption);
        statusCommand.AddOption(interactiveOption);
        statusCommand.AddOption(new Option<int>("--top", () => 5, "Number of recent runs to show"));
        statusCommand.Handler = CommandHandler.Create<string, string?, string?, string?, string?, bool, int>(ShowStatus);
        rootCommand.AddCommand(statusCommand);

        return await rootCommand.InvokeAsync(args);
    }

    static ServiceClient Connect(string org, string? clientId, string? clientSecret, string? tenantId, string? token, bool interactive)
    {
        if (!string.IsNullOrEmpty(token))
        {
            var client = new ServiceClient(new Uri(org), _ => Task.FromResult(token));
            if (!client.IsReady) throw new Exception($"Connection failed: {client.LastError}");
            return client;
        }

        if (interactive)
        {
            var connStr = $"AuthType=OAuth;Url={org};LoginPrompt=Auto;RequireNewInstance=True";
            var client = new ServiceClient(connStr);
            if (!client.IsReady) throw new Exception($"Connection failed: {client.LastError}");
            return client;
        }

        if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
        {
            var connStr = $"AuthType=ClientSecret;Url={org};ClientId={clientId};ClientSecret={clientSecret}";
            if (!string.IsNullOrEmpty(tenantId)) connStr += $";Authority=https://login.microsoftonline.com/{tenantId}";
            var client = new ServiceClient(connStr);
            if (!client.IsReady) throw new Exception($"Connection failed: {client.LastError}");
            return client;
        }

        throw new Exception("No auth provided. Use --client-id/--client-secret, --token, or --interactive.");
    }

    static ITestCenterConfig GetConfig(string name) => name.ToLower() switch
    {
        "markant" => new MarkantConfig(),
        _ => new StandardCrmConfig()
    };

    static async Task<int> RunTests(string org, string? clientId, string? clientSecret, string? tenantId,
        string? token, bool interactive, string filter, bool keepRecords, string config)
    {
        Console.WriteLine("============================================================");
        Console.WriteLine("  D365 Test Center CLI");
        Console.WriteLine($"  Org: {org}");
        Console.WriteLine($"  Filter: {filter}  |  Config: {config}");
        Console.WriteLine("============================================================\n");

        try
        {
            using var client = Connect(org, clientId, clientSecret, tenantId, token, interactive);
            var cfg = GetConfig(config);
            Console.WriteLine($"  Connected.\n");

            // Load test cases
            var q = new QueryExpression(cfg.TestCaseEntity)
            {
                ColumnSet = new ColumnSet("jbe_testid", "jbe_title", "jbe_definitionjson", "jbe_enabled", "jbe_tags"),
                Criteria = { Conditions = { new ConditionExpression("jbe_enabled", ConditionOperator.Equal, true) } },
                Orders = { new OrderExpression("jbe_testid", OrderType.Ascending) },
                TopCount = 500
            };
            var cases = client.RetrieveMultiple(q).Entities.ToList();

            if (filter != "*")
            {
                var p = filter.Replace("*", "").ToLower();
                cases = cases.Where(c => {
                    var id = c.GetAttributeValue<string>("jbe_testid") ?? "";
                    return filter.Contains("*") ? id.ToLower().Contains(p) : id.Equals(filter, StringComparison.OrdinalIgnoreCase);
                }).ToList();
            }

            Console.WriteLine($"  {cases.Count} test cases loaded.\n");

            // Create run
            var runId = client.Create(new Entity(cfg.TestRunEntity)
            {
                ["jbe_teststatus"] = new OptionSetValue(105710001),
                ["jbe_total"] = cases.Count,
                ["jbe_passed"] = 0, ["jbe_failed"] = 0,
                ["jbe_keeprecords"] = keepRecords,
                ["jbe_startedon"] = DateTime.UtcNow
            });

            int passed = 0, failed = 0;
            var tracked = new List<(string e, Guid id)>();

            foreach (var tc in cases)
            {
                var testId = tc.GetAttributeValue<string>("jbe_testid") ?? "?";
                var title = tc.GetAttributeValue<string>("jbe_title") ?? "";
                Console.Write($"  {testId} {title}");

                try
                {
                    var def = JObject.Parse(tc.GetAttributeValue<string>("jbe_definitionjson") ?? "{}");
                    var aliases = new Dictionary<string, (Guid id, string entity)>();
                    Guid? contactId = null;

                    // Preconditions
                    foreach (var p in def["preconditions"]?.ToObject<List<JObject>>() ?? new())
                    {
                        var ent = p["entity"]?.ToString() ?? "";
                        var alias = p["alias"]?.ToString() ?? "";
                        var rec = new Entity(ent);
                        foreach (var kv in p["fields"]?.ToObject<Dictionary<string, object>>() ?? new())
                        {
                            var val = ResolveValue(kv.Value?.ToString() ?? "", aliases);
                            if (kv.Key.Contains("@odata.bind"))
                                rec[kv.Key.Replace("@odata.bind", "")] = ParseEntityRef(val);
                            else if (kv.Value is long l) rec[kv.Key] = new OptionSetValue((int)l);
                            else if (kv.Value is int i) rec[kv.Key] = new OptionSetValue(i);
                            else rec[kv.Key] = val;
                        }
                        var id = client.Create(rec);
                        tracked.Add((ent, id));
                        if (alias != "") aliases[alias] = (id, ent);
                        if (ent == "contacts" && contactId == null) contactId = id;
                    }

                    // Steps
                    foreach (var s in def["steps"]?.ToObject<List<JObject>>() ?? new())
                    {
                        switch (s["action"]?.ToString())
                        {
                            case "UpdateRecord":
                                var ua = s["alias"]?.ToString() ?? "";
                                if (aliases.TryGetValue(ua, out var ur))
                                {
                                    var ue = new Entity(ur.entity, ur.id);
                                    foreach (var kv in s["fields"]?.ToObject<Dictionary<string, object>>() ?? new())
                                    {
                                        if (kv.Value is long l) ue[kv.Key] = new OptionSetValue((int)l);
                                        else if (kv.Value is int i) ue[kv.Key] = new OptionSetValue(i);
                                        else ue[kv.Key] = kv.Value?.ToString();
                                    }
                                    client.Update(ue);
                                }
                                break;
                            case "Wait":
                                await Task.Delay((s["waitSeconds"]?.Value<int>() ?? 5) * 1000);
                                break;
                            case "DeleteRecord":
                                var da = s["alias"]?.ToString() ?? "";
                                if (aliases.TryGetValue(da, out var dr)) client.Delete(dr.entity, dr.id);
                                break;
                        }
                    }

                    await Task.Delay(3000); // Wait for async plugins

                    // Assertions
                    bool allOk = true;
                    var fails = new List<string>();
                    foreach (var a in def["assertions"]?.ToObject<List<JObject>>() ?? new())
                    {
                        var target = a["target"]?.ToString() ?? "Query";
                        var field = a["field"]?.ToString() ?? "";
                        var op = a["operator"]?.ToString() ?? "Equals";
                        var expected = ResolveValue(a["value"]?.ToString() ?? "", aliases);
                        string? actual = null;

                        try
                        {
                            if (target == "Contact" && contactId.HasValue)
                            {
                                var c = client.Retrieve("contacts", contactId.Value, new ColumnSet(field));
                                actual = c.Contains(field) ? FormatValue(c[field]) : null;
                            }
                            else if (target == "Query")
                            {
                                var qe = a["entity"]?.ToString() ?? "";
                                var filt = a["filter"]?.ToObject<Dictionary<string, string>>() ?? new();
                                var qq = new QueryExpression(qe) { TopCount = 1, ColumnSet = new ColumnSet(field) };
                                foreach (var f in filt)
                                {
                                    var fv = ResolveValue(f.Value, aliases);
                                    qq.Criteria.AddCondition(f.Key, ConditionOperator.Equal, Guid.TryParse(fv, out var g) ? (object)g : fv);
                                }
                                var res = client.RetrieveMultiple(qq);
                                if (res.Entities.Count > 0)
                                    actual = res.Entities[0].Contains(field) ? FormatValue(res.Entities[0][field]) : null;
                            }
                        }
                        catch (Exception ex) { actual = $"ERROR: {ex.Message}"; }

                        bool ok = op switch
                        {
                            "Equals" => $"{actual}" == $"{expected}",
                            "IsNull" => string.IsNullOrEmpty(actual),
                            "IsNotNull" => !string.IsNullOrEmpty(actual),
                            "Contains" => (actual ?? "").Contains(expected),
                            "Exists" => actual != null,
                            "NotExists" => actual == null,
                            _ => $"{actual}" == $"{expected}"
                        };
                        if (!ok) { allOk = false; fails.Add($"{field} {op}: expected \"{expected}\", actual \"{actual}\""); }
                    }

                    if (allOk) { passed++; Console.WriteLine(" ... PASSED"); }
                    else { failed++; Console.WriteLine(" ... FAILED"); foreach (var m in fails) Console.WriteLine($"      {m}"); }

                    client.Create(new Entity(cfg.TestRunResultEntity)
                    {
                        ["jbe_testid"] = testId,
                        ["jbe_outcome"] = new OptionSetValue(allOk ? 105710000 : 105710001),
                        ["jbe_errormessage"] = allOk ? null : string.Join("; ", fails),
                        ["jbe_testrunid"] = new EntityReference(cfg.TestRunEntity, runId)
                    });
                }
                catch (Exception ex)
                {
                    failed++; Console.WriteLine($" ... ERROR ({ex.Message})");
                    client.Create(new Entity(cfg.TestRunResultEntity)
                    {
                        ["jbe_testid"] = testId, ["jbe_outcome"] = new OptionSetValue(105710002),
                        ["jbe_errormessage"] = ex.Message,
                        ["jbe_testrunid"] = new EntityReference(cfg.TestRunEntity, runId)
                    });
                }
            }

            client.Update(new Entity(cfg.TestRunEntity, runId)
            {
                ["jbe_teststatus"] = new OptionSetValue(105710002),
                ["jbe_passed"] = passed, ["jbe_failed"] = failed,
                ["jbe_completedon"] = DateTime.UtcNow,
                ["jbe_testsummary"] = $"{cases.Count} tests. {passed} passed, {failed} failed."
            });

            if (!keepRecords && tracked.Count > 0)
            {
                Console.WriteLine($"\n  Cleaning up {tracked.Count} records...");
                tracked.Reverse();
                foreach (var (e, id) in tracked) try { client.Delete(e, id); } catch { }
            }

            Console.WriteLine($"\n  {passed} PASSED  |  {failed} FAILED  |  {cases.Count} TOTAL");
            return failed > 0 ? 1 : 0;
        }
        catch (Exception ex) { Console.WriteLine($"\n  Error: {ex.Message}"); return 2; }
    }

    static async Task<int> ShowStatus(string org, string? clientId, string? clientSecret, string? tenantId,
        string? token, bool interactive, int top)
    {
        try
        {
            using var client = Connect(org, clientId, clientSecret, tenantId, token, interactive);
            var cfg = new StandardCrmConfig();
            var q = new QueryExpression(cfg.TestRunEntity)
            {
                ColumnSet = new ColumnSet("jbe_teststatus", "jbe_passed", "jbe_failed", "jbe_total", "jbe_startedon", "jbe_testsummary"),
                Orders = { new OrderExpression("jbe_startedon", OrderType.Descending) },
                TopCount = top
            };
            foreach (var r in client.RetrieveMultiple(q).Entities)
            {
                var status = r.GetAttributeValue<OptionSetValue>("jbe_teststatus")?.Value switch
                { 105710000 => "Planned", 105710001 => "Running", 105710002 => "Done", 105710003 => "Error", _ => "?" };
                Console.WriteLine($"  {r.GetAttributeValue<DateTime?>("jbe_startedon"):yyyy-MM-dd HH:mm}  {status,-8}  P:{r.GetAttributeValue<int?>("jbe_passed") ?? 0}  F:{r.GetAttributeValue<int?>("jbe_failed") ?? 0}  T:{r.GetAttributeValue<int?>("jbe_total") ?? 0}");
            }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine($"  Error: {ex.Message}"); return 1; }
    }

    static string ResolveValue(string val, Dictionary<string, (Guid id, string entity)> aliases)
    {
        foreach (var a in aliases) val = val.Replace($"{{{a.Key}.id}}", a.Value.id.ToString());
        val = val.Replace("{TIMESTAMP}", DateTime.UtcNow.ToString("o"));
        val = val.Replace("{GENERATED:company}", $"JBE Test {Guid.NewGuid().ToString()[..8]}");
        val = val.Replace("{GENERATED:firstname}", $"JBE_FN_{Guid.NewGuid().ToString()[..6]}");
        val = val.Replace("{GENERATED:lastname}", $"JBE_LN_{Guid.NewGuid().ToString()[..6]}");
        val = val.Replace("{GENERATED:email}", $"jbe_{Guid.NewGuid().ToString()[..6]}@example.com");
        val = val.Replace("{GENERATED:phone}", $"555-{new Random().Next(10000, 99999)}");
        return val;
    }

    static EntityReference ParseEntityRef(string bind)
    {
        var m = System.Text.RegularExpressions.Regex.Match(bind, @"/(\w+)\(([0-9a-f-]+)\)");
        if (!m.Success) throw new Exception($"Cannot parse entity reference: {bind}");
        var entitySet = m.Groups[1].Value;
        var entity = entitySet.EndsWith("s") ? entitySet[..^1] : entitySet;
        return new EntityReference(entity, Guid.Parse(m.Groups[2].Value));
    }

    static string? FormatValue(object? val) => val switch
    {
        null => null,
        OptionSetValue osv => osv.Value.ToString(),
        EntityReference er => er.Id.ToString(),
        Money m => m.Value.ToString(),
        DateTime dt => dt.ToString("o"),
        _ => val.ToString()
    };
}
