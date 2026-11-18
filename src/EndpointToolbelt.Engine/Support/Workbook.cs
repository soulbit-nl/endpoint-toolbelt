using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace EndpointToolbelt.Engine.Support;

/// <summary>How a workbook column writes its value.</summary>
public enum CellKind
{
    Text,

    /// <summary>Text, shaded when the row needs attention.</summary>
    Verdict,

    Number,

    /// <summary>An IME timestamp in UTC, shown in device time.</summary>
    UtcDate,

    /// <summary>An IME timestamp already in device time.</summary>
    LocalDate,
}

/// <summary>One column of a workbook tab.</summary>
public sealed class SheetColumn
{
    public SheetColumn(string header, int width, CellKind kind, Func<object, object> value)
    {
        Header = header;
        Width = width;
        Kind = kind;
        Value = value;
    }

    public string Header { get; }
    public int Width { get; }
    public CellKind Kind { get; }
    public Func<object, object> Value { get; }
}

/// <summary>One tab: its columns, its rows in display order, which rows need attention, and the text of an empty tab.</summary>
public sealed class Sheet
{
    public string Name { get; set; } = "";
    public string TableName { get; set; } = "";
    public IReadOnlyList<SheetColumn> Columns { get; set; } = Array.Empty<SheetColumn>();
    public IReadOnlyList<object> Rows { get; set; } = Array.Empty<object>();
    public Func<object, bool> Attention { get; set; } = _ => false;
    public string EmptyText { get; set; } = "";
}

