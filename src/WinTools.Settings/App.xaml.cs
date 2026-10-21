using System.Runtime.InteropServices;
using System.Windows;
using WinTools.Windows.Settings;

namespace WinTools.Settings;

/// <summary>
/// The settings process: one window, one instance. Usage: WinTools.Settings [--settings &lt;file&gt;] (the host passes the
/// file it uses). A second start brings the open window to the front instead of opening another.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The mutex lives as long as the process and is disposed in OnExit.")]
public partial class App : Application
{
    private Mutex? _single;

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
        for (var i = 0; i < e.Args.Length; i++)
            if (e.Args[i] == "--settings" && i + 1 < e.Args.Length) path = Path.GetFullPath(e.Args[++i]);
        var store = path is null ? SettingsStore.Default() : new SettingsStore(path);
        var hostPath = Path.Combine(AppContext.BaseDirectory, "WinTools.Host.exe");
        var window = new MainWindow(store, hostPath);
        MainWindow = window;
        window.Show();
        window.Activate();
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
