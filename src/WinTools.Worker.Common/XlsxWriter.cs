using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace WinTools.Worker.Common;

/// <summary>
/// Writes a one-sheet Excel workbook from a table (ADR-0033): the few parts Excel needs, written with an XmlWriter, so
/// every cell's text is escaped by the writer, never pasted into markup. A cell is a number only when
/// <see cref="IsPlainNumber"/> says so; anything else is inline text, so "=SUM(...)" in a CSV is text in the workbook,
/// never a formula. A sheet whose text is mostly right-to-left (Arabic, Hebrew) opens right to left. Columns are as wide
/// as their longest line, up to <see cref="MaxColumnWidth"/>, and a longer cell wraps: at the default width, text would be
/// cut at the next cell and a long number shown as ### in Excel and in a PDF made from the sheet.
/// </summary>
public static class XlsxWriter
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>The widest column, in characters.</summary>
    private const int MaxColumnWidth = 60;

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
                w.WriteRaw("""<fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>""");
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
                if (ColumnWidths(rows) is { Length: > 0 } widths)
                {
                    w.WriteStartElement("cols", Main);
                    for (var k = 0; k < widths.Length; k++)
                    {
                        w.WriteStartElement("col", Main);
                        var column = (k + 1).ToString(CultureInfo.InvariantCulture);
                        w.WriteAttributeString("min", column);
                        w.WriteAttributeString("max", column);
                        w.WriteAttributeString("width", widths[k].ToString(CultureInfo.InvariantCulture));
                        w.WriteAttributeString("customWidth", "1");
                        w.WriteEndElement();
                    }
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
                        if (IsPlainNumber(row[k]))
                        {
                            w.WriteElementString("v", Main, row[k]);
                        }
                        else
                        {
                            if (LongestLine(row[k]) > MaxColumnWidth || row[k].Contains('\n', StringComparison.Ordinal)) w.WriteAttributeString("s", "1");
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

    /// <summary>Each column's width: its longest line and a little air, at least Excel's usual width, at most the cap.</summary>
    private static int[] ColumnWidths(IReadOnlyList<string[]> rows)
    {
        var longest = new int[rows.Count == 0 ? 0 : rows.Max(r => r.Length)];
        foreach (var row in rows)
            for (var k = 0; k < row.Length; k++) longest[k] = Math.Max(longest[k], LongestLine(row[k]));
        return [.. longest.Select(n => Math.Clamp(n + 2, 9, MaxColumnWidth))];
    }

    private static int LongestLine(string cell)
    {
        var longest = 0;
        foreach (var line in cell.AsSpan().EnumerateLines()) longest = Math.Max(longest, line.Length);
        return longest;
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

    /// <summary>
    /// Whether a CSV field becomes a number in the workbook: an optional minus, digits without a leading zero (so "007"
    /// and postcodes stay text), an optional point and digits, and at most 15 significant digits (Excel keeps no more, so
    /// a card or ID number stays text). Thousands separators, exponents, currency and dates stay text: nothing is guessed.
    /// </summary>
    public static bool IsPlainNumber(string v)
    {
        if (v.Length is 0 or > 32) return false;
        var s = v.AsSpan();
        if (s[0] == '-') s = s[1..];
        if (s.Length == 0 || !char.IsAsciiDigit(s[0])) return false;
        var dot = s.IndexOf('.');
        var whole = dot < 0 ? s : s[..dot];
        var fraction = dot < 0 ? [] : s[(dot + 1)..];
        if (whole.Length == 0 || (whole.Length > 1 && whole[0] == '0') || whole.ContainsAnyExceptInRange('0', '9')) return false;
        if (dot >= 0 && (fraction.Length == 0 || fraction.ContainsAnyExceptInRange('0', '9'))) return false;
        var digits = (whole.TrimStart('0').ToString() + fraction.ToString()).TrimStart('0').TrimEnd('0').Length;
        return digits <= 15 && double.TryParse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d);
    }
}
