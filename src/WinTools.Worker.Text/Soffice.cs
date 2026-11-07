using System.Globalization;
using System.Security;

namespace WinTools.Worker.Text;

/// <summary>
/// How the user's LibreOffice is run for one conversion (ADR-0033). Pure: the arguments, the environment and the
/// throw-away profile are built here and run by <see cref="OfficeJobs"/>, so each can be checked by a test without
/// LibreOffice.
/// <list type="bullet">
/// <item><b>Arguments</b>: headless, no logo, no default document, no first-start wizard, no recovery, no lock check;
/// "--convert-to pdf" into "out", the input a fixed name ("in\source.docx") in the work folder, which is the working
/// directory: no text of the user's ever reaches the command line.</item>
/// <item><b>A fresh profile</b> in the work folder (<c>-env:UserInstallation</c>), so the user's own LibreOffice, its
/// settings, macros and recent files are never touched, and an open LibreOffice window does not take the job.</item>
/// <item><b>The profile's settings</b> (<see cref="ProfileSettings"/>): macros never run (security level "very high"
/// and macro execution disabled), linked content of untrusted documents blocked, OLE objects and DDE disabled, links
/// never updated on load in Writer and Calc, formulas never recalculated on load, the update check off, and every proxy
/// set to a closed local port.</item>
/// <item><b>Text and CSV</b> are decoded by the worker, written again as UTF-8, and opened with an explicit filter, so
/// LibreOffice never guesses their type (a .txt holding HTML stays text). A CSV's "=" cells are imported as text,
/// never as formulas.</item>
/// <item><b>The environment</b>: only what a Windows program needs, TEMP in the work folder, and the proxy variables
/// libcurl reads pointing at the same closed port.</item>
/// </list>
/// </summary>
internal static class Soffice
{
    /// <summary>The local port every proxy points at: the discard port, which nothing on a desktop serves.</summary>
    public const int DeadProxyPort = 9;

    public const string InputFolder = "in", OutputFolder = "out", ProfileFolder = "profile", TempFolder = "tmp";

    /// <summary>The input's fixed name in the work folder ("source.docx"): the extension the kind gives, lower case.</summary>
    public static string InputName(string extension) => "source" + extension.ToLowerInvariant();

    /// <summary>The PDF LibreOffice writes for that input: "out\source.pdf".</summary>
    public static string OutputName => Path.Combine(OutputFolder, "source.pdf");

    /// <summary>The import filter for decoded text: plain text and CSV are never type-detected.</summary>
    public static string? InputFilter(string extension, char csvSeparator = ',') => extension.ToLowerInvariant() switch
    {
        ".txt" => "--infilter=Text (encoded):UTF8",
        // Separator, quote, UTF-8 (76), first line, no column formats, default language, quoted not forced to text, no
        // special numbers, (two export tokens), no trimming, (export token), formulas imported as text (token 13 false),
        // (export token), no scientific notation (LibreOffice help, "CSV Filter Options").
        ".csv" => $"--infilter=Text - txt - csv (StarCalc):{(int)csvSeparator},34,76,1,,0,false,false,false,false,false,0,false,false,false",
        _ => null,
    };

