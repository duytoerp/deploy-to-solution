using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeployToSolution.Models;

namespace DeployToSolution.Services
{
    public class ResolveResult
    {
        public string ObjectId { get; set; }
        public int ComponentType { get; set; } = -1;
        public RowState State { get; set; } = RowState.NotFound;
        public string Message { get; set; } = "";
        /// <summary>Tên thật của bản ghi khớp được (Table -> logical name). Null khi người dùng dán thẳng GUID.</summary>
        public string MatchedName { get; set; }
        public bool Ok => State == RowState.Resolved;
    }

    /// <summary>Turns a "Type + Name" CSV line into the (componenttype, objectid) pair AddSolutionComponent needs.</summary>
    public class ComponentCatalog
    {
        private class QueryDef
        {
            public string EntitySet;
            public string IdField;
            public string[] NameFields;
            public string FixedFilter;
            public string[] ExtraSelect = Array.Empty<string>();
            /// <summary>Field holding the owning table, used when the CSV name is written as "table|name".</summary>
            public string ParentField;
        }

        // Query-based resolvers, keyed by the normalized component type label.
        private static readonly Dictionary<string, QueryDef> Queries = new(StringComparer.Ordinal)
        {
            ["workflow"] = new QueryDef
            {
                EntitySet = "workflows", IdField = "workflowid",
                NameFields = new[] { "name", "uniquename" },
                // type 1 = definition; activations are separate rows that must never be added to a solution.
                FixedFilter = "type eq 1",
                ExtraSelect = new[] { "category", "primaryentity" }
            },
            ["savedquery"] = new QueryDef
            {
                EntitySet = "savedqueries", IdField = "savedqueryid",
                NameFields = new[] { "name" }, ParentField = "returnedtypecode",
                ExtraSelect = new[] { "returnedtypecode" }
            },
            ["systemform"] = new QueryDef
            {
                EntitySet = "systemforms", IdField = "formid",
                NameFields = new[] { "name" }, ParentField = "objecttypecode",
                ExtraSelect = new[] { "objecttypecode" }
            },
            ["savedqueryvisualization"] = new QueryDef
            {
                EntitySet = "savedqueryvisualizations", IdField = "savedqueryvisualizationid",
                NameFields = new[] { "name" }, ParentField = "primaryentitytypecode",
                ExtraSelect = new[] { "primaryentitytypecode" }
            },
            ["webresource"] = new QueryDef
            {
                EntitySet = "webresourceset", IdField = "webresourceid",
                NameFields = new[] { "name", "displayname" }
            },
            ["pluginassembly"] = new QueryDef
            {
                EntitySet = "pluginassemblies", IdField = "pluginassemblyid",
                NameFields = new[] { "name" }
            },
            ["plugintype"] = new QueryDef
            {
                EntitySet = "plugintypes", IdField = "plugintypeid",
                NameFields = new[] { "typename", "name", "friendlyname" }
            },
            ["sdkmessageprocessingstep"] = new QueryDef
            {
                EntitySet = "sdkmessageprocessingsteps", IdField = "sdkmessageprocessingstepid",
                NameFields = new[] { "name" },
                ExtraSelect = new[] { "stage", "mode" }
            },
            ["serviceendpoint"] = new QueryDef
            {
                EntitySet = "serviceendpoints", IdField = "serviceendpointid",
                NameFields = new[] { "name" }
            },
            ["canvasapp"] = new QueryDef
            {
                EntitySet = "canvasapps", IdField = "canvasappid",
                NameFields = new[] { "name", "displayname" }
            },
            ["appmodule"] = new QueryDef
            {
                EntitySet = "appmodules", IdField = "appmoduleid",
                NameFields = new[] { "uniquename", "name" }
            },
            ["role"] = new QueryDef
            {
                EntitySet = "roles", IdField = "roleid",
                NameFields = new[] { "name" },
                // Roles are copied into every business unit; only the root one belongs to a solution.
                FixedFilter = "_parentroleid_value eq null"
            },
            ["fieldsecurityprofile"] = new QueryDef
            {
                EntitySet = "fieldsecurityprofiles", IdField = "fieldsecurityprofileid",
                NameFields = new[] { "name" }
            },
            ["connectionreference"] = new QueryDef
            {
                EntitySet = "connectionreferences", IdField = "connectionreferenceid",
                NameFields = new[] { "connectionreferencelogicalname", "connectionreferencedisplayname" }
            },
            ["environmentvariabledefinition"] = new QueryDef
            {
                EntitySet = "environmentvariabledefinitions", IdField = "environmentvariabledefinitionid",
                NameFields = new[] { "schemaname", "displayname" }
            },
            ["environmentvariablevalue"] = new QueryDef
            {
                EntitySet = "environmentvariablevalues", IdField = "environmentvariablevalueid",
                NameFields = new[] { "schemaname" }
            },
            ["sitemap"] = new QueryDef
            {
                EntitySet = "sitemaps", IdField = "sitemapid",
                NameFields = new[] { "sitemapname", "sitemapnameunique" }
            },
            ["report"] = new QueryDef
            {
                EntitySet = "reports", IdField = "reportid",
                NameFields = new[] { "name" }
            },
            ["emailtemplate"] = new QueryDef
            {
                EntitySet = "templates", IdField = "templateid",
                NameFields = new[] { "title" }
            },
            ["duplicaterule"] = new QueryDef
            {
                EntitySet = "duplicaterules", IdField = "duplicateruleid",
                NameFields = new[] { "name" }
            },
            ["customcontrol"] = new QueryDef
            {
                EntitySet = "customcontrols", IdField = "customcontrolid",
                NameFields = new[] { "name" }
            },
            ["customapi"] = new QueryDef
            {
                EntitySet = "customapis", IdField = "customapiid",
                NameFields = new[] { "uniquename", "name" }
            },
            ["connectionrole"] = new QueryDef
            {
                EntitySet = "connectionroles", IdField = "connectionroleid",
                NameFields = new[] { "name" }
            },
            ["ribboncustomization"] = new QueryDef
            {
                EntitySet = "ribboncustomizations", IdField = "ribboncustomizationid",
                NameFields = new[] { "entity" }
            },
            ["sla"] = new QueryDef
            {
                EntitySet = "slas", IdField = "slaid",
                NameFields = new[] { "name" }
            }
        };

