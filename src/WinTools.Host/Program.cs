using WinTools.Host;
using WinTools.Host.Jobs;
using WinTools.Windows;

// Resident host. Composition root only: wiring, lifetime and the message loop. Runs as the current user; never elevated.

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

        using var single = new Mutex(initiallyOwned: true, @"Local\WinTools.Host", out var firstInstance);
        if (!firstInstance) { Console.Error.WriteLine("WinTools is already running."); return 2; }

        var workerPath = Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.exe");
        if (!File.Exists(workerPath)) { Console.Error.WriteLine($"Worker not found next to the host: {workerPath}"); return 3; }

        using var log = HostLog.Open(logPath);
        var store = settingsPath is null ? SettingsStore.Default() : new SettingsStore(settingsPath);
        var settings = store.Load();
        log.Event("settings", new { store.LastLoad, store.ReadOnly, settings.TriggerKey, output = settings.OutputPolicy.ToString(), settings.JpegQuality, settings.WebPQuality });
        if (store.LastLoad == "defaults (no file)") store.Save(settings); // first run: write the defaults so the file can be edited
        OleApartment.Initialize();
        try
        {
            var jobs = new JobRunner(new WorkerClient(workerPath, JobTimeout), log, settings, MessageWindow.FailureBeep, MessageWindow.InfoBeep,
                facts => MessageWindow.ShowInfo("Image info", InfoText.Format(facts)));
            using var coordinator = new InteractionCoordinator(log, jobs, settings.TriggerVirtualKey);
            coordinator.Start();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; coordinator.RequestClose(); };
            log.Event("host.start", new { pid = Environment.ProcessId, runtime = Environment.Version.ToString(), log = log.Path });
            Console.WriteLine("WinTools is running. Drag a PNG or JPEG, press Space, drop it on a command. Ctrl+C exits.");
            Console.WriteLine($"Log: {log.Path}");
            MessageWindow.RunLoop();
            log.Event("host.stop");
        }
        finally
        {
            OleApartment.Uninitialize();
        }
        return 0;
    }
}