    public static IReadOnlyList<string> Arguments(string workFolder, string inputName, string? inputFilter)
    {
        List<string> args =
        [
            "-env:UserInstallation=" + new Uri(Path.Combine(workFolder, ProfileFolder) + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/'),
            "--headless", "--invisible", "--nologo", "--nodefault", "--nofirststartwizard", "--norestore", "--nolockcheck",
        ];
        if (inputFilter is not null) args.Add(inputFilter);
        args.AddRange(["--convert-to", "pdf", "--outdir", OutputFolder, Path.Combine(InputFolder, inputName)]);
        return args;
    }

    /// <summary>
    /// The profile's registrymodifications.xcu: each setting is an existing key of LibreOffice's configuration schema
    /// (officecfg, Common.xcs, Writer.xcs, Calc.xcs, Inet.xcs), written with oor:op="fuse" as LibreOffice writes them.
    /// </summary>
    public static IReadOnlyList<(string Path, string Name, string Value)> ProfileSettings { get; } =
    [
        ("/org.openoffice.Office.Common/Security/Scripting", "MacroSecurityLevel", "3"), // 3: very high, only signed macros from trusted places
        ("/org.openoffice.Office.Common/Security/Scripting", "DisableMacrosExecution", "true"), // no Basic, Python, JavaScript or BeanShell at all
        ("/org.openoffice.Office.Common/Security/Scripting", "BlockUntrustedRefererLinks", "true"), // no linked image or file from a document outside trusted places
        ("/org.openoffice.Office.Common/Security/Scripting", "DisableActiveContent", "true"), // no OLE objects and no DDE
        ("/org.openoffice.Office.Common/Security/Scripting", "DisableOLEAutomation", "true"),
        ("/org.openoffice.Office.Writer/Content/Update", "Link", "2"), // Writer: 2 = never update links
        ("/org.openoffice.Office.Calc/Content/Update", "Link", "1"), // Calc: 1 = never update links
        ("/org.openoffice.Office.Calc/Formula/Load", "OOXMLRecalcMode", "1"), // 1 = never recalculate on load
        ("/org.openoffice.Office.Calc/Formula/Load", "ODFRecalcMode", "1"),
        ("/org.openoffice.Inet/Settings", "ooInetProxyType", "2"), // 2 = manual: the proxies below
        ("/org.openoffice.Inet/Settings", "ooInetHTTPProxyName", "127.0.0.1"),
        ("/org.openoffice.Inet/Settings", "ooInetHTTPProxyPort", DeadProxyPort.ToString(CultureInfo.InvariantCulture)),
        ("/org.openoffice.Inet/Settings", "ooInetHTTPSProxyName", "127.0.0.1"),
        ("/org.openoffice.Inet/Settings", "ooInetHTTPSProxyPort", DeadProxyPort.ToString(CultureInfo.InvariantCulture)),
        ("/org.openoffice.Inet/Settings", "ooInetNoProxy", ""),
        ("/org.openoffice.Office.Jobs/Jobs/org.openoffice.Office.Jobs:Job['UpdateCheck']/Arguments", "AutoCheckEnabled", "false"),
    ];

    public static string RegistryModifications()
    {
        var items = string.Concat(ProfileSettings.Select(s =>
            $"<item oor:path=\"{SecurityElement.Escape(s.Path)}\"><prop oor:name=\"{s.Name}\" oor:op=\"fuse\"><value>{SecurityElement.Escape(s.Value)}</value></prop></item>\n"));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<oor:items xmlns:oor=\"http://openoffice.org/2001/registry\" xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n"
            + items + "</oor:items>\n";
    }

    /// <summary>The variables a Windows program needs to start, copied from the worker's own environment when set.</summary>
    private static readonly string[] Kept =
    [
        "SystemRoot", "windir", "SystemDrive", "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles",
        "CommonProgramFiles(x86)", "CommonProgramW6432", "PATH", "PATHEXT", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS",
        "USERPROFILE", "APPDATA", "LOCALAPPDATA", "ALLUSERSPROFILE", "USERNAME", "HOMEDRIVE", "HOMEPATH",
    ];

    public static IReadOnlyDictionary<string, string> Environment(string workFolder, Func<string, string?> current)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in Kept) if (current(k) is { Length: > 0 } v) env[k] = v;
        env["TEMP"] = env["TMP"] = Path.Combine(workFolder, TempFolder);
        var dead = "http://127.0.0.1:" + DeadProxyPort.ToString(CultureInfo.InvariantCulture);
        foreach (var k in new[] { "http_proxy", "https_proxy", "all_proxy", "ftp_proxy" }) env[k] = dead; // Windows names ignore case
        return env;
    }
}
