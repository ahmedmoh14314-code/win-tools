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

    /// <summary>Per-file limits of the General Files worker: File info reads 4 KB; a hash of a large file on a slow disk or
    /// a share takes minutes.</summary>
    private static readonly TimeSpan FilesInfoTimeout = TimeSpan.FromSeconds(30), FilesHashTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Per-request limit of the PDF worker: a merge of many files runs one qpdf per file, each limited in the worker.</summary>
    private static readonly TimeSpan PdfTimeout = TimeSpan.FromMinutes(10);

    /// <summary>How long the exit waits for cancelled jobs: the workers' 1 s grace plus temp cleanup.</summary>
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(3);

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
        var textWorkerPath = Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.Text.exe");
        var pdfWorkerPath = Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.Pdf.exe");
        // The image worker is required; without the General Files worker the host still runs, and those commands fail
        // with a plain reason (the worker client reports a worker that cannot start).
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
            window.OnError = ex => log.Event("ui.error", new { type = ex.GetType().Name }); // the type only: messages can hold full paths
            using var sounds = new SoundPlayer(settings.UiSounds);
            sounds.Warm();
            using var toast = new ToastWindow();
            using var info = new InfoPanel();
            var feedback = new FeedbackPresenter(window, toast, info, sounds, log);
            var current = settings;
            toast.ReducedMotionSource = () => Volatile.Read(ref current).ReducedMotion(SystemPreferences.ReducedMotion());
            var images = new JobModuleRunner(ImageJobModule.Instance, new WorkerClient(workerPath, JobTimeout, ImageJobModule.Instance), ImageJobModule.Parameters);
            var files = new JobModuleRunner(FilesJobModule.Instance,
                new FilesExecutor(new WorkerClient(textWorkerPath, FilesInfoTimeout, FilesJobModule.Instance),
                    new WorkerClient(textWorkerPath, FilesHashTimeout, FilesJobModule.Instance, parallelism: 1), // one disk read at a time
                    text => CopyTextAsync(window, text)), FilesJobModule.Parameters);
            // PDF jobs: at most two workers (each runs qpdf, which can use a lot of memory on a large file; V2 plan §13).
            var pdf = new JobModuleRunner(PdfJobModule.Instance, new WorkerClient(pdfWorkerPath, PdfTimeout, PdfJobModule.Instance, parallelism: 2), PdfJobModule.Parameters);
            var jobs = new JobScheduler([images, files, pdf], log, () => Volatile.Read(ref current), feedback);
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
                ], ex => log.Event("ui.error", new { type = ex.GetType().Name }));
                // The settings window saves the file and raises the signal; the host reloads and applies it at once.
                using var listener = SettingsSignal.TryListen(() => window.Post(() =>
                {
                    var changed = store.Load();
                    // Only a file that was read is applied. A failed read (locked, missing, corrupt) falls back to defaults
                    // in Load, which must not silently reset the running host's trigger key or output folder.
                    if (!store.LastLoad.StartsWith("loaded", StringComparison.Ordinal) && !store.LastLoad.StartsWith("newer schema", StringComparison.Ordinal))
                    {
                        log.Event("settings.reload", new { store.LastLoad, applied = false });
                        return;
                    }
                    Volatile.Write(ref current, changed);
                    sounds.Enabled = changed.UiSounds;
                    sounds.Warm();
                    coordinator.ApplySettings(changed);
                    tray.SetTooltip(Tooltip(changed));
                    log.Event("settings.reload", new { store.LastLoad, applied = true, changed.TriggerKey, output = changed.OutputPolicy.ToString(), changed.JpegQuality, changed.WebPQuality, changed.UiSounds, changed.Animations });
                }));
                if (listener is null) log.Event("settings.listen", new { ok = false }); // runs on; changes then apply at the next start
                log.Event("host.start", new { pid = Environment.ProcessId, runtime = Environment.Version.ToString(), log = log.Path, tray = tray.Visible });
                MessageWindow.RunLoop();
                // Exiting: queued jobs never start; running ones stop, their workers get the cancel message and its grace, and
                // their temps are cleaned. Waited for a bounded time; the job object ends any worker still alive after that.
                var stopped = jobs.CancelAll().Wait(ExitGrace);
                log.Event("host.stop", new { jobsStopped = stopped });
            }
        }
        finally
        {
            OleApartment.Uninitialize();
        }
        return 0;
    }

    private static string Tooltip(AppSettings s) => $"WinTools: drag files, press {s.TriggerKey}";

    /// <summary>
    /// Copy path's clipboard write (V2 PR 1.4). It runs on the UI thread, which owns the window the clipboard needs, and
    /// is tried a few times while another program holds the clipboard; the waiting happens here, never on the UI thread.
    /// </summary>
    private static async Task<bool> CopyTextAsync(MessageWindow window, string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var abandoned = 0;
            // A post that runs after the wait gave up must not copy: the user was already told it failed (evaluator, PR #18).
            window.Post(() => done.TrySetResult(Volatile.Read(ref abandoned) == 0 && WinTools.Windows.Shell.ClipboardText.TrySet(window.Handle, text)));
            try { if (await done.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false)) return true; }
            catch (TimeoutException) { Volatile.Write(ref abandoned, 1); return false; } // the UI thread is busy or gone (exiting)
            await Task.Delay(60).ConfigureAwait(false);
        }
        return false;
    }

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
        try
        {
            Directory.CreateDirectory(folder);
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var _ = Process.Start(new ProcessStartInfo(explorer) { UseShellExecute = false, ArgumentList = { folder } });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
    }
}
