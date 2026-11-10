using System.Runtime.InteropServices;

namespace WinTools.Worker.Pdf;

internal static partial class Pdfium
{
    [LibraryImport(Lib)] public static partial uint FPDFText_GetUnicode(nint textPage, int index);
    [LibraryImport(Lib)] public static partial int FPDFText_GetCharBox(nint textPage, int index, out double left, out double right, out double bottom, out double top);
    [LibraryImport(Lib)] public static partial int FPDFText_IsGenerated(nint textPage, int index);
    [LibraryImport(Lib)] public static partial int FPDFPage_CountObjects(nint page);
    [LibraryImport(Lib)] public static partial nint FPDFPage_GetObject(nint page, int index);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetType(nint pageObject);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetMatrix(nint pageObject, out Matrix matrix);
    [LibraryImport(Lib)] public static partial int FPDFPath_CountSegments(nint path);
    [LibraryImport(Lib)] public static partial nint FPDFPath_GetPathSegment(nint path, int index);
    [LibraryImport(Lib)] public static partial int FPDFPathSegment_GetPoint(nint segment, out float x, out float y);
    [LibraryImport(Lib)] public static partial int FPDFPathSegment_GetType(nint segment);

    public const int ObjectPath = 2, SegmentLineTo = 0; // FPDF_SEGMENT_LINETO (BEZIERTO is 1, MOVETO 2)

    [StructLayout(LayoutKind.Sequential)]
    public struct Matrix { public float A, B, C, D, E, F; }
}

/// <summary>A vertical line drawn on a page (a table's column border), in points.</summary>
internal readonly record struct Rule(double X, double Bottom, double Top);

/// <summary>One character PDFium read on a page, with its box in points (y up).</summary>
internal readonly record struct Glyph(char Char, double Left, double Right, double Bottom, double Top, bool Generated = false)
{
    public double Height => Top - Bottom;
    public double Middle => (Top + Bottom) / 2;
}

internal sealed unsafe partial class PdfDocument
{
    /// <summary>The characters of a page with their boxes, at most <paramref name="max"/>; null when unreadable.</summary>
    public List<Glyph>? Glyphs(int index, int max)
    {
        lock (Pdfium.Gate)
        {
            var page = Pdfium.FPDF_LoadPage(_doc, index);
            if (page == 0) return null;
            var text = Pdfium.FPDFText_LoadPage(page);
            try
            {
                if (text == 0) return null;
                var count = Math.Min(Pdfium.FPDFText_CountChars(text), max);
                var glyphs = new List<Glyph>(Math.Max(0, count));
                for (var i = 0; i < count; i++)
                {
                    var code = Pdfium.FPDFText_GetUnicode(text, i);
                    if (code is 0 or > 0xFFFF) continue;
                    var generated = Pdfium.FPDFText_IsGenerated(text, i) == 1;
                    if (Pdfium.FPDFText_GetCharBox(text, i, out var l, out var r, out var b, out var t) == 0) { if (generated) glyphs.Add(new Glyph((char)code, 0, 0, 0, 0, true)); continue; }
                    glyphs.Add(new Glyph((char)code, l, r, b, t, generated));
                }
                return glyphs;
            }
            finally
            {
                if (text != 0) Pdfium.FPDFText_ClosePage(text);
                Pdfium.FPDF_ClosePage(page);
            }
        }
    }
}

