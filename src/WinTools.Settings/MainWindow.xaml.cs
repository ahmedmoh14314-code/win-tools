using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WinTools.Core.Capabilities;
using WinTools.Core.Localization;
using WinTools.Core.Settings;
using WinTools.Windows.Settings;

namespace WinTools.Settings;

/// <summary>
/// The WinTools window: Home, How to use, Features, Settings and About, with the first-run tour over it. The resident
/// host stays the day-to-day tool (the wheel); this window explains it and holds its settings. Every setting is
/// validated, saved atomically (<see cref="SettingsStore"/>) a moment after the last edit, and announced to the running
/// host (<see cref="SettingsSignal"/>), which applies it at once. A file from a newer version is shown read-only. Every
/// text comes from <see cref="UiText"/>; in Arabic the window runs right to left, and it is built again when the
/// language changes.
/// </summary>
public partial class MainWindow : Window
{
    public const string WindowTitle = "WinTools";
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(300);

    private readonly SettingsStore _store;
    private readonly string _hostPath;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _hostTimer;
    private AppSettings _settings = new();
    private bool _loading;
    private bool _pending;
    private int _failedSaves;
    private bool _closeWarned;

    private static readonly (string Value, string Key)[] AnimationChoices = [("System", "settings.animations.system"), ("On", "settings.animations.on"), ("Off", "settings.animations.off")];
    private static readonly (string Value, string Key)[] LanguageChoices = [("System", "lang.system"), (UiText.English, "lang.english"), (UiText.Arabic, "lang.arabic")];

    /// <summary>The language was changed here: the application builds the window again in it.</summary>
    public event Action<string>? LanguageSwitched;

