using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WinTools.Core.Settings;
using WinTools.Windows.Settings;

namespace WinTools.Settings;

/// <summary>
/// The settings window. Every change is validated, saved atomically (<see cref="SettingsStore"/>) a moment after the
/// last edit, and announced to the running host (<see cref="SettingsSignal"/>), which applies it at once. There is no
/// Apply or Cancel: what is shown is what is saved. A file from a newer version is shown read-only.
/// </summary>
public partial class MainWindow : Window
{
    public const string WindowTitle = "WinTools Settings";
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(300);

    private readonly SettingsStore _store;
    private readonly string _hostPath;
    private readonly DispatcherTimer _saveTimer;
    private AppSettings _settings = new();
    private bool _loading;
    private bool _pending;
    private int _failedSaves;

    private static readonly (string Value, string Label)[] AnimationChoices = [("System", "Follow Windows"), ("On", "On"), ("Off", "Off")];

    public MainWindow(SettingsStore store, string hostPath)
    {
        _store = store;
        _hostPath = hostPath;
        InitializeComponent();
        _saveTimer = new DispatcherTimer { Interval = SaveDelay };
        _saveTimer.Tick += (_, _) => SaveNow();
        SourceInitialized += (_, _) =>
        {
            var dark = 1;
            _ = Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
        foreach (var key in AppSettings.TriggerKeys.Keys) TriggerKey.Items.Add(key);
        foreach (var (_, label) in AnimationChoices) Animations.Items.Add(label);
        Load();
        Wire();
        Closing += (_, _) =>
        {
            if (ResizeWidth.IsKeyboardFocusWithin || ResizeHeight.IsKeyboardFocusWithin) CommitSizes(); // a typed size not yet committed
            if (_pending) SaveNow(finalAttempt: true);
        };
    }

    private void Load()
    {
        _loading = true;
        _settings = _store.Load();
        TriggerKey.SelectedItem = _settings.TriggerKey;
        UiSounds.IsChecked = _settings.UiSounds;
        SameFolder.IsChecked = _settings.OutputPolicy == OutputPolicy.SameFolder;
        CustomFolder.IsChecked = _settings.OutputPolicy == OutputPolicy.Folder;
        FolderPath.Text = _settings.OutputFolder ?? "";
        JpegQuality.Value = _settings.JpegQuality;
        WebPQuality.Value = _settings.WebPQuality;
        JpegValue.Text = _settings.JpegQuality.ToString(CultureInfo.InvariantCulture);
        WebPValue.Text = _settings.WebPQuality.ToString(CultureInfo.InvariantCulture);
        ResizeWidth.Text = _settings.ResizeWidth.ToString(CultureInfo.InvariantCulture);
        ResizeHeight.Text = _settings.ResizeHeight.ToString(CultureInfo.InvariantCulture);
        KeepAspect.IsChecked = _settings.KeepAspectRatio;
        NoUpscale.IsChecked = _settings.NoUpscale;
        Animations.SelectedIndex = Math.Max(0, Array.FindIndex(AnimationChoices, c => c.Value == _settings.Animations));
        AnimationsHint.Text = WinTools.Windows.SystemPreferences.ReducedMotion()
            ? "Short movements when the menu opens and changes. Animation effects are off in Windows now, so Follow Windows shows none."
            : "Short movements when the menu opens and changes.";
        LoadStartup();
        if (_store.ReadOnly)
        {
            ReadOnlyBanner.Visibility = Visibility.Visible;
            ReadOnlyText.Text = "These settings cannot be changed here: the settings file was written by a newer version of WinTools, or it cannot be read. Nothing will be overwritten.";
            foreach (var c in new Control[] { TriggerKey, UiSounds, SameFolder, CustomFolder, Browse, JpegQuality, WebPQuality, ResizeWidth, ResizeHeight, KeepAspect, NoUpscale, Animations })
                c.IsEnabled = false;
        }
        Footer.Text = "Saved in " + Shorten(_store.Path);
        Footer.ToolTip = "The settings file";
        _loading = false;
    }

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
            StartupHint.Text = "Not available: WinTools.Host.exe is not next to this window's program.";
            return;
        }
        var state = StartupRegistration.Read(_hostPath);
        StartWithWindows.IsChecked = state == StartupState.On;
        StartupHint.Text = state == StartupState.OtherCopy
            ? "Another copy of WinTools starts with Windows now. Turn this on to start this copy instead."
            : "Start WinTools when you sign in, for this Windows account only.";
    }

    private void Wire()
    {
        TriggerKey.SelectionChanged += (_, _) => Change(s => s with { TriggerKey = TriggerKey.SelectedItem as string ?? "Space" });
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
            if (!StartupRegistration.Set(_hostPath, on)) Status.Text = "Start with Windows could not be changed (the registry refused).";
            else Status.Text = on ? "WinTools will start when you sign in." : "WinTools will not start when you sign in.";
            LoadStartup();
        };
        Done.Click += (_, _) => Close();
    }

    private bool ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where WinTools saves new files" };
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
            FolderError.Text = "This folder cannot be used (it must be a full local or network path). New files go next to the originals.";
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
            SizeError.Text = "Enter whole numbers from 0 to 65535.";
            SizeError.Visibility = Visibility.Visible;
            return;
        }
        SizeError.Text = w == 0 && h == 0 ? "With both sides at 0 the Resize menu has no custom size." : "";
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
        Status.Text = "Saving…";
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
                Status.Text = "Saved. A running WinTools applies it at once.";
                return;
            }
            if (finalAttempt) Thread.Sleep(100);
        }
        if (!finalAttempt && ++_failedSaves < 5) { _saveTimer.Start(); return; } // try again shortly
        Status.Text = "Could not save the settings file. Check that its folder can be written to.";
    }
}
