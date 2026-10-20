using Spike.Interaction.Core.Native;
using Spike.Interaction.Core.Telemetry;
using Spike.Interaction.Win32;

// Raw Win32 host for Spike 001: hooks + overlay + OLE drop target, no UI framework.
internal static class Program
{
    [STAThread] // OleInitialize (needed by RegisterDragDrop) requires a single-threaded apartment.
    private static int Main(string[] args)
    {
        var opt = SpikeOptions.Parse(args, "win32");

        // Must happen before any window is created; the manifest also declares PerMonitorV2.
        User32.SetProcessDpiAwarenessContext(DpiAwarenessContext.PER_MONITOR_AWARE_V2);

        // RegisterDragDrop requires OleInitialize (not CoInitialize) on the window's STA thread.
        var hr = Ole32.OleInitialize(0);
        if (hr < 0) { Console.Error.WriteLine($"OleInitialize failed: 0x{hr:X8}"); return 2; }

        using var log = new SpikeLog(opt.LogPath, opt.Echo);
        using var host = new SpikeHost(opt, log);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; host.RequestStop(); };
        try
        {
            host.Run();
            return 0;
        }
        finally
        {
            Ole32.OleUninitialize();
        }
    }
}