    public MainWindow(SettingsStore store, string hostPath, string startPage = "home")
    {
        _store = store;
        _hostPath = hostPath;
        InitializeComponent();
        Title = WindowTitle;
        FlowDirection = UiText.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _saveTimer = new DispatcherTimer { Interval = SaveDelay };
        _saveTimer.Tick += (_, _) => SaveNow();
        _hostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _hostTimer.Tick += (_, _) => ShowHostState();
        SourceInitialized += (_, _) =>
        {
            var dark = 1;
            _ = Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
        foreach (var key in TriggerKeyPolicy.Order) TriggerKey.Items.Add(key);
        foreach (var (_, key) in AnimationChoices) Animations.Items.Add(UiText.T(key));
        foreach (var (_, key) in LanguageChoices) LanguageChoice.Items.Add(UiText.T(key));
        Load();
        Wire();
        WireShell();
        BuildFamilies();
        BuildFeatures();
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "";
        AboutVersion.Text = UiText.F("about.version", version);
        NavVersion.Text = version;
        Navigate(startPage);
        Loaded += (_, _) =>
        {
            ShowHostState();
            _hostTimer.Start();
            if (!_settings.Onboarded && !_store.ReadOnly) StartTour();
        };
        Closing += (_, e) =>
        {
            if (ResizeWidth.IsKeyboardFocusWithin || ResizeHeight.IsKeyboardFocusWithin) CommitSizes(); // a typed size not yet committed
            if (_pending) SaveNow(finalAttempt: true);
            if (_pending && !_closeWarned)
            {
                // Not saved: stay open once and say so; closing again discards the change.
                _closeWarned = true;
                e.Cancel = true;
                Navigate("settings");
                Status.Text = UiText.T("settings.closeUnsaved");
                return;
            }
            _hostTimer.Stop();
        };
    }

    // ---- Shell: navigation, Home, Features, About, the tour ----

    private void WireShell()
    {
        NavHome.Checked += (_, _) => Show("home");
        NavHowTo.Checked += (_, _) => Show("howto");
        NavFeatures.Checked += (_, _) => Show("features");
        NavSettings.Checked += (_, _) => Show("settings");
        NavAbout.Checked += (_, _) => Show("about");
        HomeHowTo.Click += (_, _) => Navigate("howto");
        HomeSettings.Click += (_, _) => Navigate("settings");
        ShowTour.Click += (_, _) => StartTour();
        StartHost.Click += (_, _) => StartHostProcess();
        OpenLogs.Click += (_, _) => OpenLogsFolder();
        AboutLogs.Click += (_, _) => OpenLogsFolder();
        Notices.Click += (_, _) => OpenNotices();
        Tour.Completed += FinishTour;
        Tour.LanguageChanged += language =>
        {
            UiText.Use(language, WinTools.Windows.SystemPreferences.UiLanguageTag());
            FlowDirection = UiText.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        };
    }

    /// <summary>Opens a page by its name ("home", "howto", "features", "settings", "about").</summary>
    public void Navigate(string page)
    {
        var nav = page switch { "howto" => NavHowTo, "features" => NavFeatures, "settings" => NavSettings, "about" => NavAbout, _ => NavHome };
        if (nav.IsChecked == true) Show(page);
        else nav.IsChecked = true;
    }

    private void Show(string page)
    {
        HomePage.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        HowToPage.Visibility = page == "howto" ? Visibility.Visible : Visibility.Collapsed;
        FeaturesPage.Visibility = page == "features" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Whether the resident host is running (its single-instance mutex exists), and the trigger it listens for.</summary>
    private void ShowHostState()
    {
        var running = Mutex.TryOpenExisting(@"Local\WinTools.Host", out var mutex);
        mutex?.Dispose();
        HostState.Text = UiText.T(running ? "home.running" : "home.notRunning");
        StatusDot.Fill = (System.Windows.Media.Brush)FindResource(running ? "Green" : "Warning");
        StartHost.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        HomeTrigger.Text = UiText.F("home.trigger", _settings.TriggerKey);
        HomeHint.Text = UiText.F("home.hint", _settings.TriggerKey);
        HomeSteps.Trigger = _settings.TriggerKey;
        HowToSteps.Trigger = _settings.TriggerKey;
    }

    private void StartHostProcess()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(_hostPath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(_hostPath)!, ArgumentList = { "--background" } });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            HostState.Text = UiText.T("home.startFailed");
        }
        ShowHostState();
    }

    private static void OpenLogsFolder()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTools", "logs");
        OpenInExplorer(folder, createIt: true);
    }

    private static void OpenNotices()
    {
        var notices = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        if (File.Exists(notices)) OpenInExplorer(notices, createIt: false);
        else OpenInExplorer(AppContext.BaseDirectory, createIt: false);
    }

    private static void OpenInExplorer(string target, bool createIt)
    {
        try
        {
            if (createIt) Directory.CreateDirectory(target);
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var _ = Process.Start(new ProcessStartInfo(explorer) { UseShellExecute = false, ArgumentList = { File.Exists(target) ? "/select," + target : target } });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>The families WinTools works with, shown on Home: only those it really has commands for.</summary>
    private void BuildFamilies()
    {
        var modules = CapabilityResolver.For(null).Catalog.Select(c => c.Module).ToHashSet(StringComparer.Ordinal);
        var families = new (string Module, string Key)[]
        {
            ("images", "family.images"), ("pdf", "family.pdf"), ("documents", "family.documents"), ("archive", "family.archives"),
            ("subtitles", "family.subtitles"), ("video", "family.video"), ("audio", "family.audio"), ("files", "family.files"),
        };
        foreach (var (module, key) in families.Where(f => modules.Contains(f.Module)))
            Families.Children.Add(new Border
            {
                Style = (Style)FindResource("Card"), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 8),
                Child = new TextBlock { Text = UiText.T(key), FontWeight = FontWeights.SemiBold },
            });
    }

    /// <summary>Every command, grouped by what it works on, in the language in use: what is listed is what the wheel offers.</summary>
    private void BuildFeatures()
    {
        var catalog = CapabilityResolver.For(null).Catalog;
        foreach (var group in catalog.GroupBy(c => c.Module))
        {
            FeatureList.Children.Add(new TextBlock { Text = UiText.TOr("module." + group.Key, group.Key), Style = (Style)FindResource("SectionTitle") });
            var wrap = new WrapPanel();
            foreach (var c in group)
                wrap.Children.Add(new Border
                {
                    Background = (System.Windows.Media.Brush)FindResource("Surface"), BorderBrush = (System.Windows.Media.Brush)FindResource("Edge"),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 6),
                    Child = new TextBlock { Text = UiText.TOr("cmd." + c.Id, c.Label) },
                });
            FeatureList.Children.Add(wrap);
        }
    }

    private void StartTour()
    {
        var startup = File.Exists(_hostPath) && StartupRegistration.Read(_hostPath) == StartupState.On;
        Tour.Start(_settings, startup || !_settings.Onboarded);
    }

    private void FinishTour(OnboardingChoices? choices)
    {
        var languageBefore = _settings.Language;
        if (choices is null)
        {
            Change(s => s with { Onboarded = true });
            SaveNow(finalAttempt: true);
            if (_settings.Language != languageBefore) LanguageSwitched?.Invoke(_settings.Language);
            return;
        }
        Change(s => s with
        {
            Onboarded = true,
            Language = choices.Language,
            TriggerKey = choices.TriggerKey,
            OutputPolicy = choices.Output,
            OutputFolder = choices.Output == OutputPolicy.Folder ? choices.OutputFolder : s.OutputFolder,
        });
        SaveNow(finalAttempt: true);
        if (File.Exists(_hostPath)) StartupRegistration.Set(_hostPath, choices.StartWithWindows);
        if (!Mutex.TryOpenExisting(@"Local\WinTools.Host", out var m)) StartHostProcess(); else m.Dispose();
        if (choices.Language != languageBefore) LanguageSwitched?.Invoke(choices.Language);
        else { Show(_settings); ShowHostState(); }
    }

    // ---- Settings ----

    private void Load()
    {
        _settings = _store.Load();
        Show(_settings);
    }

    /// <summary>Puts the settings into the controls without saving anything.</summary>
    private void Show(AppSettings settings)
    {
        _loading = true;
        SizeError.Visibility = Visibility.Collapsed; // a message about the value being replaced must not stay (evaluator, PR #19)
        FolderError.Visibility = Visibility.Collapsed;
        LanguageChoice.SelectedIndex = Math.Max(0, Array.FindIndex(LanguageChoices, c => c.Value == settings.Language));
        TriggerKey.SelectedItem = settings.TriggerKey;
        UiSounds.IsChecked = settings.UiSounds;
        SameFolder.IsChecked = settings.OutputPolicy == OutputPolicy.SameFolder;
        CustomFolder.IsChecked = settings.OutputPolicy == OutputPolicy.Folder;
        FolderPath.Text = settings.OutputFolder ?? "";
        JpegQuality.Value = settings.JpegQuality;
        WebPQuality.Value = settings.WebPQuality;
        JpegValue.Text = settings.JpegQuality.ToString(CultureInfo.InvariantCulture);
        WebPValue.Text = settings.WebPQuality.ToString(CultureInfo.InvariantCulture);
        ResizeWidth.Text = settings.ResizeWidth.ToString(CultureInfo.InvariantCulture);
        ResizeHeight.Text = settings.ResizeHeight.ToString(CultureInfo.InvariantCulture);
        KeepAspect.IsChecked = settings.KeepAspectRatio;
        NoUpscale.IsChecked = settings.NoUpscale;
        Animations.SelectedIndex = Math.Max(0, Array.FindIndex(AnimationChoices, c => c.Value == settings.Animations));
        AnimationsHint.Text = UiText.T(WinTools.Windows.SystemPreferences.ReducedMotion() ? "settings.animations.hintOff" : "settings.animations.hint");
        LoadStartup();
        if (_store.ReadOnly)
        {
            ReadOnlyBanner.Visibility = Visibility.Visible;
            ReadOnlyText.Text = UiText.T("settings.readOnly");
            foreach (var c in new Control[] { LanguageChoice, TriggerKey, UiSounds, SameFolder, CustomFolder, Browse, JpegQuality, WebPQuality, ResizeWidth, ResizeHeight, KeepAspect, NoUpscale, Animations, Reset })
                c.IsEnabled = false;
        }
        Footer.Text = UiText.F("settings.savedIn", Ltr(Shorten(_store.Path)));
        Footer.ToolTip = UiText.T("settings.file");
        _loading = false;
    }

    /// <summary>A path or name kept left to right inside Arabic text.</summary>
    private static string Ltr(string text) => "‎" + text + "‎"; // left-to-right marks: % and \ then stay with the path

    /// <summary>The path with the user's own folders written as %APPDATA%, %LOCALAPPDATA%, %TEMP% or %USERPROFILE%.</summary>
    private static string Shorten(string path)
    {
        foreach (var (name, folder) in new[]
        {
            ("%TEMP%", Path.TrimEndingDirectorySeparator(Path.GetTempPath())),
            ("%APPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            ("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            ("%USERPROFILE%", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        })
            if (folder.Length > 0 && path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return name + path[folder.Length..];
        return path;
    }

    private void LoadStartup()
    {
        if (!File.Exists(_hostPath))
        {
            StartWithWindows.IsEnabled = false;
            StartupHint.Text = UiText.T("settings.startup.missing");
            return;
        }
        var state = StartupRegistration.Read(_hostPath);
        StartWithWindows.IsChecked = state == StartupState.On;
        StartupHint.Text = UiText.T(state == StartupState.OtherCopy ? "settings.startup.other" : "settings.startup.hint");
    }

    private void Wire()
    {
        LanguageChoice.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            var language = LanguageChoices[Math.Max(0, LanguageChoice.SelectedIndex)].Value;
            Change(s => s with { Language = language });
            SaveNow(finalAttempt: true);
            LanguageSwitched?.Invoke(language);
        };
        TriggerKey.SelectionChanged += (_, _) => { Change(s => s with { TriggerKey = TriggerKey.SelectedItem as string ?? "Space" }); ShowHostState(); };
        // Every setting in the file back to its default ("Start with Windows" is not in the file and stays as it is).
        Reset.Click += (_, _) =>
        {
            // One click wipes the output folder and every quality: ask first.
            if (MessageBox.Show(this, UiText.T("settings.reset.ask"), UiText.T("settings.reset.title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var language = _settings.Language;
            Show(AppSettings.Defaults with { Onboarded = true });
            Change(_ => AppSettings.Defaults with { Onboarded = true });
            SaveNow(finalAttempt: true);
            if (AppSettings.Defaults.Language != language) LanguageSwitched?.Invoke(AppSettings.Defaults.Language);
        };
        UiSounds.Click += (_, _) => Change(s => s with { UiSounds = UiSounds.IsChecked == true });
        Animations.SelectionChanged += (_, _) => Change(s => s with { Animations = AnimationChoices[Math.Max(0, Animations.SelectedIndex)].Value });
        KeepAspect.Click += (_, _) => Change(s => s with { KeepAspectRatio = KeepAspect.IsChecked == true });
        NoUpscale.Click += (_, _) => Change(s => s with { NoUpscale = NoUpscale.IsChecked == true });
        JpegQuality.ValueChanged += (_, e) => { JpegValue.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture); Change(s => s with { JpegQuality = (int)e.NewValue }); };
        WebPQuality.ValueChanged += (_, e) => { WebPValue.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture); Change(s => s with { WebPQuality = (int)e.NewValue }); };
        SameFolder.Checked += (_, _) => { FolderError.Visibility = Visibility.Collapsed; Change(s => s with { OutputPolicy = OutputPolicy.SameFolder }); };
        CustomFolder.Checked += (_, _) =>
        {
            if (_loading) return;
            if (string.IsNullOrEmpty(FolderPath.Text) && !ChooseFolder()) { SameFolder.IsChecked = true; return; }
            UseFolder(FolderPath.Text);
        };
        Browse.Click += (_, _) => { if (ChooseFolder()) { CustomFolder.IsChecked = true; UseFolder(FolderPath.Text); } };
        foreach (var box in new[] { ResizeWidth, ResizeHeight })
        {
            box.LostKeyboardFocus += (_, _) => CommitSizes();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitSizes(); };
            box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
        }
        StartWithWindows.Click += (_, _) =>
        {
            var on = StartWithWindows.IsChecked == true;
            Status.Text = !StartupRegistration.Set(_hostPath, on) ? UiText.T("settings.startup.failed") : UiText.T(on ? "settings.startup.on" : "settings.startup.off");
            LoadStartup();
        };
    }

    private bool ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = UiText.T("settings.output.choose") };
        if (FolderPath.Text.Length > 0 && Directory.Exists(FolderPath.Text)) dialog.InitialDirectory = FolderPath.Text;
        if (dialog.ShowDialog(this) != true) return false;
        FolderPath.Text = dialog.FolderName;
        return true;
    }

    private void UseFolder(string folder)
    {
        var candidate = new AppSettings { OutputPolicy = OutputPolicy.Folder, OutputFolder = folder }.Normalized();
        if (candidate.OutputPolicy != OutputPolicy.Folder)
        {
            FolderError.Text = UiText.T("settings.output.bad");
            FolderError.Visibility = Visibility.Visible;
            SameFolder.IsChecked = true;
            return;
        }
        FolderError.Visibility = Visibility.Collapsed;
        Change(s => s with { OutputPolicy = OutputPolicy.Folder, OutputFolder = candidate.OutputFolder });
    }

    private void CommitSizes()
    {
        if (!int.TryParse(ResizeWidth.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var w) || w > 65_535
            || !int.TryParse(ResizeHeight.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var h) || h > 65_535)
        {
            SizeError.Text = UiText.T("settings.resize.bad");
            SizeError.Visibility = Visibility.Visible;
            return;
        }
        SizeError.Text = w == 0 && h == 0 ? UiText.T("settings.resize.none") : "";
        SizeError.Visibility = SizeError.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Change(s => s with { ResizeWidth = w, ResizeHeight = h });
    }

    private void Change(Func<AppSettings, AppSettings> edit)
    {
        if (_loading || _store.ReadOnly) return;
        var next = edit(_settings).Normalized();
        if (next == _settings) return;
        _settings = next;
        _pending = true;
        Status.Text = UiText.T("settings.saving");
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>
    /// Saves the pending change. The file can be briefly locked (the host reading it, an indexer or antivirus), so a failed
    /// save is retried a few times before it is reported; the change stays pending until it is saved.
    /// </summary>
    private void SaveNow(bool finalAttempt = false)
    {
        _saveTimer.Stop();
        if (!_pending) return;
        for (var attempt = 0; attempt < (finalAttempt ? 5 : 1); attempt++)
        {
            if (_store.Save(_settings))
            {
                _pending = false;
                _failedSaves = 0;
                SettingsSignal.Raise();
                Status.Text = UiText.T("settings.savedNow");
                return;
            }
            if (finalAttempt) Thread.Sleep(100);
        }
        if (!finalAttempt && ++_failedSaves < 5) { _saveTimer.Start(); return; } // try again shortly
        _failedSaves = 0; // the next edit gets its own retries
        Status.Text = UiText.T("settings.saveFailed");
    }
}
