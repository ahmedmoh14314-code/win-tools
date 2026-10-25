using System.Runtime.InteropServices;
using System.Windows;
using WinTools.Core.Localization;
using WinTools.Windows;
using WinTools.Windows.Settings;

namespace WinTools.Settings;

/// <summary>
/// The WinTools window's process: one window, one instance. Usage: WinTools.Settings [--settings &lt;file&gt;] [--page
/// home|howto|features|settings|about]. The host passes the file it uses. A second start brings the open window to the
/// front instead of opening another. The language comes from the settings (or Windows); when it changes, the window is
/// built again in it, on the same page.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The mutex lives as long as the process and is disposed in OnExit.")]
public partial class App : Application
{
    private Mutex? _single;
    private SettingsStore? _store;
    private string _hostPath = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(initiallyOwned: true, @"Local\WinTools.Settings", out var first);
        if (!first)
        {
            var existing = Native.FindWindowW(null, global::WinTools.Settings.MainWindow.WindowTitle);
            if (existing != 0) { Native.ShowWindow(existing, 9 /* SW_RESTORE */); Native.SetForegroundWindow(existing); }
            Shutdown(0);
            return;
        }
        string? path = null;
        var page = "home";
        for (var i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--settings" && i + 1 < e.Args.Length) path = Path.GetFullPath(e.Args[++i]);
            else if (e.Args[i] == "--page" && i + 1 < e.Args.Length) page = e.Args[++i];
        }
        _store = path is null ? SettingsStore.Default() : new SettingsStore(path);
        _hostPath = Path.Combine(AppContext.BaseDirectory, "WinTools.Host.exe");
        UiText.Use(_store.Load().Language, SystemPreferences.UiLanguageTag());
        Open(page);
    }

    private void Open(string page, MainWindow? previous = null)
    {
        // Words the shared styles show (the switch's state), in the language in use.
        Resources["SwitchOn"] = UiText.T("switch.on");
        Resources["SwitchOff"] = UiText.T("switch.off");
        var window = new MainWindow(_store!, _hostPath, page);
        window.LanguageSwitched += language =>
        {
            UiText.Use(language, SystemPreferences.UiLanguageTag());
            window.Dispatcher.BeginInvoke(() => Open(window.SettingsPage.Visibility == Visibility.Visible ? "settings" : "home", window));
        };
        MainWindow = window;
        if (previous is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            (window.Left, window.Top, window.Width, window.Height) = (previous.Left, previous.Top, previous.Width, previous.Height);
        }
        window.Show();
        window.Activate();
        previous?.Close();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }
}

internal static partial class Native
{
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial nint FindWindowW(string? className, string windowName);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ShowWindow(nint hwnd, int cmd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(nint hwnd);
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE (20): a dark title bar to match the window (Windows 10 20H1 and later; ignored before).</summary>
    [LibraryImport("dwmapi.dll")] public static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
