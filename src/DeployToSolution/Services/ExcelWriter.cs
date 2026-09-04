using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DeployToSolution.Services
{
    /// <summary>Một dropdown trên vùng ô, giống Data Validation của Excel.</summary>
    public class ExcelDropdown
    {
        /// <summary>Vùng áp dụng, ví dụ "A2:A1000".</summary>
        public string Range { get; set; }

        /// <summary>
        /// Nguồn giá trị: tham chiếu sheet khác ("DanhMucType!$A$2:$A$30")
        /// hoặc danh sách cố định trong ngoặc kép ("\"Y,N\"").
        /// </summary>
        public string Source { get; set; }
    }

    public class ExcelSheet
    {
        public string Name { get; set; } = "Sheet1";
        public List<IEnumerable<string>> Rows { get; set; } = new List<IEnumerable<string>>();
        public double[] ColumnWidths { get; set; }
        public bool FreezeHeader { get; set; } = true;
        public bool HeaderStyle { get; set; } = true;
        public List<ExcelDropdown> Dropdowns { get; set; } = new List<ExcelDropdown>();
    }

    /// <summary>
    /// Ghi .xlsx bằng ZipArchive, không cần thư viện ngoài.
    /// Hỗ trợ nhiều sheet, tiêu đề in đậm nền tím, khoá dòng đầu, độ rộng cột và dropdown.
    /// Mọi ô ghi dạng inlineStr nên không cần bảng sharedStrings.
    /// </summary>
    public static class ExcelWriter
    {
        private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

        public static void Write(string path, string sheetName, IEnumerable<IEnumerable<string>> rows,
            IEnumerable<double> columnWidths = null)
        {
            Write(path, new[]
            {
                new ExcelSheet
                {
                    Name = sheetName,
                    Rows = rows.ToList(),
                    ColumnWidths = columnWidths?.ToArray()
                }
            });
        }

        public static void Write(string path, IEnumerable<ExcelSheet> sheets)
        {
            var list = sheets.ToList();
            if (list.Count == 0) throw new ArgumentException("Cần ít nhất một sheet.", nameof(sheets));

            using var file = File.Create(path);
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);

            Put(zip, "[Content_Types].xml", ContentTypes(list.Count));
            Put(zip, "_rels/.rels", RootRels());
            Put(zip, "xl/workbook.xml", Workbook(list));
            Put(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(list.Count));
            Put(zip, "xl/styles.xml", Styles());

            for (var i = 0; i < list.Count; i++)
                Put(zip, $"xl/worksheets/sheet{i + 1}.xml", Sheet(list[i]));
        }

        private static void Put(ZipArchive zip, string entryName, string content)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var stream = entry.Open();
            // Excel yêu cầu UTF-8 không BOM trong các part XML.
            var bytes = new UTF8Encoding(false).GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static string ContentTypes(int sheetCount)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            for (var i = 1; i <= sheetCount; i++)
                sb.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            sb.Append("</Types>");
            return sb.ToString();
        }

        private static string RootRels() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            $"<Relationships xmlns=\"{PkgRel}\">" +
            $"<Relationship Id=\"rId1\" Type=\"{RelNs}/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>";

        private static string Workbook(List<ExcelSheet> sheets)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append($"<workbook xmlns=\"{Main}\" xmlns:r=\"{RelNs}\"><sheets>");
            for (var i = 0; i < sheets.Count; i++)
                sb.Append($"<sheet name=\"{Escape(SafeSheetName(sheets[i].Name))}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
            sb.Append("</sheets></workbook>");
            return sb.ToString();
        }

        private static string WorkbookRels(int sheetCount)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append($"<Relationships xmlns=\"{PkgRel}\">");
            for (var i = 1; i <= sheetCount; i++)
                sb.Append($"<Relationship Id=\"rId{i}\" Type=\"{RelNs}/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
            sb.Append($"<Relationship Id=\"rId{sheetCount + 1}\" Type=\"{RelNs}/styles\" Target=\"styles.xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        /// <summary>Style 0 = thường, style 1 = tiêu đề (đậm, chữ trắng, nền tím Power Apps).</summary>
        private static string Styles() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            $"<styleSheet xmlns=\"{Main}\">" +
            "<fonts count=\"2\">" +
            "<font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
            "<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font>" +
            "</fonts>" +
            // Excel bắt buộc có đủ 2 fill mặc định đầu tiên, thiếu là file bị coi là hỏng.
            "<fills count=\"3\">" +
            "<fill><patternFill patternType=\"none\"/></fill>" +
            "<fill><patternFill patternType=\"gray125\"/></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF742774\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
            "</fills>" +
            "<borders count=\"1\"><border/></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"2\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
            "</cellXfs>" +
            "</styleSheet>";

        private static string Sheet(ExcelSheet sheet)
        {
            var grid = sheet.Rows.Select(r => r.ToList()).ToList();

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append($"<worksheet xmlns=\"{Main}\">");

            if (sheet.FreezeHeader)
            {
                sb.Append("<sheetViews><sheetView workbookViewId=\"0\">");
                sb.Append("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>");
                sb.Append("</sheetView></sheetViews>");
            }

            if (sheet.ColumnWidths != null && sheet.ColumnWidths.Length > 0)
            {
                sb.Append("<cols>");
                for (var i = 0; i < sheet.ColumnWidths.Length; i++)
                    sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" " +
                              $"width=\"{sheet.ColumnWidths[i].ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
                sb.Append("</cols>");
            }

            sb.Append("<sheetData>");
            for (var r = 0; r < grid.Count; r++)
            {
                var cells = grid[r];
                sb.Append($"<row r=\"{r + 1}\">");
                for (var c = 0; c < cells.Count; c++)
                {
                    var text = cells[c];
                    if (string.IsNullOrEmpty(text)) continue;
                    var style = (r == 0 && sheet.HeaderStyle) ? " s=\"1\"" : "";
                    sb.Append($"<c r=\"{ColumnName(c)}{r + 1}\" t=\"inlineStr\"{style}>");
                    sb.Append($"<is><t xml:space=\"preserve\">{Escape(text)}</t></is></c>");
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData>");

            // dataValidations phải đứng sau sheetData theo lược đồ OOXML.
            var dropdowns = sheet.Dropdowns?.Where(d => !string.IsNullOrEmpty(d.Range) &&
                                                        !string.IsNullOrEmpty(d.Source)).ToList();
            if (dropdowns != null && dropdowns.Count > 0)
            {
                sb.Append($"<dataValidations count=\"{dropdowns.Count}\">");
                foreach (var d in dropdowns)
                {
                    sb.Append($"<dataValidation type=\"list\" allowBlank=\"1\" showInputMessage=\"1\" " +
                              $"showErrorMessage=\"0\" sqref=\"{Escape(d.Range)}\">");
                    sb.Append($"<formula1>{Escape(d.Source.TrimStart('='))}</formula1>");
                    sb.Append("</dataValidation>");
                }
                sb.Append("</dataValidations>");
            }

            sb.Append("</worksheet>");
            return sb.ToString();
        }

        /// <summary>0 -> "A", 25 -> "Z", 26 -> "AA".</summary>
        private static string ColumnName(int index)
        {
            var name = "";
            index++;
            while (index > 0)
            {
                var rem = (index - 1) % 26;
                name = (char)('A' + rem) + name;
                index = (index - 1) / 26;
            }
            return name;
        }

        /// <summary>Tên sheet của Excel: tối đa 31 ký tự, không chứa : \ / ? * [ ]</summary>
        public static string SafeSheetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Sheet1";
            var cleaned = new string(name.Where(c => ":\\/?*[]".IndexOf(c) < 0).ToArray()).Trim();
            if (cleaned.Length == 0) return "Sheet1";
            return cleaned.Length > 31 ? cleaned.Substring(0, 31) : cleaned;
        }

        private static string Escape(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default:
                        // XML 1.0 không cho phép các ký tự điều khiển này.
                        if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') break;
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
