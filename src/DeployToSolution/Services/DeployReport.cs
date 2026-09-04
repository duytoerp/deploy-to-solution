using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DeployToSolution.Models;

namespace DeployToSolution.Services
{
    /// <summary>
    /// Dựng bản tóm tắt để dán vào ticket deploy: gom component theo loại,
    /// và rút gọn danh sách plugin step trùng tiền tố thành "prefix.*".
    /// </summary>
    /// <summary>Một nhóm trong báo cáo deploy, ví dụ "Code dll" kèm các dòng của nó.</summary>
    public class ReportGroup
    {
        public string Title { get; set; }
        public int Count { get; set; }
        /// <summary>Các dòng đã rút gọn; chuỗi rỗng là dấu ngắt giữa hai cụm.</summary>
        public List<string> Items { get; set; } = new List<string>();
    }

    public static class DeployReport
    {
        /// <summary>Tiêu đề nhóm theo cách gọi quen của team, thay cho nhãn kỹ thuật của Dataverse.</summary>
        private static readonly Dictionary<string, string> Headings = new(StringComparer.Ordinal)
        {
            ["entity"] = "Table",
            ["attribute"] = "Column",
            ["entityrelationship"] = "Relationship",
            ["relationship"] = "Relationship",
            ["entitykey"] = "Key",
            ["optionset"] = "Choice",
            ["savedquery"] = "View",
            ["systemform"] = "Form",
            ["savedqueryvisualization"] = "Chart",
            ["workflow"] = "Process",
            ["pluginassembly"] = "Code dll",
            ["plugintype"] = "Plugin type",
            ["sdkmessageprocessingstep"] = "Step",
            ["webresource"] = "Webresource",
            ["appmodule"] = "App",
            ["canvasapp"] = "Canvas App",
            ["role"] = "Security Roles",
            ["fieldsecurityprofile"] = "Column Security Profile",
            ["connectionreference"] = "Connection Reference",
            ["environmentvariabledefinition"] = "Environment Variable",
            ["environmentvariablevalue"] = "Environment Variable Value",
            ["customcontrol"] = "PCF",
            ["customapi"] = "Custom API",
            ["sitemap"] = "Site Map",
            ["serviceendpoint"] = "Service Endpoint",
            ["report"] = "Report"
        };

        /// <summary>Thứ tự nhóm theo trình tự deploy quen thuộc, loại lạ xếp sau cùng.</summary>
        private static readonly string[] Order =
        {
            "entity", "attribute", "entityrelationship", "relationship", "entitykey",
            "optionset", "savedquery", "systemform", "savedqueryvisualization",
            "workflow", "pluginassembly", "plugintype", "sdkmessageprocessingstep",
            "webresource", "customcontrol", "customapi", "appmodule", "canvasapp",
            "sitemap", "connectionreference", "environmentvariabledefinition",
            "environmentvariablevalue", "serviceendpoint", "fieldsecurityprofile", "role"
        };

        public static List<ReportGroup> BuildGroups(IEnumerable<ComponentRow> rows, ComponentCatalog catalog)
        {
            var list = rows.Where(r => r.Include).ToList();

            // Gom theo khoá loại đã chuẩn hoá, để "Table" và "Entity" rơi vào cùng một nhóm.
            var groups = list
                .GroupBy(r => KeyOf(r, catalog))
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            var result = new List<ReportGroup>();
            foreach (var key in Order.Concat(groups.Keys.Where(k => !Order.Contains(k)).OrderBy(k => k)))
            {
                if (!groups.TryGetValue(key, out var items)) continue;
                result.Add(new ReportGroup
                {
                    Title = Headings.TryGetValue(key, out var h) ? h : Pretty(key),
                    Count = items.Count,
                    Items = Lines(key, items).ToList()
                });
            }
            return result;
        }