/// <summary>
/// A minimal .xlsx writer for the support package: one tab per table, each a real Excel table with
/// a frozen header row, a filter on every column, dates as dates and the verdict cell shaded where
/// a record needs attention. Written directly as Office Open XML: no Excel, no COM (Office
/// automation is not supported from a service) and no library. Numbers and dates are written in
/// the invariant culture, so a system locale with a decimal comma cannot corrupt them. A CSV alone
/// opens as one column in Excel under locales that use ';'.
/// </summary>
public static class Workbook
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>How the IME writes timestamps in the registry: US format, with or without AM/PM, in the device's own clock.</summary>
    public static readonly string[] ImeTimestampFormats = { "MM/dd/yyyy HH:mm:ss", "M/d/yyyy H:mm:ss", "M/d/yyyy h:mm:ss tt" };

    private static readonly Regex NotXml = new("[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F\\uFFFE\\uFFFF]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    /// <summary>A registry timestamp of the IME, or MinValue when there is none (sorts last).</summary>
    public static DateTime ParseImeTimestamp(string text) =>
        !string.IsNullOrEmpty(text) && DateTime.TryParseExact(text, ImeTimestampFormats, Invariant, DateTimeStyles.None, out var parsed) ? parsed : DateTime.MinValue;

    /// <summary>A UTC moment as the UtcDate columns read it (shown in device time).</summary>
    public static string SheetUtc(DateTime? utc) => utc == null ? "" : utc.Value.ToString("MM/dd/yyyy HH:mm:ss", Invariant);

    /// <summary>Cell text: XML-escaped, without the control characters XML 1.0 cannot carry (one in an app name would make Excel refuse the whole file).</summary>
    public static string XmlText(string text) => text == null ? "" : SecurityElement.Escape(NotXml.Replace(text, ""));

    /// <summary>1 -> A, 26 -> Z, 27 -> AA.</summary>
    public static string ColumnName(int index)
    {
        var name = "";
        var n = index;
        while (n > 0)
        {
            var m = (n - 1) % 26;
            name = (char)(65 + m) + name;
            n = (n - 1) / 26;
        }
        return name;
    }

    /// <summary>
    /// Writes the sheets as one .xlsx. A sheet without rows gets its header and EmptyText and no
    /// table: Excel needs at least one row for a table. The file must not exist yet.
    /// </summary>
    public static void Write(IReadOnlyList<Sheet> sheets, string path)
    {
        var parts = new List<KeyValuePair<string, string>>();
        var sheetList = new StringBuilder();
        var workbookRels = new StringBuilder();
        var overrides = new StringBuilder();
        var tableCount = 0;

        for (var s = 1; s <= sheets.Count; s++)
        {
            var def = sheets[s - 1];
            var columns = def.Columns;
            var rows = def.Rows;
            var hasTable = rows.Count > 0;
            var sheet = new StringBuilder();
            sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sheet.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            sheet.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/><selection pane=\"bottomLeft\" activeCell=\"A2\" sqref=\"A2\"/></sheetView></sheetViews>");
            sheet.Append("<sheetFormatPr defaultRowHeight=\"15\"/><cols>");
            for (var c = 1; c <= columns.Count; c++)
            {
                sheet.AppendFormat(Invariant, "<col min=\"{0}\" max=\"{0}\" width=\"{1}\" customWidth=\"1\"/>", c, columns[c - 1].Width);
            }
            sheet.Append("</cols><sheetData><row r=\"1\">");
            for (var c = 1; c <= columns.Count; c++)
            {
                sheet.AppendFormat(Invariant, "<c r=\"{0}1\" t=\"inlineStr\"><is><t>{1}</t></is></c>", ColumnName(c), XmlText(columns[c - 1].Header));
            }
            sheet.Append("</row>");
            if (!hasTable)
            {
                sheet.AppendFormat(Invariant, "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>{0}</t></is></c></row>", XmlText(def.EmptyText));
            }

            var rowNumber = 1;
            foreach (var record in rows)
            {
                rowNumber++;
                var shade = def.Attention(record);
                sheet.AppendFormat(Invariant, "<row r=\"{0}\">", rowNumber);
                for (var c = 1; c <= columns.Count; c++)
                {
                    var column = columns[c - 1];
                    var cell = ColumnName(c) + rowNumber.ToString(Invariant);
                    var value = column.Value(record);
                    var text = value == null ? null : Convert.ToString(value, Invariant);
                    if (column.Kind == CellKind.Number)
                    {
                        if (text != null && long.TryParse(text, NumberStyles.AllowLeadingSign, Invariant, out var number))
                        {
                            sheet.AppendFormat(Invariant, "<c r=\"{0}\"><v>{1}</v></c>", cell, number.ToString(Invariant));
                        }
                        continue;
                    }
                    if (column.Kind == CellKind.UtcDate || column.Kind == CellKind.LocalDate)
                    {
                        var styles = column.Kind == CellKind.UtcDate ? DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal : DateTimeStyles.None;
                        if (!string.IsNullOrEmpty(text) && DateTime.TryParseExact(text, ImeTimestampFormats, Invariant, styles, out var parsed))
                        {
                            if (column.Kind == CellKind.UtcDate) { parsed = parsed.ToLocalTime(); }
                            sheet.AppendFormat(Invariant, "<c r=\"{0}\" s=\"1\"><v>{1}</v></c>", cell, parsed.ToOADate().ToString("R", Invariant));
                            continue;
                        }
                    }
                    if (string.IsNullOrEmpty(text)) { continue; }
                    var style = column.Kind == CellKind.Verdict && shade ? " s=\"2\"" : "";
                    sheet.AppendFormat(Invariant, "<c r=\"{0}\"{1} t=\"inlineStr\"><is><t xml:space=\"preserve\">{2}</t></is></c>", cell, style, XmlText(text));
                }
                sheet.Append("</row>");
            }
            sheet.Append("</sheetData>");

            if (hasTable)
            {
                tableCount++;
                sheet.Append("<tableParts count=\"1\"><tablePart r:id=\"rId1\"/></tableParts>");
                var tableRef = string.Format(Invariant, "A1:{0}{1}", ColumnName(columns.Count), rowNumber);
                var table = new StringBuilder();
                table.AppendFormat(Invariant, "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><table xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" id=\"{0}\" name=\"{1}\" displayName=\"{1}\" ref=\"{2}\" totalsRowShown=\"0\"><autoFilter ref=\"{2}\"/><tableColumns count=\"{3}\">",
                    tableCount, def.TableName, tableRef, columns.Count);
                for (var c = 1; c <= columns.Count; c++)
                {
                    table.AppendFormat(Invariant, "<tableColumn id=\"{0}\" name=\"{1}\"/>", c, XmlText(columns[c - 1].Header));
                }
                table.Append("</tableColumns><tableStyleInfo name=\"TableStyleMedium3\" showFirstColumn=\"0\" showLastColumn=\"0\" showRowStripes=\"1\" showColumnStripes=\"0\"/></table>");
                parts.Add(new(string.Format(Invariant, "xl/worksheets/_rels/sheet{0}.xml.rels", s),
                    string.Format(Invariant, "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/table\" Target=\"../tables/table{0}.xml\"/></Relationships>", tableCount)));
                parts.Add(new(string.Format(Invariant, "xl/tables/table{0}.xml", tableCount), table.ToString()));
                overrides.AppendFormat(Invariant, "<Override PartName=\"/xl/tables/table{0}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.table+xml\"/>", tableCount);
            }
            sheet.Append("</worksheet>");
            parts.Add(new(string.Format(Invariant, "xl/worksheets/sheet{0}.xml", s), sheet.ToString()));
            sheetList.AppendFormat(Invariant, "<sheet name=\"{0}\" sheetId=\"{1}\" r:id=\"rId{1}\"/>", XmlText(def.Name), s);
            workbookRels.AppendFormat(Invariant, "<Relationship Id=\"rId{0}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{0}.xml\"/>", s);
            overrides.AppendFormat(Invariant, "<Override PartName=\"/xl/worksheets/sheet{0}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>", s);
        }

        var package = new List<KeyValuePair<string, string>>
        {
            new("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" + overrides + "</Types>"),
            new("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            new("xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" + sheetList + "</sheets></workbook>"),
            new("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + workbookRels
                + string.Format(Invariant, "<Relationship Id=\"rId{0}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>", sheets.Count + 1)),
            // Style 1: built-in number format 22, date and time in the reader's own locale.
            // Style 2: the shaded verdict cell of a record that needs attention.
            new("xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font></fonts><fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFCE4D6\"/><bgColor indexed=\"64\"/></patternFill></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"3\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"22\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/><xf numFmtId=\"0\" fontId=\"0\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFill=\"1\"/></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles><dxfs count=\"0\"/><tableStyles count=\"0\" defaultTableStyle=\"TableStyleMedium2\" defaultPivotStyle=\"PivotStyleLight16\"/></styleSheet>"),
        };
        package.AddRange(parts);

        var utf8 = new UTF8Encoding(false);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var part in package)
        {
            var bytes = utf8.GetBytes(part.Value);
            using var stream = zip.CreateEntry(part.Key).Open();
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}

/// <summary>
/// CSV as Windows PowerShell's ConvertTo-Csv -NoTypeInformation wrote it: every value in quotes
/// (inner quotes doubled), an empty field for null, numbers in invariant digits, written as UTF-8
/// with a BOM so Excel shows accents. A text cell that could start a formula when the file is
/// opened in Excel (=, +, -, @, tab or CR first) gets a leading apostrophe: app names and versions
/// come from the tenant, not from this product.
/// </summary>
public static class Csv
{
    private const string FormulaStart = "=+-@\t\r";

    public static string Field(object value)
    {
        if (value == null) { return ""; }
        string text;
        if (value is string s)
        {
            text = s.Length > 0 && FormulaStart.IndexOf(s[0]) >= 0 ? "'" + s : s;
        }
        else if (value is bool b)
        {
            text = b ? "True" : "False";
        }
        else
        {
            text = Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Writes a header and one line per row; the header alone for no rows.</summary>
    public static void Write(string path, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object>> rows)
    {
        var lines = new List<string> { string.Join(",", headers.Select(h => "\"" + h + "\"")) };
        lines.AddRange(rows.Select(r => string.Join(",", r.Select(Field))));
        File.WriteAllLines(path, lines, new UTF8Encoding(true));
    }
}
