using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinTools.Core.Files;

public enum RenameSpaces { Keep, ToUnderscores, ToSpaces }

public enum RenameCase { Keep, Lower, Upper, Title }

/// <summary>
/// One Batch rename rule (V2 PR 7.2), applied to each name's stem; the extension is kept as it is. In order: a new name
/// replacing the old one when given (photos: "Holiday" with numbering gives "Holiday 01"), else find and replace (any
/// case) with the spaces it leaves tidied, then spaces and underscores, case, the prefix and the suffix, then a number
/// when asked for (after a space, or an underscore when spaces become underscores), counted in the names' natural order.
/// </summary>
public sealed record RenameRule(string Prefix = "", string Suffix = "", string Find = "", string Replace = "",
    RenameSpaces Spaces = RenameSpaces.Keep, RenameCase Case = RenameCase.Keep, bool Number = false, int Start = 1, int Digits = 2,
    string Name = "")
{
    public const int MaxText = 100, MaxDigits = 6, MaxStart = 999_999;

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <summary>The rule from its parameter, or null when it is not one or is out of bounds.</summary>
    public static RenameRule? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 4096) return null;
        RenameRule? rule;
        try { rule = JsonSerializer.Deserialize<RenameRule>(text, Json); }
        catch (JsonException) { return null; }
        return rule is { Prefix: not null, Suffix: not null, Find: not null, Replace: not null, Name: not null }
            && new[] { rule.Prefix, rule.Suffix, rule.Find, rule.Replace, rule.Name }.All(s => s.Length <= MaxText)
            && rule.Start is >= 0 and <= MaxStart && rule.Digits is >= 1 and <= MaxDigits
            && Enum.IsDefined(rule.Spaces) && Enum.IsDefined(rule.Case) ? rule : null;
    }
}

public enum RenameStatus { Unchanged, Renamed, Invalid, Clash }

/// <summary>One name's fate under a rule: its new name, and why it cannot have it.</summary>
public sealed record RenameItem(string From, string To, RenameStatus Status, string? Problem = null);

/// <summary>
/// Batch rename's plan (V2 PR 7.2): pure, so the dialog previews exactly what the host applies. A name that cannot exist
/// on Windows, or two names that would be one, block the whole rename.
/// </summary>
public static class BatchRename
{
    public const int MaxNameLength = 255;

    public static IReadOnlyList<RenameItem> Plan(IReadOnlyList<string> names, RenameRule rule)
    {
        var order = names.Select((n, i) => (n, i)).OrderBy(x => x.n, NaturalComparer.Instance).Select(x => x.i).ToArray();
        var numbers = new int[names.Count];
        for (var k = 0; k < order.Length; k++) numbers[order[k]] = rule.Start + k;
        var targets = names.Select((n, i) => NewName(n, rule, numbers[i])).ToArray();
        var counts = targets.GroupBy(t => t, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var items = new RenameItem[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            var (from, to) = (names[i], targets[i]);
            items[i] = Problem(to) is { } problem ? new(from, to, RenameStatus.Invalid, problem)
                : counts[to] > 1 ? new(from, to, RenameStatus.Clash, "Another file would get the same name")
                : string.Equals(from, to, StringComparison.Ordinal) ? new(from, to, RenameStatus.Unchanged)
                : new(from, to, RenameStatus.Renamed);
        }
        return items;
    }

    /// <summary>Whether the plan renames something and nothing blocks it.</summary>
    public static bool CanApply(IReadOnlyList<RenameItem> plan) =>
        plan.Any(i => i.Status == RenameStatus.Renamed) && plan.All(i => i.Status is RenameStatus.Renamed or RenameStatus.Unchanged);

    private static string NewName(string name, RenameRule rule, int number)
    {
        var ext = Path.GetExtension(name);
        var stem = name[..^ext.Length];
        if (rule.Name.Length > 0) stem = rule.Name;
        else if (rule.Find.Length > 0) stem = Tidy(stem.Replace(rule.Find, rule.Replace, StringComparison.OrdinalIgnoreCase));
        stem = rule.Spaces switch
        {
            RenameSpaces.ToUnderscores => stem.Replace(' ', '_'),
            RenameSpaces.ToSpaces => stem.Replace('_', ' '),
            _ => stem,
        };
        stem = rule.Case switch
        {
            RenameCase.Lower => stem.ToLowerInvariant(),
            RenameCase.Upper => stem.ToUpperInvariant(),
            RenameCase.Title => Title(stem),
            _ => stem,
        };
        stem = rule.Prefix + stem + rule.Suffix;
        if (rule.Number) stem += (rule.Spaces == RenameSpaces.ToUnderscores ? "_" : " ") + number.ToString(new string('0', rule.Digits), System.Globalization.CultureInfo.InvariantCulture);
        return stem + ext;
    }

    /// <summary>A removed word leaves no double or edge spaces ("photo copy" without "copy" is "photo").</summary>
    private static string Tidy(string stem)
    {
        while (stem.Contains("  ", StringComparison.Ordinal)) stem = stem.Replace("  ", " ", StringComparison.Ordinal);
        return stem.Trim(' ');
    }

    /// <summary>Each word's first letter upper case and the rest lower (words split by spaces, underscores, dashes, dots).</summary>
    private static string Title(string text)
    {
        var sb = new StringBuilder(text.Length);
        var start = true;
        foreach (var c in text)
        {
            sb.Append(start ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            start = c is ' ' or '_' or '-' or '.';
        }
        return sb.ToString();
    }

    /// <summary>Why a name cannot exist in a Windows folder, in the user's words; null when it can.</summary>
    public static string? Problem(string name)
    {
        if (name.Length == 0 || Path.GetFileNameWithoutExtension(name).Length == 0) return "The name would be empty";
        if (name.Length > MaxNameLength) return "The name would be too long";
        if (name.Any(c => c < ' ' || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')) return "The name would hold a character Windows does not allow (< > : \" / \\ | ? *)";
        if (name[^1] is '.' or ' ') return "The name would end with a dot or a space";
        // The same rule as every other path WinTools takes (device names, "aux .txt", "con.backup.txt", 8.3-alias shapes),
        // so the product can always work on the files it renamed (review, PR 7.2).
        if (!PathPolicy.IsSupported(@"C:\x\" + name)) return "The name is reserved by Windows";
        return null;
    }

    /// <summary>"img2" before "img10": digit runs compare as numbers, the rest ignoring case.</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null || y is null) return string.CompareOrdinal(x, y);
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    var (si, sj) = (i, j);
                    while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                    while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                    var a = x[si..i].TrimStart('0');
                    var b = y[sj..j].TrimStart('0');
                    var c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                    if (c != 0) return c;
                }
                else
                {
                    var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (c != 0) return c;
                    i++;
                    j++;
                }
            }
            var rest = (x.Length - i).CompareTo(y.Length - j);
            return rest != 0 ? rest : string.CompareOrdinal(x, y);
        }
    }
}
