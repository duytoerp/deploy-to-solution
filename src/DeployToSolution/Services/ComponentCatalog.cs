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
            /// <summary>Field phân biệt các bản ghi trùng tên trong cùng một bảng (loại form, loại view).</summary>
            public string VariantField;
            /// <summary>Nhãn đọc được cho VariantField, dùng cả khi hiển thị lẫn khi người dùng gõ vào Name.</summary>
            public Dictionary<int, string> VariantLabels;
        }

        // systemform.type
        private static readonly Dictionary<int, string> FormTypes = new()
        {
            [0] = "Dashboard", [1] = "AppointmentBook", [2] = "Main", [3] = "MiniCampaignBO",
            [4] = "Preview", [5] = "MobileExpress", [6] = "QuickView", [7] = "QuickCreate",
            [8] = "Dialog", [9] = "TaskFlow", [10] = "InteractionCentricDashboard", [11] = "Card",
            [12] = "MainInteractive", [13] = "ContextualDashboard", [100] = "Other",
            [101] = "MainBackup", [102] = "AppointmentBookBackup", [103] = "PowerBIDashboard"
        };

        // savedquery.querytype
        private static readonly Dictionary<int, string> QueryTypes = new()
        {
            [0] = "MainView", [1] = "AdvancedFind", [2] = "SubGrid", [4] = "QuickFind",
            [8] = "Reporting", [16] = "OfflineFilters", [32] = "Lookup", [64] = "AppointmentBook",
            [128] = "OutlookFilters", [256] = "AddressBookFilters", [1024] = "OutlookTemplate",
            [2048] = "InteractiveWorkflow", [4096] = "OfflineTemplate", [8192] = "CustomDefined"
        };

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
                VariantField = "querytype", VariantLabels = QueryTypes,
                ExtraSelect = new[] { "returnedtypecode", "querytype", "componentstate" }
            },
            ["systemform"] = new QueryDef
            {
                EntitySet = "systemforms", IdField = "formid",
                NameFields = new[] { "name" }, ParentField = "objecttypecode",
                VariantField = "type", VariantLabels = FormTypes,
                ExtraSelect = new[] { "objecttypecode", "type", "componentstate" }
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

        /// <summary>Tên loại theo cách gọi quen của team, dùng cho dropdown kể cả khi chưa kết nối.</summary>
        public static readonly string[] FriendlyTypeNames =
        {
            "Table", "Column", "Choice", "View", "Form", "Chart", "Workflow", "BPF", "CloudFlow",
            "PluginAssembly", "PluginType", "PluginStep", "WebResource", "App", "CanvasApp",
            "SecurityRole", "ColumnSecurityProfile", "ConnectionReference", "EnvironmentVariable",
            "Relationship", "Key", "CustomApi", "CustomControl", "SiteMap", "ServiceEndpoint",
            "Report", "EmailTemplate", "SLA"
        };

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
            var covered = new HashSet<string>(StringComparer.Ordinal);

            // Friendly names first: these are what a hotfix checklist actually says.
            foreach (var friendly in FriendlyTypeNames)
            {
                if (!Aliases.TryGetValue(ComponentTypeDef.Normalize(friendly), out var key)) continue;
                if (!_byKey.ContainsKey(key)) continue;

                TypeNames.Add(friendly);
                covered.Add(key);
            }

            // Nhãn gốc của môi trường chỉ thêm khi chưa có tên quen nào trỏ vào cùng loại đó.
            // So bằng chuỗi thô thì "PluginStep" và "SDK Message Processing Step" là hai mục khác
            // nhau, dropdown hoá ra có cả hai cho cùng một thứ - rất dễ chọn nhầm sang PluginType.
            foreach (var t in Types.OrderBy(t => t.Label, StringComparer.OrdinalIgnoreCase))
            {
                if (!covered.Add(t.Key)) continue;

                // Hai componenttype khác nhau vẫn có thể cùng một nhãn (3 "Relationship" và
                // 10 "Entity Relationship" đều hiện là Relationship). Hai mục chữ giống hệt nhau
                // thì người dùng không phân biệt nổi - giữ mục đầu, ai cần loại kia gõ thẳng số.
                if (TypeNames.Contains(t.Label, StringComparer.OrdinalIgnoreCase)) continue;

                TypeNames.Add(t.Label);
            }
        }

        private static string Pretty(string key) =>
            key.Length == 0 ? key : char.ToUpperInvariant(key[0]) + key.Substring(1);

        /// <summary>
        /// Khoá chuẩn của một Type chỉ dựa vào bảng bí danh - dùng được khi chưa kết nối môi trường,
        /// lúc đó chưa có option set componenttype để tra.
        /// </summary>
        public static string CanonicalKey(string typeInput)
        {
            var norm = ComponentTypeDef.Normalize(typeInput);
            return Aliases.TryGetValue(norm, out var key) ? key : norm;
        }

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

        private async Task<List<JsonElement>> EnsureEntitiesAsync(CancellationToken ct) =>
            _entityCache ??= await _client.GetAllAsync(
                "EntityDefinitions?$select=MetadataId,LogicalName,SchemaName,DisplayName", ct).ConfigureAwait(false);

        private async Task<ResolveResult> ResolveEntityAsync(string name, ComponentTypeDef type, CancellationToken ct)
        {
            await EnsureEntitiesAsync(ct).ConfigureAwait(false);

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

        private async Task<List<JsonElement>> EnsureOptionSetsAsync(CancellationToken ct)
        {
            if (_optionSetCache != null) return _optionSetCache;
            try
            {
                _optionSetCache = await _client.GetAllAsync(
                    "GlobalOptionSetDefinitions?$select=MetadataId,Name,DisplayName", ct).ConfigureAwait(false);
            }
            catch
            {
                _optionSetCache = await _client.GetAllAsync("GlobalOptionSetDefinitions", ct).ConfigureAwait(false);
            }
            return _optionSetCache;
        }

        private async Task<ResolveResult> ResolveOptionSetAsync(string name, ComponentTypeDef type, CancellationToken ct)
        {
            await EnsureOptionSetsAsync(ct).ConfigureAwait(false);

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

            // Dạng đầy đủ: "bang|ten|BienThe", ví dụ hs_api_log|Information|Main
            string parentValue = null, variantValue = null;
            var lookup = name;
            if (q.ParentField != null && name.Contains('|'))
            {
                var parts = name.Split('|');
                parentValue = parts[0].Trim();
                lookup = parts.Length > 1 ? parts[1].Trim() : "";
                if (parts.Length > 2) variantValue = parts[2].Trim();
            }

            int? variantCode = null;
            if (variantValue != null && q.VariantField != null)
            {
                variantCode = ParseVariant(q, variantValue);
                if (variantCode == null)
                    return Fail($"Biến thể '{variantValue}' không hợp lệ. Chọn một trong: " +
                                string.Join(", ", q.VariantLabels.Values));
            }

            var select = new List<string> { q.IdField };
            select.AddRange(q.NameFields);
            select.AddRange(q.ExtraSelect);

            var filters = new List<string>();
            var nameOr = string.Join(" or ", q.NameFields.Select(f => $"{f} eq '{DataverseClient.Esc(lookup)}'"));
            filters.Add($"({nameOr})");
            if (!string.IsNullOrEmpty(q.FixedFilter)) filters.Add(q.FixedFilter);
            if (parentValue != null) filters.Add($"{q.ParentField} eq '{DataverseClient.Esc(parentValue)}'");
            if (variantCode != null) filters.Add($"{q.VariantField} eq {variantCode}");

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

            // Bản ghi đã xoá (componentstate 2/3) vẫn nằm trong bảng, không bao giờ được add.
            hits = hits.Where(h => !IsDeletedComponent(h)).ToList();

            if (hits.Count == 1)
                return Ok(Str(hits[0], q.IdField), type, Describe(hits[0], q));

            if (hits.Count == 0)
                return NotFound(name, type, await SuggestAsync(q, lookup, ct).ConfigureAwait(false));

            var hint = q.VariantField != null && parentValue != null
                ? $"Hoặc ghi rõ biến thể: {parentValue}|{lookup}|Main"
                : null;

            return Ambiguous(hits.Select(h => $"{Describe(h, q)} = {Str(h, q.IdField)}"), hint);
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

            var extra = new List<string>();
            if (q.ParentField != null)
            {
                var parent = Str(row, q.ParentField);
                if (!string.IsNullOrEmpty(parent)) extra.Add(parent);
            }
            if (q.VariantField != null)
            {
                var label = VariantLabel(row, q);
                if (!string.IsNullOrEmpty(label)) extra.Add(label);
            }
            foreach (var f in q.ExtraSelect)
            {
                if (f == q.ParentField || f == q.VariantField || f == "componentstate") continue;
                var v = Str(row, f);
                if (!string.IsNullOrEmpty(v)) extra.Add(v);
            }

            return extra.Count == 0 ? main : $"{main} [{string.Join("/", extra)}]";
        }

        /// <summary>Nhãn đọc được của biến thể, ví dụ type=2 -> "Main".</summary>
        private static string VariantLabel(JsonElement row, QueryDef q)
        {
            if (q.VariantField == null) return null;
            var raw = Str(row, q.VariantField);
            if (string.IsNullOrEmpty(raw)) return null;
            return int.TryParse(raw, out var code) && q.VariantLabels != null &&
                   q.VariantLabels.TryGetValue(code, out var label)
                ? label
                : raw;
        }

        /// <summary>Chấp nhận cả nhãn ("Main") lẫn số ("2") ở đoạn thứ ba của Name.</summary>
        private static int? ParseVariant(QueryDef q, string text)
        {
            if (q.VariantLabels != null)
                foreach (var kv in q.VariantLabels)
                    if (string.Equals(kv.Value, text, StringComparison.OrdinalIgnoreCase))
                        return kv.Key;
            return int.TryParse(text, out var n) ? n : (int?)null;
        }

        private static bool IsDeletedComponent(JsonElement row)
        {
            var state = Str(row, "componentstate");
            return int.TryParse(state, out var code) && (code == 2 || code == 3);
        }

        // ---------- gợi ý tên ----------

        private readonly Dictionary<string, List<string>> _suggestCache =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public const int SuggestLimit = 2000;

        /// <summary>
        /// Danh sách tên gợi ý cho cột Name, tuỳ theo Type của dòng. Nạp lười và cache lại,
        /// vì mỗi loại là một truy vấn riêng lên môi trường.
        /// </summary>
        public async Task<List<string>> SuggestNamesAsync(string typeInput, string currentName, CancellationToken ct)
        {
            var type = MatchType(typeInput);
            if (type == null) return new List<string>();

            var key = SuggestionKey(type, currentName);
            if (_suggestCache.TryGetValue(key, out var cached)) return cached;

            var names = await BuildSuggestionsAsync(type, currentName, ct).ConfigureAwait(false);

            names = names
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(SuggestLimit)
                .ToList();

            _suggestCache[key] = names;
            return names;
        }

        // Những loại mà gợi ý chỉ có nghĩa trong phạm vi một bảng.
        private static readonly Dictionary<string, char> TableScoped = new(StringComparer.Ordinal)
        {
            ["attribute"] = '.',
            ["entitykey"] = '.',
            ["savedquery"] = '|',
            ["systemform"] = '|',
            ["savedqueryvisualization"] = '|'
        };

        /// <summary>
        /// Loại chỉ có nghĩa trong phạm vi một bảng (Column, View, Form, Chart, Key) thì trả về
        /// dấu nối giữa tên bảng và tên component. Tra theo bảng bí danh nên dùng được cả khi chưa kết nối.
        /// </summary>
        public static bool TryGetTableScope(string typeInput, out char separator) =>
            TableScoped.TryGetValue(CanonicalKey(typeInput), out separator);

        /// <summary>Bảng mà người dùng đã gõ ở đầu ô Name, nếu loại này thuộc phạm vi một bảng.</summary>
        private static bool TryTableScope(ComponentTypeDef type, string currentName, out string table)
        {
            table = null;
            if (type == null || !TableScoped.TryGetValue(type.Key, out var sep)) return false;

            var text = currentName ?? "";
            var at = text.IndexOf(sep);
            if (at <= 0) return false;

            table = text.Substring(0, at).Trim();
            // Tên bảng quá ngắn thường là đang gõ dở, đừng bắn truy vấn chắc chắn hỏng.
            return table.Length >= 3;
        }

        /// <summary>
        /// Số ký tự đầu dùng làm từ khoá tìm trên server. Ngắn thì một mẻ dùng lại được cho nhiều
        /// phím gõ tiếp theo (lọc nốt ở client), dài thì bắn truy vấn liên tục.
        /// </summary>
        private const int SearchPrefix = 3;

        /// <summary>
        /// Loại đọc từ bảng dữ liệu (step, web resource, workflow, role...) có thể có hàng chục
        /// nghìn dòng, phần lớn là bản ghi hệ thống. Lấy 2000 dòng đầu theo alphabet là cắt mất
        /// component của mình, nên chữ đang gõ phải được đẩy xuống server bằng contains().
        /// </summary>
        private static bool SearchesOnServer(ComponentTypeDef type) =>
            type != null && Queries.ContainsKey(type.Key) && !TableScoped.ContainsKey(type.Key);

        /// <summary>Loại mà ô Lọc / ô Name phải gõ đủ vài ký tự thì mới tìm ra, vì phải lọc trên server.</summary>
        public bool SearchesOnServer(string typeInput) => SearchesOnServer(MatchType(typeInput));

        /// <summary>Số ký tự tối thiểu để bắt đầu tìm trên server.</summary>
        public static int SearchMinLength => SearchPrefix;

        /// <summary>
        /// Bảng Dataverse mà gợi ý của loại này đọc ra. Hiện thẳng lên giao diện để nhìn là biết
        /// đang xem step hay plugin type, khỏi phải đoán qua tên component.
        /// </summary>
        public string SourceTable(string typeInput)
        {
            var type = MatchType(typeInput);
            if (type == null) return null;
            if (Queries.TryGetValue(type.Key, out var q)) return q.EntitySet;

            return type.Key switch
            {
                "entity" => "EntityDefinitions",
                "attribute" => "EntityDefinitions/Attributes",
                "entitykey" => "EntityDefinitions/Keys",
                "optionset" => "GlobalOptionSetDefinitions",
                "entityrelationship" or "relationship" => "RelationshipDefinitions",
                _ => null
            };
        }

        private static string SearchSeed(string currentName)
        {
            var text = (currentName ?? "").Trim();
            return text.Length >= SearchPrefix ? text.Substring(0, SearchPrefix) : "";
        }

        /// <summary>
        /// Khoá của một mẻ gợi ý. Column/View/Form phụ thuộc bảng đã gõ; loại tìm trên server
        /// còn phụ thuộc mấy chữ đầu người dùng gõ.
        /// </summary>
        public string SuggestionKey(ComponentTypeDef type, string currentName)
        {
            if (type == null) return "";
            if (TryTableScope(type, currentName, out var table)) return $"{type.Key}:{table}";

            if (SearchesOnServer(type))
            {
                var seed = SearchSeed(currentName);
                if (seed.Length > 0) return $"{type.Key}~{seed.ToLowerInvariant()}";
            }

            return type.Key;
        }

        public string SuggestionKey(string typeInput, string currentName) =>
            SuggestionKey(MatchType(typeInput), currentName);

        private async Task<List<string>> BuildSuggestionsAsync(ComponentTypeDef type, string currentName, CancellationToken ct)
        {
            switch (type.Key)
            {
                case "entity":
                    return (await EnsureEntitiesAsync(ct).ConfigureAwait(false))
                        .Select(e => Str(e, "LogicalName")).ToList();

                case "attribute":
                case "entitykey":
                {
                    // Chưa gõ bảng thì gợi ý "bảng." để chọn bảng trước; gõ xong bảng thì gợi ý cột của bảng đó.
                    if (!TryTableScope(type, currentName, out var table))
                        return (await EnsureEntitiesAsync(ct).ConfigureAwait(false))
                            .Select(e => Str(e, "LogicalName") + ".").ToList();

                    var path = type.Key == "attribute" ? "Attributes" : "Keys";
                    var rows = await _client.GetAllAsync(
                        $"EntityDefinitions(LogicalName='{DataverseClient.Esc(table)}')/{path}?$select=LogicalName",
                        ct).ConfigureAwait(false);
                    return rows.Select(a => $"{table}.{Str(a, "LogicalName")}").ToList();
                }

                case "savedquery":
                case "systemform":
                case "savedqueryvisualization":
                {
                    // Cả môi trường có hàng ngàn view/form, liệt kê hết là vô dụng.
                    // Chưa gõ bảng thì gợi ý "bảng|", gõ rồi thì chỉ lấy view/form của đúng bảng đó.
                    if (!TryTableScope(type, currentName, out var owner))
                        return (await EnsureEntitiesAsync(ct).ConfigureAwait(false))
                            .Select(e => Str(e, "LogicalName") + "|").ToList();

                    return await QuerySuggestionsAsync(type, owner, null, ct).ConfigureAwait(false);
                }

                case "optionset":
                    return (await EnsureOptionSetsAsync(ct).ConfigureAwait(false))
                        .Select(o => Str(o, "Name")).ToList();

                case "entityrelationship":
                case "relationship":
                    _relationshipCache ??= await _client.GetAllAsync(
                        "RelationshipDefinitions?$select=MetadataId,SchemaName", ct).ConfigureAwait(false);
                    return _relationshipCache.Select(r => Str(r, "SchemaName")).ToList();

                default:
                    return await QuerySuggestionsAsync(type, null, SearchSeed(currentName), ct).ConfigureAwait(false);
            }
        }

        private async Task<List<string>> QuerySuggestionsAsync(ComponentTypeDef type, string ownerTable,
            string search, CancellationToken ct)
        {
            if (!Queries.TryGetValue(type.Key, out var q)) return new List<string>();

            var nameField = q.NameFields[0];
            var fields = new List<string> { nameField };
            if (q.ParentField != null) fields.Add(q.ParentField);
            if (q.VariantField != null) fields.Add(q.VariantField);

            var plain = string.Join(",", fields.Distinct());
            // componentstate để loại bản ghi đã xoá, đúng như lúc resolve.
            var withState = string.Join(",", fields.Concat(new[] { "componentstate" }).Distinct());

            var baseFilters = new List<string>();
            if (!string.IsNullOrEmpty(q.FixedFilter)) baseFilters.Add(q.FixedFilter);
            if (!string.IsNullOrEmpty(ownerTable) && q.ParentField != null)
                baseFilters.Add($"{q.ParentField} eq '{DataverseClient.Esc(ownerTable)}'");

            // Tìm ngay trên server: bảng như sdkmessageprocessingstep có hàng chục nghìn dòng,
            // cắt 2000 dòng đầu theo alphabet là mất sạch component của mình.
            if (!string.IsNullOrEmpty(search))
                baseFilters.Add($"contains({nameField},'{DataverseClient.Esc(search)}')");

            // Hotfix hầu như chỉ đụng component unmanaged; lọc bớt hàng ngàn bản ghi hệ thống.
            var unmanagedOnly = new List<string>(baseFilters) { "ismanaged eq false" };

            // Nới dần: hết unmanaged thì lấy cả managed, môi trường không có componentstate thì bỏ cột đó.
            var attempts = new[]
            {
                (Select: withState, Filters: unmanagedOnly),
                (Select: withState, Filters: baseFilters),
                (Select: plain,     Filters: unmanagedOnly),
                (Select: plain,     Filters: baseFilters)
            };

            foreach (var attempt in attempts)
            {
                var url = $"{q.EntitySet}?$select={attempt.Select}&$orderby={nameField}&$top={SuggestLimit}";
                if (attempt.Filters.Count > 0) url += $"&$filter={F(string.Join(" and ", attempt.Filters))}";

                List<JsonElement> rows;
                try
                {
                    rows = await _client.GetAllAsync(url, ct).ConfigureAwait(false);
                }
                catch (DataverseException)
                {
                    continue;   // cột không tồn tại trên môi trường này - thử cách nới hơn
                }

                // Bản ghi đã xoá vẫn nằm trong bảng nhưng không bao giờ add được, đừng gợi ý.
                var live = rows.Where(r => !IsDeletedComponent(r)).ToList();

                // Lọc xong không còn gì thì chưa chắc là "không có", mà có thể do bộ lọc quá chặt.
                if (live.Count == 0) continue;

                return live.Select(r =>
                {
                    var name = Str(r, nameField);
                    if (q.ParentField == null) return name;

                    var parent = Str(r, q.ParentField);
                    if (string.IsNullOrEmpty(parent)) return name;

                    // Kèm luôn biến thể: form/view hay trùng tên trong cùng một bảng.
                    var variant = VariantLabel(r, q);
                    return string.IsNullOrEmpty(variant)
                        ? $"{parent}|{name}"
                        : $"{parent}|{name}|{variant}";
                }).ToList();
            }

            return new List<string>();
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

        private static ResolveResult Ambiguous(IEnumerable<string> candidates, string hint = null)
        {
            var message = "Trùng tên. " + (hint == null ? "" : hint + ". ") +
                          "Hoặc dán GUID vào cột Name: " + string.Join(" | ", candidates.Take(5));
            return new ResolveResult { State = RowState.Ambiguous, Message = message };
        }

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