        // What the user is allowed to type in the Type column, mapped onto the canonical label key.
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
        {
            ["table"] = "entity",
            ["entity"] = "entity",
            ["column"] = "attribute",
            ["field"] = "attribute",
            ["attribute"] = "attribute",
            ["choice"] = "optionset",
            ["globalchoice"] = "optionset",
            ["globaloptionset"] = "optionset",
            ["picklist"] = "optionset",
            ["optionset"] = "optionset",
            ["view"] = "savedquery",
            ["savedquery"] = "savedquery",
            ["form"] = "systemform",
            ["systemform"] = "systemform",
            ["chart"] = "savedqueryvisualization",
            ["process"] = "workflow",
            ["flow"] = "workflow",
            ["cloudflow"] = "workflow",
            ["modernflow"] = "workflow",
            ["bpf"] = "workflow",
            ["businessprocessflow"] = "workflow",
            ["businessrule"] = "workflow",
            ["action"] = "workflow",
            ["wf"] = "workflow",
            ["workflow"] = "workflow",
            ["pluginstep"] = "sdkmessageprocessingstep",
            ["step"] = "sdkmessageprocessingstep",
            ["sdkstep"] = "sdkmessageprocessingstep",
            ["sdkmessageprocessingstep"] = "sdkmessageprocessingstep",
            ["assembly"] = "pluginassembly",
            ["plugin"] = "pluginassembly",
            ["pluginassembly"] = "pluginassembly",
            ["plugintype"] = "plugintype",
            ["webresource"] = "webresource",
            ["js"] = "webresource",
            ["html"] = "webresource",
            ["app"] = "appmodule",
            ["modeldrivenapp"] = "appmodule",
            ["appmodule"] = "appmodule",
            ["canvasapp"] = "canvasapp",
            ["powerapp"] = "canvasapp",
            ["securityrole"] = "role",
            ["role"] = "role",
            ["columnsecurityprofile"] = "fieldsecurityprofile",
            ["fieldsecurityprofile"] = "fieldsecurityprofile",
            ["connectionreference"] = "connectionreference",
            ["connref"] = "connectionreference",
            ["envvar"] = "environmentvariabledefinition",
            ["environmentvariable"] = "environmentvariabledefinition",
            ["environmentvariabledefinition"] = "environmentvariabledefinition",
            ["envvarvalue"] = "environmentvariablevalue",
            ["environmentvariablevalue"] = "environmentvariablevalue",
            ["sitemap"] = "sitemap",
            ["relationship"] = "entityrelationship",
            ["entityrelationship"] = "entityrelationship",
            ["key"] = "entitykey",
            ["alternatekey"] = "entitykey",
            ["entitykey"] = "entitykey",
            ["report"] = "report",
            ["emailtemplate"] = "emailtemplate",
            ["customapi"] = "customapi",
            ["pcf"] = "customcontrol",
            ["customcontrol"] = "customcontrol",
            ["serviceendpoint"] = "serviceendpoint",
            ["applicationribbon"] = "ribboncustomization",
            ["ribbon"] = "ribboncustomization",
            ["ribboncustomization"] = "ribboncustomization",
            ["duplicaterule"] = "duplicaterule",
            ["connectionrole"] = "connectionrole",
            ["sla"] = "sla"
        };

