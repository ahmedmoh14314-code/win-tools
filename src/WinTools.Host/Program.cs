using System.Diagnostics;
using WinTools.Core.Feedback;
using WinTools.Core.Settings;
using WinTools.Host;
using WinTools.Host.Jobs;
using WinTools.Windows;
using WinTools.Windows.Feedback;
using WinTools.Windows.Settings;
using WinTools.Windows.Shell;

// Resident host. Composition root only: wiring, lifetime and the message loop. Runs as the current user; never elevated.
// A windowless (WinExe) process: its only visible parts are the radial menu, the toasts, the info panel and the tray icon.

internal static class Program
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(60);

    [STAThread]
    private static int Main(string[] args)
    {
        string? logPath = null, settingsPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--log" && i + 1 < args.Length) logPath = Path.GetFullPath(args[++i]);
            else if (args[i] == "--settings" && i + 1 < args.Length) settingsPath = Path.GetFullPath(args[++i]);
            else { Console.Error.WriteLine("Usage: WinTools.Host [--log <path>] [--settings <path>]"); return 64; }
        }

        // Already running: the first instance's tray icon is there; a second start is a no-op (exit code 2).
        using var single = new Mutex(initiallyOwned: true, @"Local\WinTools.Host", out var firstInstance);
        if (!firstInstance) return 2;

        var workerPath = Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.exe");
        if (!File.Exists(workerPath))
        {
            MessageWindow.ShowStartupError("WinTools cannot start: WinTools.Worker.exe is missing from its folder. Reinstall or copy the complete folder.");
            return 3;
        }

        using var log = HostLog.Open(logPath);
        var store = settingsPath is null ? SettingsStore.Default() : new SettingsStore(settingsPath);
        var settings = store.Load();
        log.Event("settings", new { store.LastLoad, store.ReadOnly, settings.TriggerKey, output = settings.OutputPolicy.ToString(), settings.JpegQuality, settings.WebPQuality, settings.UiSounds, settings.Animations });
        if (store.LastLoad == "defaults (no file)") // first run: write the defaults so the file exists for the settings window
            log.Event("settings.save", new { ok = store.Save(settings) });
        OleApartment.Initialize();
        try
        {
            InteractionCoordinator? coordinator = null;
            using var window = new MessageWindow((m, w, l) => coordinator?.OnMessage(m, w, l) ?? false);
            using var sounds = new SoundPlayer(settings.UiSounds);
            sounds.Warm();
            using var toast = new ToastWindow();
            using var info = new InfoPanel();
            var feedback = new FeedbackPresenter(window, toast, info, sounds, log);
            var current = settings;
            toast.ReducedMotionSource = () => Volatile.Read(ref current).ReducedMotion(SystemPreferences.ReducedMotion());
            var jobs = new JobRunner(new WorkerClient(workerPath, JobTimeout), log, () => Volatile.Read(ref current), feedback);
            coordinator = new InteractionCoordinator(log, window, jobs, feedback, sounds, settings);
            using (coordinator)
            {
                coordinator.Start();
                using var tray = new TrayIcon(Tooltip(settings),
                [
                    new("Settings…", () => OpenSettings(store.Path, toast, log), IsDefault: true),
                    new("Open logs folder", () => OpenFolder(Path.GetDirectoryName(log.Path)!)),
                    TrayCommand.Separator,
                    new("Exit WinTools", window.RequestClose),
                ]);
                // The settings window saves the file and raises the signal; the host reloads and applies it at once.
                using var listener = SettingsSignal.Listen(() => window.Post(() =>
                {
                    var changed = store.Load();
                    Volatile.Write(ref current, changed);
                    sounds.Enabled = changed.UiSounds;
                    coordinator.ApplySettings(changed);
                    tray.SetTooltip(Tooltip(changed));
                    log.Event("settings.reload", new { store.LastLoad, changed.TriggerKey, output = changed.OutputPolicy.ToString(), changed.JpegQuality, changed.WebPQuality, changed.UiSounds, changed.Animations });
                }));
                log.Event("host.start", new { pid = Environment.ProcessId, runtime = Environment.Version.ToString(), log = log.Path, tray = tray.Visible });
                MessageWindow.RunLoop();
                log.Event("host.stop");
            }
        }
        finally
        {
            OleApartment.Uninitialize();
        }
        return 0;
    }

    private static string Tooltip(AppSettings s) => $"WinTools: drag images, press {s.TriggerKey}";

    /// <summary>Starts the settings window (a separate process, so WPF never loads into the resident host).</summary>
    private static void OpenSettings(string settingsFile, ToastWindow toast, HostLog log)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "WinTools.Settings.exe");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
                ArgumentList = { "--settings", settingsFile },
            });
            log.Event("settings.open", new { ok = p is not null });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Event("settings.open", new { ok = false, type = ex.GetType().Name });
            var (x, y) = SystemPreferences.PointerPosition();
            toast.Show(new ToastContent(FeedbackKind.Failure, "Settings could not be opened", "WinTools.Settings.exe is missing from this copy."), x, y);
        }
    }

    /// <summary>Opens a folder of our own (the logs) in Explorer, at the user's request from the tray menu.</summary>
    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        using var _ = Process.Start(new ProcessStartInfo(explorer) { UseShellExecute = false, ArgumentList = { folder } });
    }
}
