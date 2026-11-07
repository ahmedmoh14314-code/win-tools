using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace WinTools.Worker.Text;

/// <summary>
/// Writes a one-sheet Excel workbook from a table (ADR-0033): the few parts Excel needs, written with an XmlWriter, so
/// every cell's text is escaped by the writer, never pasted into markup. A cell is a number only when
/// <see cref="Csv.IsPlainNumber"/> says so; anything else is inline text, so "=SUM(...)" in a CSV is text in the workbook,
/// never a formula. A sheet whose text is mostly right-to-left (Arabic, Hebrew) opens right to left.
/// </summary>
internal static class XlsxWriter
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>The workbook's bytes, and how many characters XML cannot hold were replaced (U+FFFD).</summary>
    public static (byte[] Bytes, int Replaced) Build(IReadOnlyList<string[]> rows, string sheetName)
    {
        var replaced = 0;
        var rtl = IsMostlyRightToLeft(rows);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Part(zip, "[Content_Types].xml", w =>
            {
                w.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
                Default(w, "rels", "application/vnd.openxmlformats-package.relationships+xml");
                Default(w, "xml", "application/xml");
                Override(w, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
                Override(w, "/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
                Override(w, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
                w.WriteEndElement();
            });
            Part(zip, "_rels/.rels", w =>
            {
                w.WriteStartElement("Relationships", PackageRel);
                Relationship(w, "rId1", Rel + "/officeDocument", "xl/workbook.xml");
                w.WriteEndElement();
            });
            Part(zip, "xl/workbook.xml", w =>
            {
                w.WriteStartElement("workbook", Main);
                w.WriteAttributeString("xmlns", "r", null, Rel);
                w.WriteStartElement("sheets", Main);
                w.WriteStartElement("sheet", Main);
                w.WriteAttributeString("name", sheetName);
                w.WriteAttributeString("sheetId", "1");
                w.WriteAttributeString("id", Rel, "rId1");
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();
            });
            Part(zip, "xl/_rels/workbook.xml.rels", w =>
            {
                w.WriteStartElement("Relationships", PackageRel);
                Relationship(w, "rId1", Rel + "/worksheet", "worksheets/sheet1.xml");
                Relationship(w, "rId2", Rel + "/styles", "styles.xml");
                w.WriteEndElement();
            });
            Part(zip, "xl/styles.xml", w =>
            {
                w.WriteStartElement("styleSheet", Main);
                w.WriteRaw("""<fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>""");
                w.WriteEndElement();
            });
            Part(zip, "xl/worksheets/sheet1.xml", w =>
            {
                w.WriteStartElement("worksheet", Main);
                if (rtl)
                {
                    w.WriteStartElement("sheetViews", Main);
                    w.WriteStartElement("sheetView", Main);
                    w.WriteAttributeString("rightToLeft", "1");
                    w.WriteAttributeString("workbookViewId", "0");
                    w.WriteEndElement();
                    w.WriteEndElement();
                }
                w.WriteStartElement("sheetData", Main);
                for (var r = 0; r < rows.Count; r++)
                {
                    var row = rows[r];
                    if (row.All(c => c.Length == 0)) continue; // an empty row is left out; the next one keeps its number
                    w.WriteStartElement("row", Main);
                    w.WriteAttributeString("r", (r + 1).ToString(CultureInfo.InvariantCulture));
                    for (var k = 0; k < row.Length; k++)
                    {
                        if (row[k].Length == 0) continue;
                        w.WriteStartElement("c", Main);
                        w.WriteAttributeString("r", Reference(k, r));
                        if (Csv.IsPlainNumber(row[k]))
                        {
                            w.WriteElementString("v", Main, row[k]);
                        }
                        else
                        {
                            w.WriteAttributeString("t", "inlineStr");
                            w.WriteStartElement("is", Main);
                            w.WriteStartElement("t", Main);
                            w.WriteAttributeString("xml", "space", null, "preserve");
                            w.WriteString(XmlSafe(row[k], ref replaced));
                            w.WriteEndElement();
                            w.WriteEndElement();
                        }
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                w.WriteEndElement();
            });
        }
        return (buffer.ToArray(), replaced);
    }

    /// <summary>The cell text the workbook holds: characters XML cannot carry (most C0 controls) become U+FFFD.</summary>
    public static string XmlSafe(string text, ref int replaced)
    {
        StringBuilder? safe = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var ok = char.IsSurrogatePair(text, i) || (!char.IsSurrogate(c) && XmlConvert.IsXmlChar(c));
            if (ok && char.IsHighSurrogate(c)) { safe?.Append(c).Append(text[i + 1]); i++; continue; }
            if (!ok)
            {
                safe ??= new StringBuilder(text, 0, i, text.Length);
                safe.Append('�');
                replaced++;
            }
            else safe?.Append(c);
        }
        return safe?.ToString() ?? text;
    }

    /// <summary>The sheet's name from the file's: at most 31 characters, none of []:*?/\, never empty or Excel's "History".</summary>
    public static string SheetName(string stem)
    {
        var name = new StringBuilder();
        foreach (var rune in stem.EnumerateRunes())
        {
            if (rune.IsAscii && "[]:*?/\\".Contains((char)rune.Value, StringComparison.Ordinal)) continue;
            if (Rune.IsControl(rune)) continue;
            if (name.Length + rune.Utf16SequenceLength > 31) break;
            name.Append(rune.ToString());
        }
        var result = name.ToString().Trim().Trim('\'').Trim();
        return result.Length == 0 || string.Equals(result, "History", StringComparison.OrdinalIgnoreCase) ? "Sheet1" : result;
    }

    /// <summary>"A1", "AB12"...: the cell reference of a zero-based column and row.</summary>
    public static string Reference(int column, int row)
    {
        Span<char> letters = stackalloc char[3];
        var n = 0;
        for (var c = column + 1; c > 0; c = (c - 1) / 26) letters[n++] = (char)('A' + (c - 1) % 26);
        letters[..n].Reverse();
        return string.Concat(letters[..n], (row + 1).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>More right-to-left letters (Arabic, Hebrew, Syriac, Thaana) than left-to-right ones in the first 1,000 rows.</summary>
    public static bool IsMostlyRightToLeft(IReadOnlyList<string[]> rows)
    {
        long rtl = 0, ltr = 0;
        foreach (var row in rows.Take(1000))
            foreach (var cell in row)
                foreach (var c in cell)
                {
                    if (!char.IsLetter(c)) continue;
                    if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿') rtl++;
                    else ltr++;
                }
        return rtl > ltr;
    }

    private static void Part(ZipArchive zip, string name, Action<XmlWriter> write)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var w = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CheckCharacters = true, NewLineHandling = NewLineHandling.None });
        w.WriteStartDocument(standalone: true);
        write(w);
        w.WriteEndDocument();
    }

    private static void Default(XmlWriter w, string extension, string type)
    {
        w.WriteStartElement("Default", "http://schemas.openxmlformats.org/package/2006/content-types");
        w.WriteAttributeString("Extension", extension);
        w.WriteAttributeString("ContentType", type);
        w.WriteEndElement();
    }

    private static void Override(XmlWriter w, string part, string type)
    {
        w.WriteStartElement("Override", "http://schemas.openxmlformats.org/package/2006/content-types");
        w.WriteAttributeString("PartName", part);
        w.WriteAttributeString("ContentType", type);
        w.WriteEndElement();
    }

    private static void Relationship(XmlWriter w, string id, string type, string target)
    {
        w.WriteStartElement("Relationship", PackageRel);
        w.WriteAttributeString("Id", id);
        w.WriteAttributeString("Type", type);
        w.WriteAttributeString("Target", target);
        w.WriteEndElement();
    }
}

/// <summary>
/// Reads an Excel workbook's sheets as tables for To CSV (ADR-0033), on the documents module's package rules (ADR-0025):
/// the sheets in the workbook's own order and names (through its relationships), each cell as Excel stores it.
/// Text cells (shared, inline, formula text), numbers, booleans and errors are written as their values: a number as a
/// plain number ("0.25", not "25%"), a date (a number in a date format) as ISO 8601, a formula as its last calculated
/// value. Formulas are never evaluated and nothing outside the package is read.
/// </summary>
internal static class WorkbookReader
{
    public sealed record Sheet(string Name, string Part);

    /// <summary>What the cells of a workbook need: its shared strings, which styles are dates, and its date system.</summary>
    public sealed record Context(IReadOnlyList<string> Shared, IReadOnlySet<int> DateStyles, IReadOnlySet<int> TimeOnlyStyles, bool Date1904);

    private static readonly Uri WorkbookUri = new("http://package/xl/workbook.xml");

    /// <summary>The worksheets in the workbook's order, or the numbered parts ("Sheet 1"...) when it lists none.</summary>
    public static IReadOnlyList<Sheet> Sheets(ZipArchive zip)
    {
        var rels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (DocumentJobs.ReadPart(zip, "xl/_rels/workbook.xml.rels") is { } relsXml)
        {
            using var r = DocumentJobs.Xml(relsXml);
            while (r.Read())
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "Relationship" && (r.GetAttribute("Type") ?? "").EndsWith("/worksheet", StringComparison.Ordinal)
                    && !string.Equals(r.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)
                    && r.GetAttribute("Id") is { } id && r.GetAttribute("Target") is { Length: > 0 } target
                    && Uri.TryCreate(WorkbookUri, target, out var uri) && uri.Host == "package")
                    rels.TryAdd(id, Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')));
        }
        var sheets = new List<Sheet>();
        if (DocumentJobs.ReadPart(zip, "xl/workbook.xml") is { } wb)
        {
            using var r = DocumentJobs.Xml(wb);
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element || r.LocalName != "sheet") continue;
                string? rid = null;
                for (var i = 0; i < r.AttributeCount; i++)
                {
                    r.MoveToAttribute(i);
                    if (r.LocalName == "id" && r.NamespaceURI.EndsWith("/relationships", StringComparison.Ordinal)) rid = r.Value;
                }
                r.MoveToElement();
                if (rid is not null && rels.TryGetValue(rid, out var part) && DocumentJobs.Find(zip, part) is { } entry)
                    sheets.Add(new(r.GetAttribute("name") is { Length: > 0 } n ? n : $"Sheet {sheets.Count + 1}", entry.FullName));
            }
        }
        if (sheets.Count == 0)
            foreach (var (n, name) in DocumentJobs.Numbered(zip, "xl/worksheets/sheet")) sheets.Add(new($"Sheet {n}", name));
        return sheets;
    }

    public static Context ContextOf(ZipArchive zip)
    {
        var shared = new List<string>();
        if (DocumentJobs.ReadPart(zip, "xl/sharedStrings.xml") is { } sst)
        {
            using var r = DocumentJobs.Xml(sst);
            StringBuilder? item = null;
            var onNext = false;
            while (onNext || r.Read())
            {
                onNext = false;
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "si") { item = new StringBuilder(); if (r.IsEmptyElement) { shared.Add(""); item = null; } }
                else if (r.NodeType == XmlNodeType.Element && r.LocalName == "rPh" && !r.IsEmptyElement) { r.Skip(); onNext = true; } // phonetic guide, not the text
                else if (r.NodeType == XmlNodeType.Element && r.LocalName == "t" && item is not null && !r.IsEmptyElement) { item.Append(r.ReadElementContentAsString()); onNext = true; }
                else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "si" && item is not null) { shared.Add(item.ToString()); item = null; }
            }
        }
        var formats = new Dictionary<int, string>();
        var dates = new HashSet<int>();
        var times = new HashSet<int>();
        if (DocumentJobs.ReadPart(zip, "xl/styles.xml") is { } styles)
        {
            using (var r = DocumentJobs.Xml(styles))
                while (r.Read())
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "numFmt"
                        && int.TryParse(r.GetAttribute("numFmtId"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && r.GetAttribute("formatCode") is { } code)
                        formats[id] = code;
            using (var r = DocumentJobs.Xml(styles))
            {
                var inCellXfs = false;
                var index = 0;
                while (r.Read())
                {
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "cellXfs") inCellXfs = !r.IsEmptyElement;
                    else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "cellXfs") inCellXfs = false;
                    else if (inCellXfs && r.NodeType == XmlNodeType.Element && r.LocalName == "xf")
                    {
                        if (int.TryParse(r.GetAttribute("numFmtId"), NumberStyles.None, CultureInfo.InvariantCulture, out var fmt))
                        {
                            var kind = DateKind(fmt, formats.GetValueOrDefault(fmt));
                            if (kind != 0) dates.Add(index);
                            if (kind == 2) times.Add(index);
                        }
                        index++;
                    }
                }
            }
        }
        var date1904 = false;
        if (DocumentJobs.ReadPart(zip, "xl/workbook.xml") is { } wb)
        {
            using var r = DocumentJobs.Xml(wb);
            while (r.Read())
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "workbookPr")
                    date1904 = r.GetAttribute("date1904") is "1" or "true";
        }
        return new(shared, dates, times, date1904);
    }

    /// <summary>0: not a date; 1: a date or date and time; 2: a time of day only. Built-in ids per ECMA-376 §18.8.30.</summary>
    internal static int DateKind(int id, string? code)
    {
        if (id is >= 14 and <= 17 or 22 or >= 27 and <= 31 or >= 34 and <= 36 or >= 50 and <= 58) return 1;
        if (id is >= 18 and <= 21 or 32 or 33 or 45 or 46 or 47) return 2;
        if (code is null) return 0;
        // Quoted text, escaped characters and [colour]/[condition] sections are not date parts; [h] or [mm] are durations.
        var plain = new StringBuilder();
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '"') { var end = code.IndexOf('"', i + 1); if (end < 0) break; i = end; continue; }
            if (c is '\\' or '_' or '*') { i++; continue; }
            if (c == '[')
            {
                var end = code.IndexOf(']', i + 1);
                if (end < 0) break;
                if (code.AsSpan(i + 1, end - i - 1).Trim().ToString().ToLowerInvariant() is ['h', ..] or ['m', ..] or ['s', ..]) return 0;
                i = end;
                continue;
            }
            plain.Append(char.ToLowerInvariant(c));
        }
        var p = plain.ToString();
        var date = p.AsSpan().IndexOfAny("dy") >= 0 || (p.Contains('m', StringComparison.Ordinal) && !p.Contains('h', StringComparison.Ordinal) && !p.Contains('s', StringComparison.Ordinal));
        var time = p.AsSpan().IndexOfAny("hs") >= 0;
        return date ? 1 : time ? 2 : 0;
    }

    /// <summary>
    /// The sheet's rows as CSV text, every gap kept (a missing row is an empty row, a missing cell an empty field).
    /// <paramref name="raw"/>: numbers as stored, for checking a workbook this module wrote.
    /// </summary>
    public static List<string[]> Rows(string xml, Context context, bool raw = false)
    {
        var rows = new List<string[]>();
        using var r = DocumentJobs.Xml(xml);
        SortedDictionary<int, string>? cells = null;
        int rowNumber = 0, nextRow = 1, column = 0, nextColumn = 0, style = 0;
        string? type = null;
        StringBuilder? inline = null;
        var onNext = false;
        void Flush()
        {
            while (nextRow < rowNumber)
            {
                if (rows.Count >= Csv.MaxRows) throw new Csv.Problem("A sheet has more rows than Excel allows.", limit: true);
                rows.Add([]);
                nextRow++;
            }
            var width = cells!.Count == 0 ? 0 : cells.Keys.Max() + 1;
            if (width > Csv.MaxColumns) throw new Csv.Problem("A row is wider than a worksheet can be.", limit: true);
            rows.Add([.. Enumerable.Range(0, width).Select(k => cells.TryGetValue(k, out var c) ? c : "")]);
            if (rows.Count > Csv.MaxRows) throw new Csv.Problem("A sheet has more rows than Excel allows.", limit: true);
            nextRow = rowNumber + 1;
        }
        while (onNext || r.Read())
        {
            onNext = false;
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "row")
            {
                cells = [];
                nextColumn = 0;
                rowNumber = int.TryParse(r.GetAttribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= nextRow && n <= Csv.MaxRows ? n : nextRow;
                if (r.IsEmptyElement) { Flush(); cells = null; }
            }
            else if (r.NodeType == XmlNodeType.Element && r.LocalName == "c" && cells is not null)
            {
                type = r.GetAttribute("t");
                style = int.TryParse(r.GetAttribute("s"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
                column = DocumentJobs.ColumnOf(r.GetAttribute("r")) ?? nextColumn;
                nextColumn = column + 1;
            }
            else if (r.NodeType == XmlNodeType.Element && r.LocalName == "f" && !r.IsEmptyElement) { r.Skip(); onNext = true; } // the formula itself is never read
            else if (r.NodeType == XmlNodeType.Element && r.LocalName == "v" && cells is not null)
            {
                var v = r.ReadElementContentAsString();
                onNext = true;
                cells[column] = type switch
                {
                    "s" => int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < context.Shared.Count ? context.Shared[i] : "",
                    "b" => v.Trim() == "1" ? "TRUE" : "FALSE",
                    "str" or "e" or "d" or "inlineStr" => v,
                    _ => raw ? v : Number(v, style, context),
                };
            }
            else if (r.NodeType == XmlNodeType.Element && r.LocalName == "is" && cells is not null) { inline = new StringBuilder(); if (r.IsEmptyElement) { cells[column] = ""; inline = null; } }
            else if (r.NodeType == XmlNodeType.Element && r.LocalName == "rPh" && inline is not null && !r.IsEmptyElement) { r.Skip(); onNext = true; }
            else if (r.NodeType == XmlNodeType.Element && r.LocalName == "t" && inline is not null && !r.IsEmptyElement) { inline.Append(r.ReadElementContentAsString()); onNext = true; }
            else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "is" && inline is not null && cells is not null) { cells[column] = inline.ToString(); inline = null; }
            else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "row" && cells is not null) { Flush(); cells = null; }
        }
        while (rows.Count > 0 && rows[^1].All(c => c.Length == 0)) rows.RemoveAt(rows.Count - 1); // trailing empty rows say nothing
        return rows;
    }

    /// <summary>A stored number as CSV text: a date in ISO 8601 when its style is a date format, otherwise the plain value.</summary>
    internal static string Number(string stored, int style, Context context)
    {
        if (!double.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d)) return stored;
        if (context.DateStyles.Contains(style) && d >= 0 && d < 2958466) // up to 9999-12-31
        {
            var day = Math.Floor(d);
            var seconds = Math.Round((d - day) * 86400);
            if (seconds >= 86400) { day++; seconds = 0; }
            var time = TimeSpan.FromSeconds(seconds);
            if (context.TimeOnlyStyles.Contains(style) && day == 0) return time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            // 1900 system: serial 1 is 1900-01-01 and Excel counts a 29 February 1900 that never was (serial 60).
            var origin = context.Date1904 ? new DateTime(1904, 1, 1) : day < 60 ? new DateTime(1899, 12, 31) : new DateTime(1899, 12, 30);
            if (!context.Date1904 && day == 60) return stored; // the day that never was: the number, not a false date
            var when = origin.AddDays(day).Add(time);
            return seconds == 0 && !context.TimeOnlyStyles.Contains(style)
                ? when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("G15", CultureInfo.InvariantCulture);
    }
}
