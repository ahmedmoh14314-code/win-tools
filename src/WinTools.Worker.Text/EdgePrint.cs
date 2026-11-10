namespace WinTools.Worker.Text;

/// <summary>
/// How Edge prints one web page to PDF (HTML to PDF). Pure, so a test checks every switch:
/// <list type="bullet">
/// <item>headless, with a fresh profile in the work folder (the user's own Edge, its sign-in, history and extensions are
/// never used, and an open Edge window does not take the job);</item>
/// <item>scripts off by the profile's own setting (<see cref="Preferences"/>: JavaScript blocked for every site; the
/// <c>--blink-settings</c> switch stops Edge printing at all), extensions, sync, background networking and component
/// updates off;</item>
/// <item>no network: every host name maps to nothing and every proxy is the closed local port, so the page fetches
/// nothing; files beside it on this PC still load;</item>
/// <item>the PDF written into "out", without the header and footer a browser adds.</item>
/// </list>
/// </summary>
internal static class EdgePrint
{
    /// <summary>The fresh profile's Default\Preferences: JavaScript blocked (content setting 2) for every page.</summary>
    public const string Preferences = "{\"profile\":{\"default_content_setting_values\":{\"javascript\":2}}}";

    public static IReadOnlyList<string> Arguments(string workFolder, string pageUrl) =>
    [
        "--headless",
        "--user-data-dir=" + Path.Combine(workFolder, Soffice.ProfileFolder),
        "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-sync",
        "--disable-background-networking", "--disable-component-update", "--disable-default-apps", "--disable-gpu",
        "--host-resolver-rules=MAP * ~NOTFOUND",
        "--proxy-server=127.0.0.1:" + Soffice.DeadProxyPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--no-pdf-header-footer",
        "--print-to-pdf=" + Path.Combine(workFolder, Soffice.OutputName()),
        pageUrl,
    ];
}
