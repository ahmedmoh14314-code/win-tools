using System.IO;
using Spike.Interaction.Core.Input;

namespace Spike.Interaction.Win32;

/// <summary>Command-line options shared by the Win32 and WPF overlay hosts.</summary>
public sealed record SpikeOptions(
    TriggerSpec Trigger,
    bool SwallowTrigger,
    bool RequireDrag,
    int Radius96,
    int Sectors,
    string LogPath,
    bool Echo,
    int StatsIntervalSeconds,
    bool HideOnRelease,
    int DeadZoneAlpha,
    string GcPressure,
    bool RawInput,
    bool GcOnStats)
{
    public static SpikeOptions Parse(string[] args, string defaultLogName)
    {
        var trigger = "space";
        bool swallow = false, requireDrag = true, echo = true;
        int radius = 110, sectors = 4, statsInterval = 10, deadZoneAlpha = -1; bool hideOnRelease = false, rawInput = false, gcOnStats = false; var gcPressure = "none";
        string? log = null;

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value after {args[i]}");
            switch (args[i])
            {
                case "--trigger": trigger = Next(); break;
                case "--swallow": swallow = true; break;
                case "--no-require-drag": requireDrag = false; break;
                case "--radius": radius = int.Parse(Next()); break;
                case "--sectors": sectors = int.Parse(Next()); break;
                case "--log": log = Next(); break;
                case "--quiet": echo = false; break;
                case "--stats-interval": statsInterval = int.Parse(Next()); break;
                case "--hide-on-release": hideOnRelease = true; break; // negative control: hide on the hook's button-up (the behaviour that lost drops)
                case "--deadzone-alpha": deadZoneAlpha = int.Parse(Next()); break; // WPF: paint the dead zone with this alpha (0..255); -1 = leave it unpainted
                case "--gc-pressure": gcPressure = Next(); break;              // none | churn | extreme (Spike 002B E1)
                case "--raw-input": rawInput = true; break;                     // Win32 host: Raw Input probe (Spike 002B E2)
                case "--gc-on-stats": gcOnStats = true; break;                 // full GC before each stats sample (memory-growth check only)
                case "-h" or "--help":
                    Console.WriteLine("Options: --trigger <space|ctrl+space|mbutton|xbutton1|f8|...> --swallow --no-require-drag --radius <px96> --sectors <n> --log <path> --quiet --stats-interval <s>");
                    Environment.Exit(0); break;
                default: throw new ArgumentException($"Unknown option {args[i]}");
            }
        }

        log ??= Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "results", $"{defaultLogName}-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        return new SpikeOptions(TriggerSpec.Parse(trigger), swallow, requireDrag, radius, sectors, Path.GetFullPath(log), echo, statsInterval, hideOnRelease, deadZoneAlpha, gcPressure, rawInput, gcOnStats);
    }
}
