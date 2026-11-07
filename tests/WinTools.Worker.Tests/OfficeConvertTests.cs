using System.Diagnostics;
using System.Text;
using System.Xml;
using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;
using WinTools.Worker.Text;

namespace WinTools.Worker.Tests;

/// <summary>
/// ADR-0033: Office to PDF through the user's LibreOffice. LibreOffice is not installed on the build machine, so the
/// adapter is tested with a stand-in that plays LibreOffice's part (writes a PDF, writes nothing, hangs, writes junk) and
/// records how it was asked: the arguments, the throw-away profile and its settings, the environment, the input it was
/// given. The checks after it (the bundled qpdf, the naming, the host's) are the real ones. A run on a PC with
/// LibreOffice is the owner's end-to-end check.
/// </summary>
public sealed class OfficeConvertTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();

    /// <summary>A stand-in for LibreOffice: what it was asked, and what it does in the work folder.</summary>
    private sealed class FakeOffice(Action<string>? act, string? soffice = @"C:\Program Files\LibreOffice\program\soffice.exe", bool timeOut = false) : IOfficeEngine
    {
        public IReadOnlyList<string>? Arguments { get; private set; }
        public IReadOnlyDictionary<string, string>? Environment { get; private set; }
        public string? Profile { get; private set; }
        public byte[]? Input { get; private set; }
        public string? WorkFolder { get; private set; }
        public string? Executable { get; private set; }

        public string? Locate() => soffice;

        public ProgramRun Convert(string executable, IReadOnlyList<string> arguments, string workFolder, IReadOnlyDictionary<string, string> environment, TimeSpan timeout)
        {
            (Executable, Arguments, WorkFolder, Environment) = (executable, arguments, workFolder, environment);
            Profile = File.ReadAllText(Path.Combine(workFolder, "profile", "user", "registrymodifications.xcu"));
            Input = File.ReadAllBytes(Path.Combine(workFolder, arguments[^1]));
            if (timeOut) return new ProgramRun(-1, "", TimedOut: true);
            act?.Invoke(workFolder);
            return new ProgramRun(0, "", TimedOut: false);
        }

        public ProgramRun? Qpdf(IReadOnlyList<string> arguments, string workFolder) => InstalledOfficeEngine.Instance.Qpdf(arguments, workFolder);
    }

    private static Action<string> WritesPdf => work => File.Copy(Path.Combine(AppContext.BaseDirectory, "samples", "pages-3.pdf"), Path.Combine(work, "out", "source.pdf"));

    private static JobRequest Request(string path) => new(Guid.NewGuid().ToString("N"), OfficeCapabilities.ToPdfId, [path]);

    private static JobResult Run(JobRequest request, IOfficeEngine engine)
    {
        var r = OfficeJobs.Run(request, engine, TimeSpan.FromSeconds(30));
        Assert.True(OfficeJobModule.Instance.IsBelievable(request, r), $"not believable: {r.Error} {r.Message}");
        Assert.False(Directory.Exists(OfficeJobs.WorkFolderOf(request.Id)), "the work folder was left behind");
        return r;
    }

    private string Docx(string name = "report.docx")
    {
        var p = Path.Combine(_dir.Path, name);
        OoxmlSamples.Write(p, OoxmlSamples.Docx("<w:p><w:r><w:t>مرحبا</w:t></w:r></w:p>"));
        return p;
    }

    [Fact]
    public void A_document_becomes_a_checked_pdf_beside_it_and_the_original_is_untouched()
    {
        var docx = Docx();
        var before = Hash.Of(docx);
        var office = new FakeOffice(WritesPdf);
        var request = Request(docx);
        var r = Run(request, office);
        Assert.True(r.Success, r.Message);
        Assert.Equal("report.pdf", Path.GetFileName(r.OutputPath));
        Assert.Equal("3", r.Facts.Single(f => f.Label == "Pages").Value);
        Assert.Equal(before, Hash.Of(docx));
        Assert.Equal(File.ReadAllBytes(docx), office.Input); // a copy, under a fixed name, in the work folder
        Assert.Equal(["report.docx", "report.pdf"], _dir.Names()); // no lock file or temp next to the original
    }

    [Fact]
    public void LibreOffice_is_run_headless_with_a_fresh_profile_and_no_user_text_on_the_command_line()
    {
        var docx = Docx("تقرير خاص & co.docx");
        var office = new FakeOffice(WritesPdf);
        var request = Request(docx);
        Assert.True(Run(request, office).Success);
        var args = office.Arguments!;
        Assert.StartsWith("-env:UserInstallation=file:///", args[0], StringComparison.Ordinal);
        Assert.Equal(new Uri(Path.Combine(office.WorkFolder!, "profile")).AbsoluteUri, args[0]["-env:UserInstallation=".Length..]);
        foreach (var flag in new[] { "--headless", "--invisible", "--nologo", "--nodefault", "--nofirststartwizard", "--norestore", "--nolockcheck" })
            Assert.Contains(flag, args);
        Assert.Equal(["--convert-to", "pdf", "--outdir", "out", Path.Combine("in", "source.docx")], args.TakeLast(5));
        Assert.DoesNotContain(args, a => a.Contains("تقرير", StringComparison.Ordinal) || a.Contains(_dir.Path, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"C:\Program Files\LibreOffice\program\soffice.exe", office.Executable);
    }

    [Fact]
    public void The_profile_disables_macros_blocks_links_and_points_every_proxy_at_a_closed_port()
    {
        var office = new FakeOffice(WritesPdf);
        Assert.True(Run(Request(Docx()), office).Success);
        var xml = new XmlDocument { XmlResolver = null };
        xml.LoadXml(office.Profile!);
        var ns = new XmlNamespaceManager(xml.NameTable);
        ns.AddNamespace("oor", "http://openoffice.org/2001/registry");
        string Value(string path, string name) =>
            xml.SelectSingleNode($"//item[@oor:path='{path}']/prop[@oor:name='{name}' and @oor:op='fuse']/value", ns)?.InnerText ?? "(missing)";
        Assert.Equal("3", Value("/org.openoffice.Office.Common/Security/Scripting", "MacroSecurityLevel"));
        Assert.Equal("true", Value("/org.openoffice.Office.Common/Security/Scripting", "DisableMacrosExecution"));
        Assert.Equal("true", Value("/org.openoffice.Office.Common/Security/Scripting", "BlockUntrustedRefererLinks"));
        Assert.Equal("true", Value("/org.openoffice.Office.Common/Security/Scripting", "DisableActiveContent"));
        Assert.Equal("2", Value("/org.openoffice.Office.Writer/Content/Update", "Link")); // Writer: never
        Assert.Equal("1", Value("/org.openoffice.Office.Calc/Content/Update", "Link")); // Calc: never
        Assert.Equal("1", Value("/org.openoffice.Office.Calc/Formula/Load", "OOXMLRecalcMode"));
        Assert.Equal("2", Value("/org.openoffice.Inet/Settings", "ooInetProxyType"));
        Assert.Equal("127.0.0.1", Value("/org.openoffice.Inet/Settings", "ooInetHTTPSProxyName"));
        Assert.Equal("9", Value("/org.openoffice.Inet/Settings", "ooInetHTTPProxyPort"));
        var env = office.Environment!;
        Assert.Equal("http://127.0.0.1:9", env["https_proxy"]);
        Assert.Equal(Path.Combine(office.WorkFolder!, "tmp"), env["TEMP"]);
    }

    [Fact]
    public void The_environment_carries_only_what_windows_needs()
    {
        var current = new Dictionary<string, string> { ["SystemRoot"] = @"C:\Windows", ["PATH"] = @"C:\Windows", ["GITHUB_TOKEN"] = "secret", ["TEMP"] = @"C:\elsewhere" };
        var env = Soffice.Environment(@"C:\w", k => current.GetValueOrDefault(k));
        Assert.False(env.ContainsKey("GITHUB_TOKEN"));
        Assert.Equal(@"C:\w\tmp", env["TEMP"]);
        Assert.Equal(@"C:\Windows", env["SystemRoot"]);
    }

    [Fact]
    public void Text_and_csv_are_handed_over_as_utf8_with_an_explicit_filter_and_formulas_as_text()
    {
        var office = new FakeOffice(WritesPdf);
        var txt = _dir.Write("notes.txt", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("<html><img src=http://x></html>")).ToArray());
        Assert.True(Run(Request(txt), office).Success);
        Assert.Contains("--infilter=Text (encoded):UTF8", office.Arguments!);
        Assert.Equal([0xEF, 0xBB, 0xBF, .. "<html><img src=http://x></html>"u8], office.Input);

        var csv = _dir.Write("data.csv", "sep=;\r\na;=WEBSERVICE(\"http://x\")\r\n"u8.ToArray());
        Assert.True(Run(Request(csv), office).Success);
        var filter = office.Arguments!.Single(a => a.StartsWith("--infilter=", StringComparison.Ordinal));
        Assert.Equal("--infilter=Text - txt - csv (StarCalc):59,34,76,1,,0,false,false,false,false,false,0,false,false,false", filter);
        Assert.Equal(15, filter.Split(":")[1].Split(",").Length);
        Assert.Equal("false", filter.Split(":")[1].Split(",")[12]); // token 13, "import as formulas"
        Assert.Equal("\uFEFFa;=WEBSERVICE(\"http://x\")\r\n"u8.ToArray(), office.Input); // the sep= line is not data
    }

    [Theory]
    [InlineData("letter.docx", "<html><body>not a document</body></html>", ErrorCode.ContentMismatch)]
    [InlineData("old.doc", "<html>also not one</html>", ErrorCode.ContentMismatch)]
    [InlineData("text.rtf", "plain text, not RTF", ErrorCode.ContentMismatch)]
    [InlineData("sheet.ods", "MZ", ErrorCode.ContentMismatch)]
    public void A_file_that_is_not_what_its_name_says_never_reaches_LibreOffice(string name, string content, ErrorCode expected)
    {
        var office = new FakeOffice(WritesPdf);
        var path = _dir.Write(name, Encoding.UTF8.GetBytes(content));
        var r = Run(Request(path), office);
        Assert.Equal(expected, r.Error);
        Assert.Null(office.Arguments);
        Assert.Equal([name], _dir.Names());
    }

    [Fact]
    public void A_password_protected_office_file_says_so_and_a_real_compound_doc_is_accepted()
    {
        byte[] compound = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0];
        var office = new FakeOffice(WritesPdf);
        Assert.Equal(ErrorCode.PasswordRequired, Run(Request(_dir.Write("locked.docx", compound)), office).Error);
        Assert.Null(office.Arguments);
        Assert.True(Run(Request(_dir.Write("old.doc", compound)), office).Success);
        Assert.True(Run(Request(_dir.Write("old.rtf", "{\\rtf1 hello}"u8.ToArray())), office).Success);
    }

    [Fact]
    public void Without_LibreOffice_the_command_fails_plainly_and_runs_nothing()
    {
        var office = new FakeOffice(WritesPdf, soffice: null);
        var r = Run(Request(Docx()), office);
        Assert.Equal(ErrorCode.UnsupportedOperation, r.Error);
        Assert.Null(office.Arguments);
    }

    public static TheoryData<string, ErrorCode> BadRuns => new()
    {
        { "nothing", ErrorCode.EncodeFailed },
        { "junk", ErrorCode.OutputValidationFailed },
        { "two", ErrorCode.OutputValidationFailed },
        { "damaged", ErrorCode.OutputValidationFailed },
        { "timeout", ErrorCode.WorkerTimeout },
    };

    [Theory]
    [MemberData(nameof(BadRuns))]
    public void A_run_that_does_not_give_one_good_pdf_saves_nothing(string what, ErrorCode expected)
    {
        Action<string> act = what switch
        {
            "junk" => w => File.WriteAllText(Path.Combine(w, "out", "source.pdf"), new string('x', 100)),
            "two" => w => { WritesPdf(w); File.WriteAllText(Path.Combine(w, "out", "other.pdf"), "%PDF-1.4"); },
            "damaged" => w => File.WriteAllBytes(Path.Combine(w, "out", "source.pdf"), [.. "%PDF-1.7\n"u8, .. new byte[200]]),
            _ => _ => { },
        };
        var docx = Docx();
        var before = Hash.Of(docx);
        var r = Run(Request(docx), new FakeOffice(act, timeOut: what == "timeout"));
        Assert.Equal(expected, r.Error);
        Assert.Equal(["report.docx"], _dir.Names());
        Assert.Equal(before, Hash.Of(docx));
    }

    [Fact]
    public void An_existing_pdf_is_never_overwritten()
    {
        var docx = Docx();
        var taken = _dir.Write("report.pdf", [1, 2, 3]);
        var r = Run(Request(docx), new FakeOffice(WritesPdf));
        Assert.Equal("report (1).pdf", Path.GetFileName(r.OutputPath));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(taken));
    }

    [Fact]
    public void The_host_believes_only_a_pdf_under_an_allowed_name_with_its_page_count()
    {
        var docx = Docx();
        var request = Request(docx);
        var r = OfficeJobs.Run(request, new FakeOffice(WritesPdf), TimeSpan.FromSeconds(30));
        Assert.True(OfficeJobModule.Instance.IsBelievable(request, r));
        Assert.False(OfficeJobModule.Instance.IsBelievable(request, r with { Facts = [] }));
        Assert.False(OfficeJobModule.Instance.IsBelievable(request, r with { Outputs = [r.Outputs[0] with { Path = Path.Combine(_dir.Path, "elsewhere.pdf") }] }));
        File.AppendAllText(r.OutputPath!, "tampered");
        Assert.False(OfficeJobModule.Instance.IsBelievable(request, r));
        Assert.Contains(Path.Combine(Path.GetTempPath(), OfficeCapabilities.WorkFolderName(request.Id)), OfficeJobModule.Instance.TempPathsOf(request));
    }

    [Fact]
    public void A_program_past_its_time_is_killed_with_the_processes_it_started()
    {
        // PowerShell starts a second PowerShell that sleeps and writes down its id: both must be gone when the limit passes
        // (LibreOffice's soffice.exe starts soffice.bin the same way).
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var env = new Dictionary<string, string> { ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")!, ["PATH"] = Environment.GetEnvironmentVariable("PATH")! };
        var watch = Stopwatch.StartNew();
        var run = ChildProcess.Run(powershell, ["-NoProfile", "-NonInteractive", "-Command",
            $"$c = Start-Process -NoNewWindow -PassThru -FilePath '{powershell}' -ArgumentList '-NoProfile','-Command','Start-Sleep 60'; Set-Content -Path child.txt -Value $c.Id; Start-Sleep 60"],
            _dir.Path, env, TimeSpan.FromSeconds(8));
        Assert.True(run.TimedOut);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
        var child = int.Parse(File.ReadAllText(Path.Combine(_dir.Path, "child.txt")).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(Gone(child), "the child process outlived the limit");
        Assert.Throws<ArgumentException>(() => ChildProcess.Run(powershell, ["a\nb"], _dir.Path, env, TimeSpan.FromSeconds(1)));
    }

    private static bool Gone(int pid)
    {
        for (var i = 0; i < 20; i++)
        {
            try { using var p = Process.GetProcessById(pid); if (p.HasExited) return true; }
            catch (ArgumentException) { return true; }
            Thread.Sleep(100);
        }
        return false;
    }
}
