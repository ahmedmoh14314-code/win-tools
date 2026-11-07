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
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            for (var k = 0; k < width; k++)
            {
                if (k > 0) text.Append(separator);
                var v = k < row.Length ? row[k] : "";
                if (NeedsQuotes(v, separator)) text.Append('"').Append(v.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                else text.Append(v);
            }
            text.Append("\r\n");
        }
        return [0xEF, 0xBB, 0xBF, .. new UTF8Encoding(false).GetBytes(text.ToString())];
    }

    private static bool NeedsQuotes(string v, char separator) =>
        v.Length > 0 && (v.AsSpan().IndexOfAny(separator, '"', '\r') >= 0 || v.Contains('\n', StringComparison.Ordinal) || char.IsWhiteSpace(v[0]) || char.IsWhiteSpace(v[^1]));

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
