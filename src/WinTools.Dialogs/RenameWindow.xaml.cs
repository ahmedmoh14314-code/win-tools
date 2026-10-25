using System.Globalization;
using System.Windows;
using WinTools.Core.Files;
using WinTools.Core.Jobs;

namespace WinTools.Dialogs;

/// <summary>
/// Batch rename (V2 PR 7.2): the rule's fields and a preview of every name, made with the same plan the host applies,
/// so what is shown is what happens. Rename stays disabled while a name is invalid or two names clash, or nothing would
/// change; the reason is shown. Enter renames, Esc cancels; closing it any other way is a cancel. It receives names only.
/// </summary>
public partial class RenameWindow : Window
{
    private readonly RenameRequest _request;
    private bool _ready;

    /// <summary>The rule, serialized for the host, or null when cancelled.</summary>
    public string? Rule { get; private set; }

    /// <summary>One preview row: the name now, and after (or why it cannot be).</summary>
    public sealed record Row(string From, string Shown);

    public RenameWindow(RenameRequest request)
    {
        _request = request;
        InitializeComponent();
        Title = request.Title;
        Heading.Text = request.Names.Count == 1 ? "Rename 1 file" : $"Rename {request.Names.Count:N0} files";
        _ready = true;
        Loaded += (_, _) =>
        {
            WindowPlacement.NearPoint(this, _request.X, _request.Y);
            Activate();
            Prefix.Focus();
            Refresh();
        };
    }

    private RenameRule? Current()
    {
        if (!int.TryParse(Start.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var start)) start = -1;
        if (!int.TryParse(Digits.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var digits)) digits = 0;
        var rule = new RenameRule(Prefix.Text, Suffix.Text, Find.Text, Replace.Text, (RenameSpaces)Math.Max(0, Spaces.SelectedIndex),
            (RenameCase)Math.Max(0, Case.SelectedIndex), Number.IsChecked == true, Number.IsChecked == true ? start : 1, Number.IsChecked == true ? digits : 2);
        return RenameRule.Parse(rule.Serialize());
    }

    private void OnChanged(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (!_ready || !IsLoaded) return;
        var rule = Current();
        if (rule is null)
        {
            Preview.ItemsSource = null;
            Problem.Text = "The number must start at 0 or more, with 1 to 6 digits.";
            Ok.IsEnabled = false;
            return;
        }
        var plan = BatchRename.Plan(_request.Names, rule);
        Preview.ItemsSource = plan.Select(i => new Row(i.From, i.Status switch
        {
            RenameStatus.Unchanged => "(unchanged)",
            RenameStatus.Renamed => i.To,
            _ => $"{i.To}: {i.Problem}",
        })).ToArray();
        var blocked = plan.FirstOrDefault(i => i.Problem is not null);
        Problem.Text = blocked is not null ? $"{blocked.From}: {blocked.Problem}." : BatchRename.CanApply(plan) ? "" : "No name would change yet.";
        Ok.IsEnabled = BatchRename.CanApply(plan);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Current() is not { } rule || !BatchRename.CanApply(BatchRename.Plan(_request.Names, rule))) return;
        Rule = rule.Serialize();
        Close();
    }
}