        // Used only when the environment option set cannot be read.
        private static readonly Dictionary<string, int> Fallback = new(StringComparer.Ordinal)
        {
            ["entity"] = 1,
            ["attribute"] = 2,
            ["relationship"] = 3,
            ["optionset"] = 9,
            ["entityrelationship"] = 10,
            ["entitykey"] = 14,
            ["role"] = 20,
            ["savedquery"] = 26,
            ["workflow"] = 29,
            ["report"] = 31,
            ["emailtemplate"] = 36,
            ["duplicaterule"] = 44,
            ["ribboncustomization"] = 50,
            ["savedqueryvisualization"] = 59,
            ["systemform"] = 60,
            ["webresource"] = 61,
            ["sitemap"] = 62,
            ["connectionrole"] = 63,
            ["fieldsecurityprofile"] = 70,
            ["appmodule"] = 80,
            ["plugintype"] = 90,
            ["pluginassembly"] = 91,
            ["sdkmessageprocessingstep"] = 92,
            ["serviceendpoint"] = 93,
            ["sla"] = 97,
            ["customcontrol"] = 161,
            ["canvasapp"] = 300,
            ["environmentvariabledefinition"] = 380,
            ["environmentvariablevalue"] = 381
        };

        private readonly DataverseClient _client;
        private readonly Dictionary<string, ComponentTypeDef> _byKey = new(StringComparer.Ordinal);

        private List<JsonElement> _entityCache;
        private List<JsonElement> _optionSetCache;
        private List<JsonElement> _relationshipCache;

        public ComponentCatalog(DataverseClient client) => _client = client;

        public List<ComponentTypeDef> Types { get; } = new List<ComponentTypeDef>();

        /// <summary>Component type code for a table, as this environment numbers it.</summary>
        public int EntityComponentType => _byKey.TryGetValue("entity", out var d) ? d.Value : 1;

        /// <summary>Every string the Type column accepts, ready for the editor dropdown.</summary>
        public List<string> TypeNames { get; } = new List<string>();

