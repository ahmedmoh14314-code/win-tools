using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinTools.Core.Localization;
using WinTools.Core.Settings;

namespace WinTools.Settings;

/// <summary>What the first-run tour asked (language, trigger, where new files go, start with Windows).</summary>
public sealed record OnboardingChoices(string Language, string TriggerKey, OutputPolicy Output, string? OutputFolder, bool StartWithWindows);

/// <summary>
/// The first-run tour, over the whole window: welcome, how the gesture works, then the few choices that matter (language,
/// trigger key, where new files go, start with Windows), then "WinTools is ready". Shown once (<see cref="AppSettings.Onboarded"/>)
/// and again on request from How to use. Choosing a language redraws the tour in it at once.
/// </summary>
public sealed class Onboarding : UserControl
{
    private const int Pages = 4;
    private readonly Grid _root = new();
    private int _page;
    private OnboardingChoices _choices = new("System", "Space", OutputPolicy.SameFolder, null, true);

    /// <summary>The tour ended: finished (choices given) or skipped (null).</summary>
    public event Action<OnboardingChoices?>? Completed;

    /// <summary>The language was changed on the choices page; the window turns to it.</summary>
    public event Action<string>? LanguageChanged;

    public Onboarding()
    {
        Content = _root;
        _root.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x11, 0x17, 0x14));
    }

    /// <summary>Starts the tour from the beginning, with the current settings as the choices' defaults.</summary>
    public void Start(AppSettings current, bool startsWithWindows)
    {
        _choices = new(current.Language, current.TriggerKey, current.OutputPolicy, current.OutputFolder, startsWithWindows);
        _page = 0;
        Visibility = Visibility.Visible;
        Render();
    }

    private void Render()
    {
        _root.Children.Clear();
        var card = new Border
        {
            Style = (Style)FindResource("Card"), Padding = new Thickness(36, 30, 36, 24), MaxWidth = 720, Margin = new Thickness(40),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = UiText.F("onboard.step", _page + 1, Pages), Style = (Style)FindResource("Hint") });
        switch (_page)
        {
            case 0:
                stack.Children.Add(Title(UiText.T("onboard.welcome")));
                stack.Children.Add(Hint(UiText.T("onboard.welcome.text")));
                stack.Children.Add(new FlowSteps { Trigger = _choices.TriggerKey, Margin = new Thickness(0, 22, 0, 0) });
                break;
            case 1:
                stack.Children.Add(Title(UiText.T("howto.title")));
                stack.Children.Add(new FlowSteps { Trigger = _choices.TriggerKey, Detailed = true, Margin = new Thickness(0, 16, 0, 0) });
                break;
            case 2:
                stack.Children.Add(Title(UiText.T("settings.title")));
                stack.Children.Add(Choices());
                break;
            default:
                stack.Children.Add(Title(UiText.T("onboard.ready")));
                stack.Children.Add(new TextBlock { Text = UiText.F("onboard.ready.text", _choices.TriggerKey), FontSize = 16, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
                stack.Children.Add(new FlowSteps { Trigger = _choices.TriggerKey, Margin = new Thickness(0, 22, 0, 0) });
                break;
        }
        stack.Children.Add(Buttons());
        card.Child = stack;
        _root.Children.Add(card);
    }

    private static TextBlock Title(string text) => new() { Text = text, FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 6), TextWrapping = TextWrapping.Wrap };

    private TextBlock Hint(string text) => new() { Text = text, Style = (Style)FindResource("Hint"), FontSize = 14 };

    private StackPanel Choices()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0), MinWidth = 520 };

        // Language: redraws the tour in the chosen language at once.
        var language = new ComboBox { Width = 200, Height = 32, HorizontalAlignment = HorizontalAlignment.Left };
        var languages = new[] { ("System", UiText.T("lang.system")), (UiText.English, UiText.T("lang.english")), (UiText.Arabic, UiText.T("lang.arabic")) };
        foreach (var (_, label) in languages) language.Items.Add(label);
        language.SelectedIndex = Math.Max(0, Array.FindIndex(languages, l => l.Item1 == _choices.Language));
        language.SelectionChanged += (_, _) =>
        {
            var chosen = languages[Math.Max(0, language.SelectedIndex)].Item1;
            if (chosen == _choices.Language) return;
            _choices = _choices with { Language = chosen };
            LanguageChanged?.Invoke(chosen);
            Render();
        };
        panel.Children.Add(Field(UiText.T("onboard.language"), UiText.T("onboard.language.text"), language));

        var trigger = new ComboBox { Width = 120, Height = 32, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var key in TriggerKeyPolicy.Order) trigger.Items.Add(key);
        trigger.SelectedItem = _choices.TriggerKey;
        trigger.SelectionChanged += (_, _) => _choices = _choices with { TriggerKey = trigger.SelectedItem as string ?? "Space" };
        panel.Children.Add(Field(UiText.T("onboard.trigger"), UiText.T("onboard.trigger.text"), trigger));

        var output = new StackPanel();
        var same = new RadioButton { Content = UiText.T("settings.output.same"), GroupName = "TourOutput", IsChecked = _choices.Output == OutputPolicy.SameFolder };
        var folder = new RadioButton { Content = UiText.T("settings.output.folder"), GroupName = "TourOutput", Margin = new Thickness(0, 8, 0, 0), IsChecked = _choices.Output == OutputPolicy.Folder };
        var path = new TextBlock { Text = _choices.OutputFolder ?? "", Style = (Style)FindResource("Hint"), Margin = new Thickness(28, 4, 0, 0), FlowDirection = FlowDirection.LeftToRight };
        same.Checked += (_, _) => _choices = _choices with { Output = OutputPolicy.SameFolder };
        folder.Checked += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = UiText.T("settings.output.choose") };
            var picked = dialog.ShowDialog(Window.GetWindow(this)) == true
                && new AppSettings { OutputPolicy = OutputPolicy.Folder, OutputFolder = dialog.FolderName }.Normalized().OutputPolicy == OutputPolicy.Folder;
            if (!picked) { same.IsChecked = true; return; }
            _choices = _choices with { Output = OutputPolicy.Folder, OutputFolder = dialog.FolderName };
            path.Text = dialog.FolderName;
        };
        output.Children.Add(same);
        output.Children.Add(folder);
        output.Children.Add(path);
        panel.Children.Add(Field(UiText.T("onboard.output"), UiText.T("settings.output.hint"), output));

        var startup = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = _choices.StartWithWindows, HorizontalAlignment = HorizontalAlignment.Left };
        startup.Click += (_, _) => _choices = _choices with { StartWithWindows = startup.IsChecked == true };
        panel.Children.Add(Field(UiText.T("onboard.startup"), UiText.T("onboard.startup.text"), startup));
        return panel;
    }

    private Grid Field(string title, string hint, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var words = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
        words.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        words.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("Hint") });
        grid.Children.Add(words);
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        return grid;
    }

    private DockPanel Buttons()
    {
        var bar = new DockPanel { Margin = new Thickness(0, 26, 0, 0), LastChildFill = false };
        if (_page < Pages - 1)
        {
            var skip = new Button { Content = UiText.T("onboard.skip"), Padding = new Thickness(14, 6, 14, 6) };
            skip.Click += (_, _) => End(null);
            DockPanel.SetDock(skip, Dock.Left);
            bar.Children.Add(skip);
        }
        var next = new Button
        {
            Content = _page < Pages - 1 ? UiText.T("onboard.next") : UiText.T("onboard.finish"),
            Style = (Style)FindResource("Accent"), IsDefault = true, Margin = new Thickness(8, 0, 0, 0),
        };
        next.Click += (_, _) =>
        {
            if (_page < Pages - 1) { _page++; Render(); }
            else End(_choices);
        };
        DockPanel.SetDock(next, Dock.Right);
        bar.Children.Add(next);
        if (_page > 0)
        {
            var back = new Button { Content = UiText.T("onboard.back"), Padding = new Thickness(14, 6, 14, 6) };
            back.Click += (_, _) => { _page--; Render(); };
            DockPanel.SetDock(back, Dock.Right);
            bar.Children.Add(back);
        }
        return bar;
    }

    private void End(OnboardingChoices? choices)
    {
        Visibility = Visibility.Collapsed;
        Completed?.Invoke(choices);
    }
}
