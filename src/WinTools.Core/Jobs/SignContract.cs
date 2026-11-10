using System.Globalization;
using System.Text;

namespace WinTools.Core.Jobs;

/// <summary>
/// The Sign window's request: the PDF's page count and the folder of page pictures the image worker drew for it, as for
/// Edit pages. Like every dialog request it names no file of the user's.
/// </summary>
public sealed record SignRequest(string Title, string Thumbnails, int Pages, int X, int Y, string Kind = DialogKinds.Sign);

/// <summary>The window's answer: a <see cref="SignPlan"/> in its one spelling, or null when cancelled.</summary>
public sealed record SignResult(string? Answer);

/// <summary>One stroke of a drawn signature: points in the signature box, x 0 to <see cref="SignPlan.BoxWidth"/>, y 0 to <see cref="SignPlan.BoxHeight"/> (down).</summary>
public sealed record InkStroke(IReadOnlyList<(int X, int Y)> Points);

/// <summary>
/// Where and what to sign: one page (from 1) or every page, the signature's centre as fractions of the page as it is
/// shown (0,0 the top left), its width as a fraction of the page's width, and the signature itself, drawn (strokes in a
/// 1000 × 400 box) or typed (a line of text). One spelling, read the same way by the window, the host and the worker:
/// "page=2;x=0.75;y=0.88;w=0.3;ink=10,20 30,40|50,60 70,80" or "...;text=Ahmed%20Ali". Anything else reads as null.
/// </summary>
public sealed record SignPlan(int? Page, double X, double Y, double Width, IReadOnlyList<InkStroke>? Ink, string? Text)
{
    public const int BoxWidth = 1000, BoxHeight = 400;
    public const int MaxStrokes = 300, MaxPoints = 8000, MaxTextLength = 60;

    public string Format()
    {
        var sb = new StringBuilder("page=");
        sb.Append(Page is { } p ? p.ToString(CultureInfo.InvariantCulture) : "all");
        sb.Append(";x=").Append(Fraction(X)).Append(";y=").Append(Fraction(Y)).Append(";w=").Append(Fraction(Width));
        if (Text is not null) sb.Append(";text=").Append(Uri.EscapeDataString(Text));
        else
        {
            sb.Append(";ink=");
            for (var s = 0; s < Ink!.Count; s++)
            {
                if (s > 0) sb.Append('|');
                for (var i = 0; i < Ink[s].Points.Count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(Ink[s].Points[i].X.ToString(CultureInfo.InvariantCulture)).Append(',').Append(Ink[s].Points[i].Y.ToString(CultureInfo.InvariantCulture));
                }
            }
        }
        return sb.ToString();
    }

    private static string Fraction(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A plan for a PDF of <paramref name="pageCount"/> pages, or null.</summary>
    public static SignPlan? Read(string? text, int pageCount)
    {
        if (text is null || text.Length > 120_000 || pageCount < 1) return null;
        var parts = text.Split(';');
        if (parts.Length != 5 || !parts[0].StartsWith("page=", StringComparison.Ordinal) || !parts[1].StartsWith("x=", StringComparison.Ordinal)
            || !parts[2].StartsWith("y=", StringComparison.Ordinal) || !parts[3].StartsWith("w=", StringComparison.Ordinal)) return null;
        int? page = null;
        var pageText = parts[0][5..];
        if (pageText != "all")
        {
            if (!int.TryParse(pageText, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p < 1 || p > pageCount) return null;
            page = p;
        }
        if (ReadFraction(parts[1][2..]) is not { } x || ReadFraction(parts[2][2..]) is not { } y || ReadFraction(parts[3][2..]) is not { } w || w < 0.05 || w > 0.9) return null;
        SignPlan plan;
        if (parts[4].StartsWith("text=", StringComparison.Ordinal))
        {
            var line = Uri.UnescapeDataString(parts[4][5..]);
            if (line.Length is 0 or > MaxTextLength || line.Trim() != line
                || line.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned))
                return null;
            plan = new SignPlan(page, x, y, w, null, line);
        }
        else if (parts[4].StartsWith("ink=", StringComparison.Ordinal))
        {
            var strokes = new List<InkStroke>();
            var total = 0;
            var body = parts[4][4..];
            if (body.Length == 0) return null;
            foreach (var s in body.Split('|'))
            {
                if (strokes.Count == MaxStrokes) return null;
                var points = new List<(int, int)>();
                foreach (var pt in s.Split(' '))
                {
                    var comma = pt.IndexOf(',', StringComparison.Ordinal);
                    if (comma <= 0 || ++total > MaxPoints
                        || !int.TryParse(pt.AsSpan(0, comma), NumberStyles.None, CultureInfo.InvariantCulture, out var px)
                        || !int.TryParse(pt.AsSpan(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var py)
                        || px > BoxWidth || py > BoxHeight) return null;
                    points.Add((px, py));
                }
                strokes.Add(new InkStroke(points));
            }
            plan = new SignPlan(page, x, y, w, strokes, null);
        }
        else return null;
        return plan.Format() == text ? plan : null; // one spelling only
    }

    /// <summary>0 to 1 with at most three decimals, written as <see cref="Fraction"/> writes it.</summary>
    private static double? ReadFraction(string text) =>
        text.Length is > 0 and <= 5 && double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 1
            ? Math.Round(v, 3) : null;
}
