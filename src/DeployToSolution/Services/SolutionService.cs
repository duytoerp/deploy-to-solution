using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeployToSolution.Models;

namespace DeployToSolution.Services
{
    /// <summary>A component Dataverse pulled into the solution on its own, not one the CSV asked for.</summary>
    public class StrayComponent
    {
        public string ObjectId { get; set; }
        public int ComponentType { get; set; }
        public string Name { get; set; }
    }

    /// <summary>Kết quả dò component archival: gỡ được gì, và vì sao không gỡ được.</summary>
    public class ArchivalScan
    {
        public List<StrayComponent> Removable { get; } = new List<StrayComponent>();
        public List<string> Notes { get; } = new List<string>();
    }

    public class SolutionService
    {
        private readonly DataverseClient _client;
        private readonly Dictionary<string, string> _solutionIds =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public SolutionService(DataverseClient client) => _client = client;

        /// <summary>Unmanaged, non-system solutions - the only ones a hotfix can be built into.</summary>
        public async Task<List<SolutionTarget>> ListSolutionsAsync(CancellationToken ct)
        {
            var rows = await _client.GetAllAsync(
                "solutions?$select=solutionid,uniquename,friendlyname,version,ismanaged" +
                "&$filter=isvisible eq true&$orderby=friendlyname asc", ct).ConfigureAwait(false);

            var list = new List<SolutionTarget>();
            foreach (var r in rows)
            {
                var unique = Get(r, "uniquename");
                if (string.IsNullOrEmpty(unique)) continue;
                if (unique.Equals("Active", StringComparison.OrdinalIgnoreCase) ||
                    unique.Equals("Basic", StringComparison.OrdinalIgnoreCase) ||
                    unique.Equals("Default", StringComparison.OrdinalIgnoreCase)) continue;

                var managed = r.TryGetProperty("ismanaged", out var m) && m.ValueKind == JsonValueKind.True;
                if (managed) continue;

                _solutionIds[unique] = Get(r, "solutionid");
                list.Add(new SolutionTarget
                {
                    Enabled = false,
                    UniqueName = unique,
                    FriendlyName = Get(r, "friendlyname"),
                    Version = Get(r, "version"),
                    IsManaged = false
                });
            }
            return list;
        }

        public async Task<string> GetSolutionIdAsync(string uniqueName, CancellationToken ct)
        {
            if (_solutionIds.TryGetValue(uniqueName, out var cached) && !string.IsNullOrEmpty(cached))
                return cached;

            var rows = await _client.GetAllAsync(
                $"solutions?$select=solutionid,ismanaged&$filter=uniquename eq '{DataverseClient.Esc(uniqueName)}'",
                ct).ConfigureAwait(false);

            if (rows.Count == 0)
                throw new InvalidOperationException($"Không tìm thấy solution '{uniqueName}' trong môi trường này.");

            if (rows[0].TryGetProperty("ismanaged", out var m) && m.ValueKind == JsonValueKind.True)
                throw new InvalidOperationException($"Solution '{uniqueName}' là managed, không thể add component.");

            var id = Get(rows[0], "solutionid");
            _solutionIds[uniqueName] = id;
            return id;
        }

        /// <summary>True when the component is already a member of the solution, so the run stays idempotent.</summary>
        public async Task<bool> ContainsAsync(string solutionId, string objectId, int componentType, CancellationToken ct)
        {
            var rows = await _client.GetAllAsync(
                "solutioncomponents?$select=solutioncomponentid" +
                $"&$filter=_solutionid_value eq {solutionId} and objectid eq {objectId} and componenttype eq {componentType}",
                ct).ConfigureAwait(false);
            return rows.Count > 0;
        }

        public Task AddAsync(string solutionUniqueName, string objectId, int componentType,
            bool includeAllSubcomponents, bool addRequiredComponents, CancellationToken ct)
        {
            var payload = new Dictionary<string, object>
            {
                ["ComponentId"] = objectId,
                ["ComponentType"] = componentType,
                ["SolutionUniqueName"] = solutionUniqueName,
                ["AddRequiredComponents"] = addRequiredComponents,
                ["DoNotIncludeSubcomponents"] = !includeAllSubcomponents
            };
            return _client.PostActionAsync("AddSolutionComponent", payload, ct);
        }