        public async Task LoadAsync(CancellationToken ct)
        {
            Types.Clear();
            _byKey.Clear();

            try
            {
                var el = await _client.GetAsync(
                    "EntityDefinitions(LogicalName='solutioncomponent')/Attributes(LogicalName='componenttype')" +
                    "/Microsoft.Dynamics.CRM.PicklistAttributeMetadata?$select=LogicalName&$expand=OptionSet", ct)
                    .ConfigureAwait(false);

                if (el.TryGetProperty("OptionSet", out var os) && os.TryGetProperty("Options", out var options))
                {
                    foreach (var opt in options.EnumerateArray())
                    {
                        var value = opt.GetProperty("Value").GetInt32();
                        var label = Label(opt, "Label") ?? value.ToString();
                        Add(new ComponentTypeDef
                        {
                            Value = value,
                            Label = label,
                            Key = ComponentTypeDef.Normalize(label)
                        });
                    }
                }
            }
            catch
            {
                // Older or locked-down environments: fall back to the well-known codes.
            }

            foreach (var kv in Fallback)
                if (!_byKey.ContainsKey(kv.Key))
                    Add(new ComponentTypeDef { Value = kv.Value, Label = Pretty(kv.Key), Key = kv.Key });

            BuildTypeNames();
        }

        private void Add(ComponentTypeDef def)
        {
            if (string.IsNullOrEmpty(def.Key) || _byKey.ContainsKey(def.Key)) return;
            _byKey[def.Key] = def;
            Types.Add(def);
        }

        private void BuildTypeNames()
        {
            TypeNames.Clear();
            // Friendly names first: these are what a hotfix checklist actually says.
            foreach (var friendly in new[]
                     {
                         "Table", "Column", "Choice", "View", "Form", "Chart", "Workflow", "BPF", "CloudFlow",
                         "PluginAssembly", "PluginType", "PluginStep", "WebResource", "App", "CanvasApp",
                         "SecurityRole", "ConnectionReference", "EnvironmentVariable", "Relationship",
                         "CustomApi", "CustomControl", "SiteMap", "ServiceEndpoint", "Report"
                     })
            {
                if (Aliases.TryGetValue(ComponentTypeDef.Normalize(friendly), out var key) && _byKey.ContainsKey(key))
                    TypeNames.Add(friendly);
            }

            foreach (var t in Types.OrderBy(t => t.Label, StringComparer.OrdinalIgnoreCase))
                if (!TypeNames.Contains(t.Label, StringComparer.OrdinalIgnoreCase))
                    TypeNames.Add(t.Label);
        }

        private static string Pretty(string key) =>
            key.Length == 0 ? key : char.ToUpperInvariant(key[0]) + key.Substring(1);

        /// <summary>Maps whatever the user typed onto a component type, via alias then exact label.</summary>
        public ComponentTypeDef MatchType(string userInput)
        {
            var norm = ComponentTypeDef.Normalize(userInput);
            if (norm.Length == 0) return null;

            if (Aliases.TryGetValue(norm, out var aliasKey) && _byKey.TryGetValue(aliasKey, out var byAlias))
                return byAlias;

            if (_byKey.TryGetValue(norm, out var exact)) return exact;

            // Last resort: a raw component type number.
            if (int.TryParse(userInput?.Trim(), out var code))
            {
                var byCode = Types.FirstOrDefault(t => t.Value == code);
                if (byCode != null) return byCode;
                return new ComponentTypeDef { Value = code, Label = code.ToString(), Key = norm };
            }

            return null;
        }

