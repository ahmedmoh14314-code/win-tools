using System.Globalization;
using System.Text;

namespace WinTools.Worker.Text;

/// <summary>A table read from a file or a sheet: rows of cell text, every row as wide as the cells it holds.</summary>
internal sealed record Table(IReadOnlyList<string[]> Rows, char Separator)
{
    public int Width => Rows.Count == 0 ? 0 : Rows.Max(r => r.Length);

    public bool HasData => Rows.Any(r => r.Any(c => c.Length > 0));
}

/// <summary>
/// CSV and TSV as RFC 4180 writes them (ADR-0033): fields separated by one character, a field in double quotes may hold
/// the separator, line breaks and doubled quotes, and a record ends at CRLF, LF or CR. A quote inside an unquoted field
/// is kept as it is (what Excel does); a quoted field never closed is an error, not a guess. Excel's limits are checked
/// while reading, so a table Excel cannot hold is refused rather than cut.
/// </summary>
internal static class Csv
{
    /// <summary>Excel's limits: rows and columns of a sheet, characters in a cell.</summary>
    public const int MaxRows = 1_048_576, MaxColumns = 16_384, MaxCellChars = 32_767;

    /// <summary>The most characters one CSV may hold (the host reads each file again to check it).</summary>
    public const int MaxWrittenChars = 200_000_000;

    /// <summary>A table that could not be read, with the reason in the user's words.</summary>
    public sealed class Problem(string message, bool limit = false) : Exception(message)
    {
        public bool Limit { get; } = limit;
    }

    /// <summary>
    /// The separator of a CSV file: Excel's "sep=" first line when there is one, otherwise whichever of comma, semicolon
    /// and tab is most frequent outside quotes in the first record (a European Excel writes semicolons), comma on a tie.
    /// </summary>
    public static char DetectSeparator(string text, out bool sepLine)
    {
        sepLine = false;
        if (text.StartsWith("sep=", StringComparison.OrdinalIgnoreCase) && text.Length >= 5 && text[4] is ',' or ';' or '\t' or '|'
            && (text.Length == 5 || text[5] is '\r' or '\n'))
        {
            sepLine = true;
            return text[4];
        }
        int comma = 0, semicolon = 0, tab = 0;
        var quoted = false;
        foreach (var c in text.AsSpan(0, Math.Min(text.Length, 1 << 20)))
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && c is '\r' or '\n') break;
            else if (!quoted && c == ',') comma++;
            else if (!quoted && c == ';') semicolon++;
            else if (!quoted && c == '\t') tab++;
        }
        if (semicolon > comma && semicolon >= tab) return ';';
        if (tab > comma && tab > semicolon) return '\t';
        return ',';
    }

    /// <summary>Parses the whole text with the separator given. A last empty line is not a row.</summary>
    public static Table Parse(string text, char separator, bool skipSepLine = false)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var i = 0;
        if (skipSepLine)
        {
            while (i < text.Length && text[i] is not ('\r' or '\n')) i++;
            if (i < text.Length && text[i] == '\r') i++;
            if (i < text.Length && text[i] == '\n') i++;
        }
        var line = 1;
        var fieldStarted = false;
        void EndField()
        {
            if (field.Length > MaxCellChars) throw new Problem($"A cell on line {line:N0} holds more than {MaxCellChars:N0} characters, which Excel cannot hold.", limit: true);
            row.Add(field.ToString());
            field.Clear();
            if (row.Count > MaxColumns) throw new Problem($"Line {line:N0} has more than {MaxColumns:N0} columns, which Excel cannot hold.", limit: true);
        }
        void EndRow()
        {
            EndField();
            rows.Add([.. row]);
            row.Clear();
            if (rows.Count > MaxRows) throw new Problem($"The table has more than {MaxRows:N0} rows, which Excel cannot hold.", limit: true);
            fieldStarted = false;
        }
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"' && field.Length == 0 && !fieldStarted)
            {
                // A quoted field: up to the closing quote; "" is one quote, and line breaks belong to the field.
                var start = line;
                i++;
                while (true)
                {
                    if (i >= text.Length) throw new Problem($"A quoted field that starts on line {start:N0} is never closed.");
                    var q = text[i];
                    if (q == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                        i++;
                        break;
                    }
                    if (q == '\n' || (q == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'))) line++;
                    field.Append(q);
                    i++;
                }
                fieldStarted = true;
                continue; // whatever follows the closing quote up to the separator is kept, as Excel does
            }
            if (c == separator) { EndField(); fieldStarted = false; i++; continue; }
            if (c is '\r' or '\n')
            {
                EndRow();
                i += c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                line++;
                continue;
            }
            field.Append(c);
            fieldStarted = true;
            i++;
        }
        if (field.Length > 0 || row.Count > 0 || fieldStarted) EndRow();
        return new Table(rows, separator);
    }

    /// <summary>
    /// The table as CSV: UTF-8 with a BOM (Excel reads UTF-8 only with one), CRLF, every row as wide as the widest, a field
    /// quoted when it holds the separator, a quote, a line break, or space at either end.
    /// </summary>
    public static byte[] Write(IReadOnlyList<string[]> rows, char separator = ',')
    {
        var width = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
        // One cell at the far corner of a sheet would pad a million rows to 16,384 columns: refused before any is written.
        if (width * (long)rows.Count > MaxWrittenChars) throw new Problem("A sheet is too large to write as CSV.", limit: true);
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            for (var k = 0; k < width; k++)
            {
                if (text.Length > MaxWrittenChars) throw new Problem("A sheet is too large to write as CSV.", limit: true); // one row of long cells
                if (k > 0) text.Append(separator);
                var v = k < row.Length ? row[k] : "";
                if (NeedsQuotes(v, separator)) text.Append('"').Append(v.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                else text.Append(v);
            }
            text.Append("\r\n");
        }
        return [0xEF, 0xBB, 0xBF, .. new UTF8Encoding(false).GetBytes(text.ToString())];
    }

    /// <summary>
    /// Also when it holds a semicolon or a tab: Excel with another list separator would split the field there, and what
    /// follows ("x;=cmd|...") would be a cell of its own that no guard saw.
    /// </summary>
    private static bool NeedsQuotes(string v, char separator) =>
        v.Length > 0 && (v.AsSpan().IndexOfAny(separator, '"', '\r') >= 0 || v.AsSpan().IndexOfAny('\n', ';', '\t') >= 0 || char.IsWhiteSpace(v[0]) || char.IsWhiteSpace(v[^1]));

    /// <summary>See <see cref="WinTools.Worker.Common.XlsxWriter.IsPlainNumber"/>.</summary>
    public static bool IsPlainNumber(string v) => WinTools.Worker.Common.XlsxWriter.IsPlainNumber(v);
}
