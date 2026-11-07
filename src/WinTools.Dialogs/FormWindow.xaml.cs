using System.Windows;
using System.Windows.Controls;
using WinTools.Core.Jobs;
using WinTools.Core.Localization;

namespace WinTools.Dialogs;

/// <summary>
/// A command's form (Trim, Split, Shift…): one labelled box per field of <see cref="Forms.For"/>, each with its hint, checked
/// as it is typed with the same <see cref="Forms.Check"/> the host and the worker apply. Go stays disabled while a box is
/// wrong and the reason is shown; Enter goes, Esc cancels, closing it any other way is a cancel. Times and numbers are
/// typed left to right in either language.
/// </summary>
public partial class FormWindow : Window
{
    private readonly FormRequest _request;
    private readonly IReadOnlyList<FormField> _fields;
    private readonly List<TextBox> _boxes = [];

    /// <summary>The canonical answer, or null when cancelled.</summary>
    public string? Answer { get; private set; }

    public FormWindow(FormRequest request, IReadOnlyList<FormField> fields)
    {
        _request = request;
        _fields = fields;
        InitializeComponent();
        Title = request.Title;
        Heading.Text = request.Title;
        foreach (var f in fields)
        {
            var label = UiText.T(f.LabelKey);
            Fields.Children.Add(new TextBlock
            {
                Text = label, Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary"), FontFamily = new("Segoe UI"), FontSize = 12,
                Margin = new(0, _boxes.Count == 0 ? 0 : 10, 0, 4),
            });
            var box = new TextBox { Text = f.Default, FontSize = 14, Padding = new(6, 4, 6, 4), MaxLength = 12, FlowDirection = FlowDirection.LeftToRight, ToolTip = UiText.T(f.LabelKey + ".tip") };
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            System.Windows.Automation.AutomationProperties.SetHelpText(box, UiText.T(f.LabelKey + ".tip"));
            box.TextChanged += (_, _) => Refresh();
            _boxes.Add(box);
            Fields.Children.Add(box);
            Fields.Children.Add(new TextBlock
            {
                Text = UiText.T(f.LabelKey + ".tip"), Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary"), FontFamily = new("Segoe UI"),
                FontSize = 11, Margin = new(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
            });
        }
        Loaded += (_, _) =>
        {
            WindowPlacement.NearPoint(this, _request.X, _request.Y);
            Activate();
            if (_boxes.Count > 0)
            {
                _boxes[0].Focus();
                _boxes[0].SelectAll();
            }
            Refresh();
        };
    }

    private FormCheck Check() => Forms.Check(_request.Form, [.. _boxes.Select(b => b.Text)]);

    private void Refresh()
    {
        if (!IsLoaded) return;
        var check = Check();
        Ok.IsEnabled = check.Answer is not null;
        Problem.Text = check.ProblemKey is { } key ? UiText.F(key, check.Field >= 0 && check.Field < _fields.Count ? UiText.T(_fields[check.Field].LabelKey) : "") : "";
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Check().Answer is not { } answer) return;
        Answer = answer;
        Close();
    }
}