        public static string Build(IEnumerable<ComponentRow> rows, ComponentCatalog catalog,
            IEnumerable<string> solutions, string environmentUrl)
        {
            var groups = BuildGroups(rows, catalog);
            var total = groups.Sum(g => g.Count);

            var sb = new StringBuilder();
            foreach (var line in HeaderLines(solutions, environmentUrl, total)) sb.AppendLine(line);
            sb.AppendLine();

            foreach (var group in groups)
            {
                sb.AppendLine($"{group.Title} ({group.Count})");
                foreach (var item in group.Items) sb.AppendLine(item);
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd() + Environment.NewLine;
        }

        private static List<string> HeaderLines(IEnumerable<string> solutions, string environmentUrl, int total)
        {
            var targets = (solutions ?? Enumerable.Empty<string>()).ToList();
            var lines = new List<string>
            {
                "BÁO CÁO DEPLOY",
                new string('=', 60)
            };
            if (!string.IsNullOrWhiteSpace(environmentUrl)) lines.Add($"Môi trường : {environmentUrl}");
            if (targets.Count > 0) lines.Add($"Solution   : {string.Join(", ", targets)}");
            lines.Add($"Ngày       : {DateTime.Now:dd/MM/yyyy HH:mm}");
            lines.Add($"Tổng cộng  : {total} component");
            return lines;
        }

        /// <summary>Cùng nội dung nhưng dạng bảng hai cột, để xuất ra Excel.</summary>
        public static List<IEnumerable<string>> ToGrid(IEnumerable<ComponentRow> rows, ComponentCatalog catalog,
            IEnumerable<string> solutions, string environmentUrl)
        {
            var groups = BuildGroups(rows, catalog);
            var grid = new List<IEnumerable<string>> { new[] { "Nhóm", "Component" } };

            var targets = (solutions ?? Enumerable.Empty<string>()).ToList();
            if (!string.IsNullOrWhiteSpace(environmentUrl)) grid.Add(new[] { "Môi trường", environmentUrl });
            if (targets.Count > 0) grid.Add(new[] { "Solution", string.Join(", ", targets) });
            grid.Add(new[] { "Ngày", DateTime.Now.ToString("dd/MM/yyyy HH:mm") });
            grid.Add(new[] { "Tổng cộng", $"{groups.Sum(g => g.Count)} component" });
            grid.Add(new[] { "", "" });

            foreach (var group in groups)
            {
                grid.Add(new[] { $"{group.Title} ({group.Count})", "" });
                foreach (var item in group.Items)
                {
                    if (string.IsNullOrWhiteSpace(item)) continue; // dấu ngắt cụm chỉ có nghĩa ở bản text
                    grid.Add(new[] { "", item.TrimStart('-', ' ') });
                }
                grid.Add(new[] { "", "" });
            }

            return grid;
        }

        private static string KeyOf(ComponentRow row, ComponentCatalog catalog)
        {
            var type = catalog?.MatchType(row.Type);
            if (type != null && !string.IsNullOrEmpty(type.Key)) return type.Key;
            // Chưa kết nối thì chưa có option set componenttype, nhưng bảng bí danh vẫn dùng được.
            return ComponentCatalog.CanonicalKey(row.Type);
        }

        private static IEnumerable<string> Lines(string key, List<ComponentRow> items)
        {
            var names = items.Select(Display).Where(n => !string.IsNullOrWhiteSpace(n));

            if (key == "sdkmessageprocessingstep")
                return CollapseSteps(names);

            // Giữ nguyên thứ tự người dùng đã sắp trong danh sách, không tự sort lại.
            return names.Select(n => "- " + n).ToList();

            string Display(ComponentRow row)
            {
                var name = (row.Name ?? "").Trim();

                if (key == "entity" && row.IncludeAll) return name + " (full)";

                // Assembly quen được gọi kèm đuôi .dll trong ticket.
                if (key == "pluginassembly" && !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    return name + ".dll";

                return name;
            }
        }

        /// <summary>
        /// Nhiều step cùng một tiền tố được rút thành "prefix.*" cho ticket dễ đọc.
        /// Chèn dòng trống khi đổi namespace gốc (3 đoạn đầu) để các cụm tách bạch.
        /// </summary>
        private static List<string> CollapseSteps(IEnumerable<string> names)
        {
            var groups = names
                .Select(n => n.Trim())
                .GroupBy(Prefix)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var lines = new List<string>();
            string previousRoot = null;

            foreach (var group in groups)
            {
                var root = NamespaceRoot(group.Key);
                if (previousRoot != null && !string.Equals(root, previousRoot, StringComparison.OrdinalIgnoreCase))
                    lines.Add("");
                previousRoot = root;

                if (group.Count() >= 2 && group.Key.Length > 0)
                    lines.Add($"- {group.Key}.*");
                else
                    foreach (var single in group.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                        lines.Add("- " + single);
            }

            return lines;
        }

        private static string Prefix(string name)
        {
            var at = name.LastIndexOf('.');
            return at > 0 ? name.Substring(0, at) : name;
        }

        private static string NamespaceRoot(string prefix)
        {
            var parts = prefix.Split('.');
            return parts.Length <= 3 ? prefix : string.Join(".", parts.Take(3));
        }

        private static string Pretty(string key) =>
            string.IsNullOrEmpty(key) ? "Khác" : char.ToUpperInvariant(key[0]) + key.Substring(1);
    }
}