        public Task RemoveAsync(string solutionUniqueName, string objectId, int componentType, CancellationToken ct)
        {
            var payload = new Dictionary<string, object>
            {
                ["ComponentId"] = objectId,
                ["ComponentType"] = componentType,
                ["SolutionUniqueName"] = solutionUniqueName
            };
            return _client.PostActionAsync("RemoveSolutionComponent", payload, ct);
        }

        /// <summary>
        /// Adding a table drags in its MetadataForArchival extension row (retention metadata), which shows up
        /// as a second component with the same name. Looks the row up two independent ways and reports every
        /// step, so a run that removes nothing still says why.
        /// </summary>
        public async Task<ArchivalScan> ScanArchivalAsync(string solutionId, string entityMetadataId,
            string entityLogicalName, CancellationToken ct)
        {
            var scan = new ArchivalScan();
            var found = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

            const string Select = "$select=metadataforarchivalid,name,statecode," +
                                  "isavailableforarchival,isreadyforarchival";

            var probes = new List<(string Label, string Filter)>
            {
                ("theo extensionofrecordid", $"_extensionofrecordid_value eq {entityMetadataId}")
            };
            if (!string.IsNullOrWhiteSpace(entityLogicalName))
                probes.Add(("theo name", $"name eq '{DataverseClient.Esc(entityLogicalName)}'"));

            foreach (var probe in probes)
            {
                try
                {
                    var rows = await _client.GetAllAsync(
                        $"metadataforarchivals?{Select}&$filter={Uri.EscapeDataString(probe.Filter)}",
                        ct).ConfigureAwait(false);

                    foreach (var r in rows)
                    {
                        var id = Get(r, "metadataforarchivalid");
                        if (!string.IsNullOrEmpty(id)) found[id] = r;
                    }
                    scan.Notes.Add($"tra {probe.Label}: {rows.Count} bản ghi");
                }
                catch (DataverseException ex)
                {
                    scan.Notes.Add($"tra {probe.Label} LỖI {ex.StatusCode}: {ex.Message}");
                }
            }

            if (found.Count == 0)
            {
                scan.Notes.Add("không thấy bản ghi MetadataForArchival nào của table này");
                return scan;
            }

            foreach (var kv in found)
            {
                var record = kv.Value;
                var name = Get(record, "name") ?? entityLogicalName ?? "MetadataForArchival";
                var available = IsTrue(record, "isavailableforarchival");
                var ready = IsTrue(record, "isreadyforarchival");
                var state = Get(record, "statecode");

                // isavailableforarchival chỉ nói table ĐỦ ĐIỀU KIỆN dùng retention - nền tảng tự set cho
                // hầu hết bảng, nên không dùng để quyết định. isreadyforarchival mới là "retention đã bật".
                if (ready)
                {
                    scan.Notes.Add($"giữ '{name}': retention đã bật (isreadyforarchival=True)");
                    continue;
                }

                scan.Notes.Add($"'{name}' chưa bật retention " +
                               $"(isavailableforarchival={available}, isreadyforarchival=False, " +
                               $"statecode={state ?? "?"}) -> sẽ gỡ khỏi solution");

                List<JsonElement> inSolution;
                try
                {
                    inSolution = await _client.GetAllAsync(
                        "solutioncomponents?$select=componenttype" +
                        $"&$filter=_solutionid_value eq {solutionId} and objectid eq {kv.Key}",
                        ct).ConfigureAwait(false);
                }
                catch (DataverseException ex)
                {
                    scan.Notes.Add($"không đọc được solutioncomponents cho '{name}': {ex.Message}");
                    continue;
                }

                if (inSolution.Count == 0)
                {
                    scan.Notes.Add($"'{name}' không nằm trong solution này");
                    continue;
                }

                foreach (var c in inSolution)
                {
                    if (!c.TryGetProperty("componenttype", out var code) || code.ValueKind != JsonValueKind.Number)
                        continue;
                    scan.Removable.Add(new StrayComponent
                    {
                        ObjectId = kv.Key,
                        ComponentType = code.GetInt32(),
                        Name = name
                    });
                }
            }

            return scan;
        }

        private static bool IsTrue(JsonElement el, string prop)
        {
            if (!el.TryGetProperty(prop, out var v)) return false;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.Number => v.GetInt32() != 0,
                JsonValueKind.String => bool.TryParse(v.GetString(), out var b) && b,
                _ => false
            };
        }

        private static string Get(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Null
                ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
                : null;
    }
}
