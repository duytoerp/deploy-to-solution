using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace DeployToSolution.Services
{
    /// <summary>
    /// Đọc .xlsx bằng ZipArchive + XDocument có sẵn trong .NET, không cần thư viện ngoài.
    /// Chỉ lấy giá trị text của các ô - đủ cho một danh sách component.
    /// </summary>
    public static class ExcelReader
    {
        private static readonly XNamespace Main =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace Rel =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PackageRel =
            "http://schemas.openxmlformats.org/package/2006/relationships";

        /// <summary>Đọc một sheet thành lưới chuỗi. Ưu tiên sheet tên <paramref name="preferredSheet"/>.</summary>
        public static List<List<string>> Read(string path, string preferredSheet = "Components")
        {
            using var zip = ZipFile.OpenRead(path);

            var shared = ReadSharedStrings(zip);
            var sheetPath = FindSheetPath(zip, preferredSheet);
            if (sheetPath == null)
                throw new InvalidOperationException("File Excel không có sheet nào đọc được.");

            var entry = zip.GetEntry(sheetPath)
                        ?? throw new InvalidOperationException($"Không tìm thấy '{sheetPath}' trong file Excel.");

            using var stream = entry.Open();
            var doc = XDocument.Load(stream);

            var grid = new List<List<string>>();
            foreach (var row in doc.Descendants(Main + "row"))
            {
                var cells = new List<string>();
                foreach (var c in row.Elements(Main + "c"))
                {
                    var index = ColumnIndex((string)c.Attribute("r"));
                    // Ô trống bị bỏ khỏi XML, phải chèn bù để cột không bị lệch.
                    while (index >= 0 && cells.Count < index) cells.Add("");
                    cells.Add(CellText(c, shared));
                }

                while (cells.Count > 0 && string.IsNullOrWhiteSpace(cells[cells.Count - 1]))
                    cells.RemoveAt(cells.Count - 1);

                if (cells.Count > 0) grid.Add(cells);
            }

            return grid;
        }

        public static List<string> SheetNames(string path)
        {
            using var zip = ZipFile.OpenRead(path);
            var workbook = zip.GetEntry("xl/workbook.xml");
            if (workbook == null) return new List<string>();
            using var stream = workbook.Open();
            return XDocument.Load(stream).Descendants(Main + "sheet")
                .Select(s => (string)s.Attribute("name"))
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
        }

        private static string FindSheetPath(ZipArchive zip, string preferredSheet)
        {
            var workbook = zip.GetEntry("xl/workbook.xml");
            if (workbook == null) return null;

            List<XElement> sheets;
            using (var stream = workbook.Open())
                sheets = XDocument.Load(stream).Descendants(Main + "sheet").ToList();

            if (sheets.Count == 0) return null;

            var chosen = sheets.FirstOrDefault(s =>
                             string.Equals((string)s.Attribute("name"), preferredSheet,
                                 StringComparison.OrdinalIgnoreCase))
                         ?? sheets[0];

            var id = (string)chosen.Attribute(Rel + "id");
            var target = ResolveRelationship(zip, id);
            if (target != null) return target;

            // Không có rels thì đoán theo thứ tự sheet.
            var position = sheets.IndexOf(chosen) + 1;
            return $"xl/worksheets/sheet{position}.xml";
        }

        private static string ResolveRelationship(ZipArchive zip, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
            if (rels == null) return null;

            using var stream = rels.Open();
            var target = XDocument.Load(stream).Descendants(PackageRel + "Relationship")
                .Where(r => (string)r.Attribute("Id") == id)
                .Select(r => (string)r.Attribute("Target"))
                .FirstOrDefault();

            if (string.IsNullOrEmpty(target)) return null;
            target = target.Replace('\\', '/').TrimStart('/');
            return target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? target : "xl/" + target;
        }

        private static List<string> ReadSharedStrings(ZipArchive zip)
        {
            var result = new List<string>();
            var entry = zip.GetEntry("xl/sharedStrings.xml");
            if (entry == null) return result;

            using var stream = entry.Open();
            foreach (var si in XDocument.Load(stream).Descendants(Main + "si"))
            {
                // Chuỗi có định dạng bị tách thành nhiều <r><t>, phải nối lại.
                var sb = new StringBuilder();
                foreach (var t in si.Descendants(Main + "t")) sb.Append(t.Value);
                result.Add(sb.ToString());
            }
            return result;
        }

        private static string CellText(XElement cell, List<string> shared)
        {
            var type = (string)cell.Attribute("t");

            if (type == "inlineStr")
            {
                var sb = new StringBuilder();
                foreach (var t in cell.Descendants(Main + "t")) sb.Append(t.Value);
                return sb.ToString();
            }

            var value = cell.Element(Main + "v")?.Value;
            if (value == null) return "";

            if (type == "s")
                return int.TryParse(value, out var i) && i >= 0 && i < shared.Count ? shared[i] : "";

            if (type == "b")
                return value == "1" ? "TRUE" : "FALSE";

            return value;
        }

        /// <summary>"B7" -> 1 (0-based). Trả -1 khi không đọc được tham chiếu.</summary>
        private static int ColumnIndex(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return -1;
            var index = 0;
            var any = false;
            foreach (var c in reference)
            {
                if (c >= 'A' && c <= 'Z') { index = index * 26 + (c - 'A' + 1); any = true; }
                else if (c >= 'a' && c <= 'z') { index = index * 26 + (c - 'a' + 1); any = true; }
                else break;
            }
            return any ? index - 1 : -1;
        }
    }
}
