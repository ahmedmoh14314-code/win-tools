using System.Globalization;
using System.Text;

namespace WinTools.Core.Jobs;

/// <summary>
/// Organize pages' window (ADR-0029): the page count and the folder of page thumbnails the image worker drew for it.
/// The folder is the host's own, in its temporary folder; like every dialog request it names no file of the user's.
/// </summary>
public sealed record OrganizeRequest(string Title, string Thumbnails, int Pages, int X, int Y, string Kind = DialogKinds.Organize);

/// <summary>The window's answer: a <see cref="PagePlan"/> in its one spelling, or null when cancelled.</summary>
public sealed record OrganizeResult(string? Answer);

/// <summary>One page of a new PDF: a page of the source (from 1) and the clockwise turn added to it (0, 90, 180, 270).</summary>
public readonly record struct PlannedPage(int Page, int Turn);

/// <summary>
/// What Organize pages writes: the source's pages in a new order, turned, left out or repeated ("organize"), or only some
/// of them ("extract"). One spelling, read the same way by the window, the host and the worker:
/// "organize:1,3@90,3,2" (page 3 turned right, then again unturned). Anything else reads as null.
/// </summary>
public sealed record PagePlan(bool Extract, IReadOnlyList<PlannedPage> Pages)
{
    /// <summary>Most pages a plan may hold: every page of the largest PDF Organize takes, each twice.</summary>
    public const int MaxEntries = 1000;

    private const string OrganizePrefix = "organize:", ExtractPrefix = "extract:";

    public string Format()
    {
        var text = new StringBuilder(Extract ? ExtractPrefix : OrganizePrefix);
        for (var i = 0; i < Pages.Count; i++)
        {
            if (i > 0) text.Append(',');
            text.Append(Pages[i].Page.ToString(CultureInfo.InvariantCulture));
            if (Pages[i].Turn != 0) text.Append('@').Append(Pages[i].Turn.ToString(CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    /// <summary>A plan for a PDF of <paramref name="pageCount"/> pages, or null: an unknown page, a turn that is not a quarter, another spelling.</summary>
    public static PagePlan? Read(string? text, int pageCount)
    {
        if (text is null || text.Length > 16 * MaxEntries || pageCount < 1) return null;
        var extract = text.StartsWith(ExtractPrefix, StringComparison.Ordinal);
        if (!extract && !text.StartsWith(OrganizePrefix, StringComparison.Ordinal)) return null;
        var body = text[(extract ? ExtractPrefix : OrganizePrefix).Length..];
        if (body.Length == 0) return null;
        var parts = body.Split(',');
        if (parts.Length > MaxEntries) return null;
        var pages = new List<PlannedPage>(parts.Length);
        foreach (var part in parts)
        {
            var at = part.IndexOf('@', StringComparison.Ordinal);
            var pageText = at < 0 ? part : part[..at];
            if (!int.TryParse(pageText, NumberStyles.None, CultureInfo.InvariantCulture, out var page) || page < 1 || page > pageCount) return null;
            var turn = 0;
            if (at >= 0 && (!int.TryParse(part[(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out turn) || turn is not (90 or 180 or 270))) return null;
            pages.Add(new PlannedPage(page, turn));
        }
        var plan = new PagePlan(extract, pages);
        return plan.Format() == text ? plan : null; // "01", "3@0", "+2" and the like are other spellings
    }

    /// <summary>Whether the plan would write the source again as it is.</summary>
    public bool KeepsEverything(int pageCount) =>
        !Extract && Pages.Count == pageCount && Pages.Select((p, i) => p.Page == i + 1 && p.Turn == 0).All(same => same);
}