internal sealed partial class PdfDocument
{
    /// <summary>The vertical lines drawn on a page (straight path segments at least 6 points tall), at most 20,000 segments read.</summary>
    public List<Rule> VerticalRules(int index)
    {
        var rules = new List<Rule>();
        lock (Pdfium.Gate)
        {
            var page = Pdfium.FPDF_LoadPage(_doc, index);
            if (page == 0) return rules;
            try
            {
                var budget = 20_000;
                var objects = Pdfium.FPDFPage_CountObjects(page);
                for (var o = 0; o < objects && budget > 0; o++)
                {
                    var obj = Pdfium.FPDFPage_GetObject(page, o);
                    if (obj == 0 || Pdfium.FPDFPageObj_GetType(obj) != Pdfium.ObjectPath || Pdfium.FPDFPageObj_GetMatrix(obj, out var m) == 0) continue;
                    var segments = Pdfium.FPDFPath_CountSegments(obj);
                    double px = 0, py = 0;
                    for (var i = 0; i < segments && budget-- > 0; i++)
                    {
                        var seg = Pdfium.FPDFPath_GetPathSegment(obj, i);
                        if (seg == 0 || Pdfium.FPDFPathSegment_GetPoint(seg, out var sx, out var sy) == 0) continue;
                        var (x, y) = ((double)m.A * sx + m.C * sy + m.E, (double)m.B * sx + m.D * sy + m.F);
                        if (Pdfium.FPDFPathSegment_GetType(seg) == Pdfium.SegmentLineTo && Math.Abs(x - px) < 1 && Math.Abs(y - py) >= 6)
                            rules.Add(new Rule((x + px) / 2, Math.Min(y, py), Math.Max(y, py)));
                        (px, py) = (x, y);
                    }
                }
            }
            finally { Pdfium.FPDF_ClosePage(page); }
        }
        return rules;
    }
}

/// <summary>
/// A page's text laid out again as a table (PDF to Excel): characters become words where they touch, words on one height
/// become a line, and a line splits into cells where a gap is wider than about two letters. Cells are put in columns by
/// where they start, so the columns of a real table line up. Right-to-left words (Arabic, Hebrew), which a PDF stores in
/// the order they are drawn, are turned back into reading order, and a mostly right-to-left page runs right to left.
/// This reads text only: a scan has none, and merged or ruled cells are not understood.
/// </summary>
internal static class PdfTables
{
    private sealed record Word(string Text, double Left, double Right, double Middle, double Height, bool Rtl);

