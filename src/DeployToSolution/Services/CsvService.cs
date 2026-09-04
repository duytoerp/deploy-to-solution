using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DeployToSolution.Models;

namespace DeployToSolution.Services
{
    /// <summary>Reads the hotfix component list. Accepts comma, semicolon or tab so an Excel paste works as-is.</summary>
    public static class CsvService
    {
        private static readonly string[] TypeHeaders = { "type", "loai", "loại", "componenttype", "kieu" };
        private static readonly string[] NameHeaders = { "name", "ten", "tên", "schemaname", "uniquename", "logicalname" };
        private static readonly string[] IncludeAllHeaders = { "includeall", "allobjects", "subcomponents", "full" };

        /// <summary>Nạp danh sách từ .xlsx hoặc .csv/.txt, tự nhận theo phần mở rộng.</summary>
        public static List<ComponentRow> Load(string path)
        {
            var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            if (ext == ".xlsx" || ext == ".xlsm")
                return FromGrid(ExcelReader.Read(path));

            return Parse(File.ReadAllText(path, DetectEncoding(path)));
        }

        public static List<ComponentRow> Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<ComponentRow>();

            var lines = SplitLines(text);
            if (lines.Count == 0) return new List<ComponentRow>();

            var delimiter = DetectDelimiter(lines[0]);
            return FromGrid(lines.Select(l => SplitFields(l, delimiter)).ToList());
        }

        /// <summary>
        /// Chuyển lưới ô (từ CSV đã tách hoặc từ Excel) thành danh sách dòng component.
        /// Nhận diện dòng tiêu đề, bỏ qua dòng trống và dòng chú thích bắt đầu bằng #.
        /// </summary>
        public static List<ComponentRow> FromGrid(List<List<string>> grid)
        {
            var rows = new List<ComponentRow>();
            if (grid == null || grid.Count == 0) return rows;

            // Không có tiêu đề thì các cột hiểu theo vị trí: Type, Name, IncludeAll.
            int typeIdx = 0, nameIdx = 1, includeIdx = 2;
            var start = 0;

            var first = grid[0];
            if (LooksLikeHeader(first))
            {
                typeIdx = IndexOfHeader(first, TypeHeaders);
                nameIdx = IndexOfHeader(first, NameHeaders);
                includeIdx = IndexOfHeader(first, IncludeAllHeaders);
                if (typeIdx < 0) typeIdx = 0;
                if (nameIdx < 0) nameIdx = Math.Min(1, first.Count - 1);
                start = 1;
            }

            for (var i = start; i < grid.Count; i++)
            {
                var f = grid[i];
                if (f == null || f.Count == 0) continue;

                var type = Field(f, typeIdx);
                var name = Field(f, nameIdx);
                if (string.IsNullOrWhiteSpace(type) && string.IsNullOrWhiteSpace(name)) continue;
                if (type.StartsWith("#")) continue;

                rows.Add(new ComponentRow
                {
                    Include = true,
                    Type = type,
                    Name = name,
                    IncludeAll = IsYes(Field(f, includeIdx))
                });
            }

            return rows;
        }

        public static void Save(string path, IEnumerable<ComponentRow> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Type,Name,IncludeAll");
            foreach (var r in rows)
                sb.AppendLine($"{Quote(r.Type)},{Quote(r.Name)},{(r.IncludeAll ? "Y" : "N")}");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        public static string ToReport(IEnumerable<ComponentRow> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Type,Name,IncludeAll,ComponentType,ObjectId,State,Message");
            foreach (var r in rows)
                sb.AppendLine(string.Join(",",
                    Quote(r.Type), Quote(r.Name), r.IncludeAll ? "Y" : "N",
                    r.ComponentType < 0 ? "" : r.ComponentType.ToString(),
                    Quote(r.ObjectId), Quote(r.StateText), Quote(r.Message)));
            return sb.ToString();
        }

        private static string Quote(string s)
        {
            s ??= "";
            return s.Contains(',') || s.Contains('"') || s.Contains('\n')
                ? "\"" + s.Replace("\"", "\"\"") + "\""
                : s;
        }

        private static Encoding DetectEncoding(string path)
        {
            using var fs = File.OpenRead(path);
            var bom = new byte[3];
            var read = fs.Read(bom, 0, 3);
            if (read == 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return new UTF8Encoding(true);
            return new UTF8Encoding(false);
        }

        private static List<string> SplitLines(string text) =>
            text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

        private static char DetectDelimiter(string line)
        {
            if (line.Contains('\t')) return '\t';
            if (line.Count(c => c == ';') > line.Count(c => c == ',')) return ';';
            return ',';
        }

        private static bool LooksLikeHeader(List<string> fields)
        {
            var first = (fields.FirstOrDefault() ?? "").Trim().ToLowerInvariant();
            return TypeHeaders.Contains(first) || NameHeaders.Contains(first);
        }

        private static int IndexOfHeader(List<string> fields, string[] candidates)
        {
            for (var i = 0; i < fields.Count; i++)
            {
                var h = fields[i].Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "");
                if (candidates.Contains(h)) return i;
            }
            return -1;
        }

        private static string Field(List<string> fields, int index) =>
            index >= 0 && index < fields.Count ? fields[index].Trim() : "";

        private static bool IsYes(string value)
        {
            value = (value ?? "").Trim().ToLowerInvariant();
            return value == "y" || value == "yes" || value == "true" || value == "1" || value == "x" || value == "co";
        }

        /// <summary>Minimal RFC4180 splitter: honours quoted fields so names containing the delimiter survive.</summary>
        private static List<string> SplitFields(string line, char delimiter)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(c);
                }
                else if (c == '"') inQuotes = true;
                else if (c == delimiter) { result.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }

            result.Add(sb.ToString());
            return result;
        }
    }
}