        public async Task<ResolveResult> ResolveAsync(ComponentRow row, CancellationToken ct)
        {
            var type = MatchType(row.Type);
            if (type == null)
                return Fail($"Type '{row.Type}' không hợp lệ. Xem danh sách trong dropdown cột Type.");

            var name = (row.Name ?? "").Trim();
            if (name.Length == 0)
                return Fail("Thiếu Name.");

            // A pasted GUID skips lookup entirely - handy for the odd component with a duplicated name.
            if (Guid.TryParse(name, out var direct))
                return new ResolveResult
                {
                    ObjectId = direct.ToString(),
                    ComponentType = type.Value,
                    State = RowState.Resolved,
                    Message = "Dùng GUID trực tiếp"
                };

            try
            {
                return type.Key switch
                {
                    "entity" => await ResolveEntityAsync(name, type, ct).ConfigureAwait(false),
                    "attribute" => await ResolveAttributeAsync(name, type, ct).ConfigureAwait(false),
                    "optionset" => await ResolveOptionSetAsync(name, type, ct).ConfigureAwait(false),
                    "entitykey" => await ResolveEntityKeyAsync(name, type, ct).ConfigureAwait(false),
                    "entityrelationship" or "relationship" =>
                        await ResolveRelationshipAsync(name, type, ct).ConfigureAwait(false),
                    _ => await ResolveByQueryAsync(name, type, ct).ConfigureAwait(false)
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new ResolveResult { State = RowState.Failed, Message = ex.Message };
            }
        }

        // ---------- metadata resolvers ----------

        private async Task<ResolveResult> ResolveEntityAsync(string name, ComponentTypeDef type, CancellationToken ct)
        {
            _entityCache ??= await _client.GetAllAsync(
                "EntityDefinitions?$select=MetadataId,LogicalName,SchemaName,DisplayName", ct).ConfigureAwait(false);

            var hits = _entityCache.Where(e =>
                Eq(Str(e, "LogicalName"), name) ||
                Eq(Str(e, "SchemaName"), name) ||
                Eq(Label(e, "DisplayName"), name)).ToList();

            if (hits.Count == 1)
                return Ok(Str(hits[0], "MetadataId"), type, Str(hits[0], "LogicalName"));

            if (hits.Count == 0)
                return NotFound(name, type, _entityCache
                    .Select(e => Str(e, "LogicalName"))
                    .Where(n => Contains(n, name)).Take(5));

            return Ambiguous(hits.Select(e => $"{Str(e, "LogicalName")} = {Str(e, "MetadataId")}"));
        }

        private async Task<ResolveResult> ResolveAttributeAsync(string name, ComponentTypeDef type, CancellationToken ct)
        {
            if (!SplitQualified(name, out var table, out var column))
                return Fail("Column phải ghi dạng 'tenbang.tencot', ví dụ hs_api_log.hs_retry_count");

            var all = await _client.GetAllAsync(
                $"EntityDefinitions(LogicalName='{DataverseClient.Esc(table)}')/Attributes" +
                "?$select=MetadataId,LogicalName,SchemaName,DisplayName", ct).ConfigureAwait(false);

            var hits = all.Where(a =>
                Eq(Str(a, "LogicalName"), column) ||
                Eq(Str(a, "SchemaName"), column) ||
                Eq(Label(a, "DisplayName"), column)).ToList();

            if (hits.Count == 1) return Ok(Str(hits[0], "MetadataId"), type, $"{table}.{Str(hits[0], "LogicalName")}");
            if (hits.Count == 0)
                return NotFound(name, type, all.Select(a => Str(a, "LogicalName")).Where(n => Contains(n, column)).Take(5));
            return Ambiguous(hits.Select(a => $"{Str(a, "LogicalName")} = {Str(a, "MetadataId")}"));
        }

        private async Task<ResolveResult> ResolveOptionSetAsync(string name, ComponentTypeDef type, CancellationToken ct)
        {
            if (_optionSetCache == null)
            {
                try
                {
                    _optionSetCache = await _client.GetAllAsync(
                        "GlobalOptionSetDefinitions?$select=MetadataId,Name,DisplayName", ct).ConfigureAwait(false);
                }
                catch
                {
                    _optionSetCache = await _client.GetAllAsync("GlobalOptionSetDefinitions", ct).ConfigureAwait(false);
                }
            }

            var hits = _optionSetCache.Where(o =>
                Eq(Str(o, "Name"), name) || Eq(Label(o, "DisplayName"), name)).ToList();

            if (hits.Count == 1) return Ok(Str(hits[0], "MetadataId"), type, Str(hits[0], "Name"));
            if (hits.Count == 0)
                return NotFound(name, type, _optionSetCache.Select(o => Str(o, "Name")).Where(n => Contains(n, name)).Take(5));
            return Ambiguous(hits.Select(o => $"{Str(o, "Name")} = {Str(o, "MetadataId")}"));
        }

        private async Task<ResolveResult> ResolveEntityKeyAsync(string name, ComponentTypeDef type, CancellationToken ct)
        {
            if (!SplitQualified(name, out var table, out var key))
                return Fail("Key phải ghi dạng 'tenbang.tenkey'");

            var all = await _client.GetAllAsync(
                $"EntityDefinitions(LogicalName='{DataverseClient.Esc(table)}')/Keys" +
                "?$select=MetadataId,LogicalName,SchemaName,DisplayName", ct).ConfigureAwait(false);

            var hits = all.Where(k =>
                Eq(Str(k, "LogicalName"), key) ||
                Eq(Str(k, "SchemaName"), key) ||
                Eq(Label(k, "DisplayName"), key)).ToList();

            if (hits.Count == 1) return Ok(Str(hits[0], "MetadataId"), type, name);
            if (hits.Count == 0) return NotFound(name, type, all.Select(k => Str(k, "SchemaName")).Take(5));
            return Ambiguous(hits.Select(k => $"{Str(k, "SchemaName")} = {Str(k, "MetadataId")}"));
        }

        private async Task<ResolveResult> ResolveRelationshipAsync(string name, ComponentTypeDef type, CancellationToken ct)
        {
            _relationshipCache ??= await _client.GetAllAsync(
                "RelationshipDefinitions?$select=MetadataId,SchemaName", ct).ConfigureAwait(false);

            var hits = _relationshipCache.Where(r => Eq(Str(r, "SchemaName"), name)).ToList();

            if (hits.Count == 1) return Ok(Str(hits[0], "MetadataId"), type, Str(hits[0], "SchemaName"));
            if (hits.Count == 0)
                return NotFound(name, type, _relationshipCache.Select(r => Str(r, "SchemaName")).Where(n => Contains(n, name)).Take(5));
            return Ambiguous(hits.Select(r => $"{Str(r, "SchemaName")} = {Str(r, "MetadataId")}"));
        }

        // ---------- record resolvers ----------

        private async Task<ResolveResult> ResolveByQueryAsync(string name, ComponentTypeDef type, CancellationToken ct)
        {
            if (!Queries.TryGetValue(type.Key, out var q))
                return Fail($"Chưa hỗ trợ tự tìm ID cho type '{type.Label}'. Hãy điền thẳng GUID vào cột Name.");

            string parentValue = null;
            var lookup = name;
            if (q.ParentField != null && name.Contains('|'))
            {
                var parts = name.Split('|', 2);
                parentValue = parts[0].Trim();
                lookup = parts[1].Trim();
            }

            var select = new List<string> { q.IdField };
            select.AddRange(q.NameFields);
            select.AddRange(q.ExtraSelect);

            var filters = new List<string>();
            var nameOr = string.Join(" or ", q.NameFields.Select(f => $"{f} eq '{DataverseClient.Esc(lookup)}'"));
            filters.Add($"({nameOr})");
            if (!string.IsNullOrEmpty(q.FixedFilter)) filters.Add(q.FixedFilter);
            if (parentValue != null) filters.Add($"{q.ParentField} eq '{DataverseClient.Esc(parentValue)}'");

            var url = $"{q.EntitySet}?$select={string.Join(",", select.Distinct())}" +
                      $"&$filter={F(string.Join(" and ", filters))}&$top=25";

            List<JsonElement> hits;
            try
            {
                hits = await _client.GetAllAsync(url, ct).ConfigureAwait(false);
            }
            catch (DataverseException)
            {
                // Some name fields are not filterable on every version - retry one field at a time.
                hits = new List<JsonElement>();
                foreach (var field in q.NameFields)
                {
                    var single = new List<string> { $"{field} eq '{DataverseClient.Esc(lookup)}'" };
                    if (!string.IsNullOrEmpty(q.FixedFilter)) single.Add(q.FixedFilter);
                    if (parentValue != null) single.Add($"{q.ParentField} eq '{DataverseClient.Esc(parentValue)}'");
                    try
                    {
                        hits = await _client.GetAllAsync(
                            $"{q.EntitySet}?$select={q.IdField},{field}&$filter={F(string.Join(" and ", single))}&$top=25",
                            ct).ConfigureAwait(false);
                    }
                    catch (DataverseException) { continue; }
                    if (hits.Count > 0) break;
                }
            }

            if (hits.Count == 1)
                return Ok(Str(hits[0], q.IdField), type, Describe(hits[0], q));

            if (hits.Count == 0)
                return NotFound(name, type, await SuggestAsync(q, lookup, ct).ConfigureAwait(false));

            return Ambiguous(hits.Select(h => $"{Describe(h, q)} = {Str(h, q.IdField)}"));
        }

        private async Task<IEnumerable<string>> SuggestAsync(QueryDef q, string lookup, CancellationToken ct)
        {
            if (lookup.Length < 3) return Array.Empty<string>();
            var field = q.NameFields[0];
            var filters = new List<string> { $"contains({field},'{DataverseClient.Esc(lookup)}')" };
            if (!string.IsNullOrEmpty(q.FixedFilter)) filters.Add(q.FixedFilter);
            try
            {
                var rows = await _client.GetAllAsync(
                    $"{q.EntitySet}?$select={q.IdField},{field}&$filter={F(string.Join(" and ", filters))}&$top=5",
                    ct).ConfigureAwait(false);
                return rows.Select(r => Str(r, field)).Where(s => !string.IsNullOrEmpty(s)).ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static string Describe(JsonElement row, QueryDef q)
        {
            var main = q.NameFields.Select(f => Str(row, f)).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "?";
            var extra = q.ExtraSelect
                .Select(f => Str(row, f))
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList();
            return extra.Count == 0 ? main : $"{main} [{string.Join("/", extra)}]";
        }

        // ---------- helpers ----------

        private static ResolveResult Ok(string id, ComponentTypeDef type, string matched) => new ResolveResult
        {
            ObjectId = id,
            ComponentType = type.Value,
            State = RowState.Resolved,
            Message = matched,
            MatchedName = matched
        };

        private static ResolveResult Fail(string message) =>
            new ResolveResult { State = RowState.Failed, Message = message };

        private static ResolveResult NotFound(string name, ComponentTypeDef type, IEnumerable<string> suggestions)
        {
            var list = suggestions?.Where(s => !string.IsNullOrWhiteSpace(s)).Take(5).ToList() ?? new List<string>();
            var sb = new StringBuilder($"Không tìm thấy {type.Label} tên '{name}'");
            if (list.Count > 0) sb.Append($". Gần giống: {string.Join(" | ", list)}");
            return new ResolveResult { State = RowState.NotFound, Message = sb.ToString() };
        }

        private static ResolveResult Ambiguous(IEnumerable<string> candidates) => new ResolveResult
        {
            State = RowState.Ambiguous,
            Message = "Trùng tên, hãy dán GUID vào cột Name: " + string.Join(" | ", candidates.Take(5))
        };

        private static bool SplitQualified(string name, out string parent, out string child)
        {
            var sep = name.IndexOfAny(new[] { '.', '|' });
            if (sep <= 0 || sep == name.Length - 1) { parent = child = null; return false; }
            parent = name.Substring(0, sep).Trim();
            child = name.Substring(sep + 1).Trim();
            return true;
        }

        private static string Str(JsonElement el, string prop) =>
            el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Null
                ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
                : null;

        private static string Label(JsonElement el, string prop)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var label)) return null;
            if (label.ValueKind == JsonValueKind.String) return label.GetString();
            if (label.ValueKind != JsonValueKind.Object) return null;
            if (label.TryGetProperty("UserLocalizedLabel", out var ull) && ull.ValueKind == JsonValueKind.Object &&
                ull.TryGetProperty("Label", out var text))
                return text.GetString();
            if (label.TryGetProperty("LocalizedLabels", out var all) && all.ValueKind == JsonValueKind.Array)
                foreach (var l in all.EnumerateArray())
                    if (l.TryGetProperty("Label", out var t)) return t.GetString();
            return null;
        }

        /// <summary>OData filters travel in the query string, so names with [ ] &amp; # must be percent-encoded.</summary>
        private static string F(string filter) => Uri.EscapeDataString(filter);

        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static bool Contains(string haystack, string needle) =>
            haystack != null && needle != null &&
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