    public static List<string[]> Rows(IReadOnlyList<Glyph> glyphs, IReadOnlyList<Rule>? rules = null)
    {
        var words = Words(glyphs);
        if (words.Count == 0) return [];
        var rtlPage = words.Count(w => w.Rtl) * 2 > words.Count;
        words = JoinTouching(words);
        // Lines: words whose middles are within half a letter's height, top of the page first.
        var lines = new List<List<Word>>();
        foreach (var w in words.OrderByDescending(w => w.Middle))
        {
            var line = lines.LastOrDefault();
            if (line is not null && Math.Abs(line.Average(x => x.Middle) - w.Middle) <= Math.Max(1, line.Average(x => x.Height)) * 0.5) line.Add(w);
            else lines.Add([w]);
        }
        // A ruled table: its vertical borders are the columns; each word goes between the borders around its middle.
        if (rules is { Count: >= 2 } && RuledRows(lines, rules, rtlPage) is { } ruled) return ruled;
        // Cells: a line split where the gap between two words is wider than about two letters.
        var cellLines = new List<List<(string Text, double Left, double Right)>>();
        foreach (var line in lines)
        {
            var ordered = line.OrderBy(w => w.Left).ToList();
            var cells = new List<(string Text, double Left, double Right)>();
            var current = new List<Word> { ordered[0] };
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Left - current[^1].Right > Math.Max(ordered[i].Height, current[^1].Height) * 1.2) { cells.Add(Cell(current, rtlPage)); current = []; }
                current.Add(ordered[i]);
            }
            cells.Add(Cell(current, rtlPage));
            cellLines.Add(cells);
        }
        // Columns: the bands of the page that cells of table lines (two cells or more) cover, separated by the empty
        // channels between them, so left-, right- and centre-aligned columns are all found.
        var spans = cellLines.Where(l => l.Count > 1).SelectMany(l => l.Select(c => (c.Left, c.Right))).OrderBy(c => c.Left).ToList();
        var bands = new List<(double Left, double Right)>();
        foreach (var (l, r) in spans)
        {
            if (bands.Count > 0 && l <= bands[^1].Right + 2) bands[^1] = (bands[^1].Left, Math.Max(bands[^1].Right, r));
            else bands.Add((l, r));
        }
        if (bands.Count == 0) bands.Add((double.MinValue, double.MaxValue));
        if (rtlPage) bands.Reverse();
        var rows = new List<string[]>();
        foreach (var cells in cellLines)
        {
            var row = new string?[bands.Count];
            foreach (var (text, left, right) in rtlPage ? cells.AsEnumerable().Reverse() : cells)
            {
                // A cell goes in the band holding its middle; a line that is not a table (one cell) starts in its first band.
                var column = cells.Count == 1 ? Band(bands, rtlPage ? right : left) : Band(bands, (left + right) / 2);
                row[column] = row[column] is null ? text : rtlPage ? text + " " + row[column] : row[column] + " " + text;
            }
            rows.Add([.. row.Select(c => c ?? "")]);
        }
        return rows;
    }

    /// <summary>
    /// Rows of a page whose lines cross vertical borders: the borders' places (within 2 points) cut the page into columns;
    /// a word in a line no border crosses (a title, a paragraph) stays in the first column. Null when no line is ruled.
    /// </summary>
    private static List<string[]>? RuledRows(List<List<Word>> lines, IReadOnlyList<Rule> rules, bool rtlPage)
    {
        var xs = new List<double>();
        foreach (var x in rules.Select(r => r.X).Order()) if (xs.Count == 0 || x - xs[^1] > 2) xs.Add(x);
        if (xs.Count < 2) return null;
        var columns = xs.Count + 1; // left of the first border, between borders, right of the last
        var rows = new List<string[]>();
        var anyRuled = false;
        foreach (var line in lines)
        {
            var middle = line.Average(w => w.Middle);
            var ruled = rules.Any(r => r.Bottom - 1 <= middle && middle <= r.Top + 1);
            anyRuled |= ruled;
            var row = new string?[columns];
            foreach (var w in rtlPage ? line.OrderByDescending(w => w.Right) : line.OrderBy(w => w.Left))
            {
                var center = (w.Left + w.Right) / 2;
                var k = ruled ? xs.Count(x => x < center) : 0;
                if (rtlPage && ruled) k = columns - 1 - k;
                row[k] = row[k] is null ? w.Text : row[k] + " " + w.Text;
            }
            rows.Add([.. row.Select(c => c ?? "")]);
        }
        if (!anyRuled) return null;
        // Columns no line uses (outside the table's outer borders) are dropped.
        var used = Enumerable.Range(0, columns).Where(k => rows.Any(r => r[k].Length > 0)).ToArray();
        return [.. rows.Select(r => used.Select(k => r[k]).ToArray())];
    }

    private static int Band(List<(double Left, double Right)> bands, double x)
    {
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < bands.Count; i++)
        {
            var distance = x < bands[i].Left ? bands[i].Left - x : x > bands[i].Right ? x - bands[i].Right : 0;
            if (distance < bestDistance) (best, bestDistance) = (i, distance);
        }
        return best;
    }

    private static (string Text, double Left, double Right) Cell(List<Word> words, bool rtlPage) =>
        (string.Join(" ", (rtlPage ? words.AsEnumerable().Reverse() : words).Select(w => w.Text)), words.Min(w => w.Left), words.Max(w => w.Right));

    /// <summary>
    /// Words drawn as separate pieces that touch (a writer may draw one word in two runs) become one word: on one line,
    /// with less than a tenth of a letter's height between them.
    /// </summary>
    private static List<Word> JoinTouching(List<Word> words)
    {
        var joined = new List<Word>();
        foreach (var w in words.OrderByDescending(w => w.Middle).ThenBy(w => w.Left))
        {
            var i = joined.FindIndex(j => Math.Abs(j.Middle - w.Middle) <= Math.Max(j.Height, w.Height) * 0.5
                && (Math.Abs(w.Left - j.Right) <= Math.Max(j.Height, w.Height) * 0.1 || Math.Abs(j.Left - w.Right) <= Math.Max(j.Height, w.Height) * 0.1));
            if (i < 0) { joined.Add(w); continue; }
            var j = joined[i];
            var (left, right) = w.Left < j.Left ? (w, j) : (j, w);
            var rtl = j.Rtl || w.Rtl;
            joined[i] = new Word(rtl ? right.Text + left.Text : left.Text + right.Text, left.Left, Math.Max(left.Right, right.Right),
                (j.Middle + w.Middle) / 2, Math.Max(j.Height, w.Height), rtl);
        }
        return joined;
    }

    private static List<Word> Words(IReadOnlyList<Glyph> glyphs)
    {
        var words = new List<Word>();
        var run = new List<Glyph>();
        void Flush()
        {
            if (run.Count == 0) return;
            var rtl = run.Any(g => IsRtl(g.Char));
            // Marks (a shadda, a vowel sign) stay after the letter they were drawn with; only letters are ordered by place.
            var units = run.Where(g => !IsMark(g.Char)).Select(g => new List<Glyph> { g }).ToList();
            foreach (var mark in run.Where(g => IsMark(g.Char)))
            {
                // A mark goes with the letter it is drawn over: the one whose width holds its middle, else the nearest.
                if (units.Count == 0) { units.Add([mark]); continue; }
                var middle = (mark.Left + mark.Right) / 2;
                units.MinBy(u => middle < u[0].Left ? u[0].Left - middle : middle > u[0].Right ? middle - u[0].Right : 0)!.Add(mark);
            }
            var ordered = units.OrderBy(u => u[0].Left);
            var text = string.Concat((rtl ? ordered.Reverse() : ordered).Select(u => new string([.. u.Select(g => g.Char)])));
            var letters = run.Where(g => !IsMark(g.Char)).DefaultIfEmpty(run[0]).ToList();
            words.Add(new Word(text, letters.Min(g => g.Left), letters.Max(g => g.Right), letters.Average(g => g.Middle), letters.Max(g => g.Height), rtl));
            run.Clear();
        }
        var guessedSpace = false; // a space seen since the last letter
        foreach (var g in glyphs)
        {
            // A space ends a word only where there is a gap to see: PDFium guesses spaces inside Arabic words after letters
            // that do not join, and some writers (Excel) put zero-width spaces there. A line break always ends a word.
            if (g.Char is not ('\r' or '\n') && char.IsWhiteSpace(g.Char)) { guessedSpace = true; continue; }
            if (char.IsWhiteSpace(g.Char) || char.IsControl(g.Char) || g.Height <= 0) { Flush(); guessedSpace = false; continue; }
            if (IsMark(g.Char))
            {
                // A mark sits above or below its letter: it joins the word in progress, or the word just ended.
                if (run.Count > 0) run.Add(g);
                else if (words.Count > 0) words[^1] = words[^1] with { Text = words[^1].Text + g.Char };
                continue;
            }
            if (guessedSpace && run.Count > 0)
            {
                var prev = run.LastOrDefault(x => !IsMark(x.Char), run[^1]);
                var gap = Math.Min(Math.Abs(g.Left - prev.Right), Math.Abs(prev.Left - g.Right));
                if (gap > Math.Max(prev.Height, g.Height) * 0.2) Flush();
            }
            guessedSpace = false;
            if (run.Count > 0)
            {
                var last = run.LastOrDefault(x => !IsMark(x.Char), run[^1]);
                // On one line when their heights overlap: Arabic letters rise and drop far more than Latin ones.
                var sameLine = Math.Min(last.Top, g.Top) - Math.Max(last.Bottom, g.Bottom) > -0.1 * Math.Max(last.Height, g.Height);
                var near = Math.Min(Math.Abs(g.Left - last.Right), Math.Abs(last.Left - g.Right)) <= Math.Max(last.Height, g.Height) * 0.8; // spaces split words; this only joins what touches
                if (!sameLine || !near) Flush();
            }
            run.Add(g);
        }
        Flush();
        return words;
    }

    private static bool IsMark(char c) => char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark;

    private static bool IsRtl(char c) => c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿';
}
