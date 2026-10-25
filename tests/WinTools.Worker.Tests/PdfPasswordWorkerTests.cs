using System.Security.Cryptography;
using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;
using WinTools.Worker.Pdf;

namespace WinTools.Worker.Tests;

/// <summary>
/// V2 PR 2.2 (ADR-0026): Protect and Remove protection through the bundled qpdf, the password reaching qpdf only on its
/// stdin. Protect sets an open password (AES-256) and no restrictions; Remove protection needs the password that opens
/// the PDF and never removes an author's restrictions. Written before the code.
/// </summary>
public sealed class PdfPasswordWorkerTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();

    private const string Password = "سرّ 😀 with spaces \"quotes\" and --dashes";

    private static JobRequest Request(string capability, string input, string? password) =>
        new(Guid.NewGuid().ToString("N"), capability, [input], Parameters: password is null ? null : new Dictionary<string, string> { [PasswordPolicy.ParameterName] = password });

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private string Pdf(string name, int pages) => _dir.Write(name, PdfSamples.Pages(pages));

    /// <summary>qpdf's own verdict, the password on stdin: 0 another password is needed, 2 not encrypted, 3 this one opens it.</summary>
    private static int Opens(string path, string? password) =>
        Qpdf.RunWithArgumentsOnStdin([.. password is null ? Array.Empty<string>() : [$"--password={password}"], "--requires-password", path], TimeSpan.FromSeconds(30)).ExitCode;

    private static int Pages(string path, string? password)
    {
        var run = Qpdf.RunWithArgumentsOnStdin([.. password is null ? Array.Empty<string>() : [$"--password={password}"], "--show-npages", path], TimeSpan.FromSeconds(30));
        return int.Parse(run.Output.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Protect_then_remove_with_the_same_password_gives_back_every_page()
    {
        var source = Pdf("report.pdf", 3);
        var before = Hash(source);
        var protect = Request(PdfCapabilities.ProtectId, source, Password);
        var p = PdfJobs.Run(protect);
        Assert.True(p.Success, p.Message);
        Assert.Equal("report (protected).pdf", Path.GetFileName(p.OutputPath));
        Assert.Equal(0, Opens(p.OutputPath!, null));      // it needs a password
        Assert.Equal(0, Opens(p.OutputPath!, "wrong"));   // and not just any
        Assert.Equal(3, Opens(p.OutputPath!, Password));  // this one opens it
        Assert.Equal(3, Pages(p.OutputPath!, Password));
        Assert.True(PdfJobModule.Instance.IsBelievable(protect, p));
        Assert.Equal(before, Hash(source)); // the original is only read

        var remove = Request(PdfCapabilities.UnprotectId, p.OutputPath!, Password);
        var u = PdfJobs.Run(remove);
        Assert.True(u.Success, u.Message);
        Assert.Equal("report (protected) (unprotected).pdf", Path.GetFileName(u.OutputPath));
        Assert.Equal(2, Opens(u.OutputPath!, null)); // not encrypted at all
        Assert.Equal(3, Pages(u.OutputPath!, null));
        Assert.True(PdfJobModule.Instance.IsBelievable(remove, u));
    }

    [Fact]
    public void A_protected_pdf_has_no_restrictions_and_aes_256()
    {
        var p = PdfJobs.Run(Request(PdfCapabilities.ProtectId, Pdf("report.pdf", 1), "pass"));
        Assert.True(p.Success, p.Message);
        var json = Qpdf.RunWithArgumentsOnStdin(["--password=pass", "--json=2", "--json-key=encrypt", p.OutputPath!], TimeSpan.FromSeconds(30)).Output;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var encrypt = doc.RootElement.GetProperty("encrypt");
        Assert.Equal(256, encrypt.GetProperty("parameters").GetProperty("bits").GetInt32());
        Assert.Equal("AESv3", encrypt.GetProperty("parameters").GetProperty("method").GetString());
        Assert.All(encrypt.GetProperty("capabilities").EnumerateObject(), c => Assert.True(c.Value.GetBoolean(), c.Name)); // no restriction
    }

    [Fact]
    public void A_wrong_password_is_said_as_such_and_nothing_is_written()
    {
        var locked = _dir.Path + @"\locked.pdf";
        Assert.True(Qpdf.Run([Pdf("plain.pdf", 2), "--encrypt", "--user-password=right", "--owner-password=o", "--bits=256", "--", locked], TimeSpan.FromSeconds(30)).Usable);
        var r = PdfJobs.Run(Request(PdfCapabilities.UnprotectId, locked, "wrong one"));
        Assert.Equal(ErrorCode.PasswordIncorrect, r.Error);
        Assert.DoesNotContain("wrong one", r.Message ?? "", StringComparison.Ordinal);
        Assert.Equal(["locked.pdf", "plain.pdf"], _dir.Names());
    }

    [Fact]
    public void An_already_protected_pdf_is_not_protected_again()
    {
        var locked = _dir.Path + @"\locked.pdf";
        Assert.True(Qpdf.Run([Pdf("plain.pdf", 1), "--encrypt", "--user-password=u", "--owner-password=o", "--bits=256", "--", locked], TimeSpan.FromSeconds(30)).Usable);
        Assert.Equal(ErrorCode.NothingToDo, PdfJobs.Run(Request(PdfCapabilities.ProtectId, locked, "another")).Error);
        Assert.Equal(["locked.pdf", "plain.pdf"], _dir.Names());
    }

    [Fact]
    public void An_authors_restrictions_are_never_removed()
    {
        // Opens without a password, printing forbidden by its author: the owner decided WinTools leaves that alone (PR 2.2).
        var restricted = _dir.Path + @"\restricted.pdf";
        Assert.True(Qpdf.Run([Pdf("plain.pdf", 1), "--encrypt", "--owner-password=owner", "--bits=256", "--print=none", "--", restricted], TimeSpan.FromSeconds(30)).Usable);
        foreach (var password in new[] { "owner", "anything" })
        {
            var r = PdfJobs.Run(Request(PdfCapabilities.UnprotectId, restricted, password));
            Assert.Equal(ErrorCode.UnsupportedContent, r.Error);
            Assert.Contains("restrictions", r.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(["plain.pdf", "restricted.pdf"], _dir.Names());
    }

    [Theory]
    [InlineData("user")]  // the open password: qpdf would drop the restrictions with it
    [InlineData("owner")] // the owner password: still refused (the owner's decision: never, whatever password)
    public void Restrictions_are_never_removed_whatever_password_is_given(string password)
    {
        var both = _dir.Path + @"\both.pdf";
        Assert.True(Qpdf.Run([Pdf("plain.pdf", 2), "--encrypt", "--user-password=user", "--owner-password=owner", "--bits=256", "--print=none", "--", both], TimeSpan.FromSeconds(30)).Usable);
        var r = PdfJobs.Run(Request(PdfCapabilities.UnprotectId, both, password));
        Assert.Equal(ErrorCode.UnsupportedContent, r.Error);
        Assert.Contains("restrictions", r.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["both.pdf", "plain.pdf"], _dir.Names());
    }

    [Fact]
    public void A_pdf_without_protection_has_nothing_to_remove()
    {
        Assert.Equal(ErrorCode.NothingToDo, PdfJobs.Run(Request(PdfCapabilities.UnprotectId, Pdf("plain.pdf", 1), "any")).Error);
        Assert.Equal(["plain.pdf"], _dir.Names());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("two\nlines")]
    [InlineData("tab\there")]
    public void A_password_outside_the_policy_is_refused_before_qpdf_runs(string? password)
    {
        var source = Pdf("plain.pdf", 1);
        var r = PdfJobs.Run(Request(PdfCapabilities.ProtectId, source, password));
        Assert.Equal(ErrorCode.InvalidRequest, r.Error);
        Assert.Equal(["plain.pdf"], _dir.Names());
        if (!string.IsNullOrEmpty(password)) Assert.DoesNotContain(password, r.Message ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void No_other_command_takes_a_password()
    {
        var r = PdfJobs.Run(Request(PdfCapabilities.InfoId, Pdf("plain.pdf", 1), "secret"));
        Assert.Equal(ErrorCode.InvalidRequest, r.Error);
        Assert.DoesNotContain("secret", r.Message ?? "", StringComparison.Ordinal);
        var extra = new JobRequest(Guid.NewGuid().ToString("N"), PdfCapabilities.ProtectId, [Pdf("other.pdf", 1)],
            Parameters: new Dictionary<string, string> { [PasswordPolicy.ParameterName] = "a", ["more"] = "b" });
        Assert.Equal(ErrorCode.InvalidRequest, PdfJobs.Run(extra).Error);
    }

    [Fact]
    public void The_password_never_reaches_a_result_or_qpdfs_command_line()
    {
        // The arguments-on-stdin runner gives qpdf exactly one command-line argument: "@-".
        Assert.Equal(["@-"], Qpdf.CommandLineFor(["--password=x", "--requires-password", "a.pdf"]));
        var p = PdfJobs.Run(Request(PdfCapabilities.ProtectId, Pdf("report.pdf", 1), Password));
        Assert.True(p.Success, p.Message);
        Assert.All(p.Facts, f => Assert.DoesNotContain(Password, f.Value, StringComparison.Ordinal));
        Assert.Null(p.Message);
    }

    [Fact]
    public void Arguments_on_stdin_refuse_a_line_break_that_would_start_another_argument()
    {
        Assert.Throws<ArgumentException>(() => Qpdf.RunWithArgumentsOnStdin(["--password=a\nb", "--requires-password", "x.pdf"], TimeSpan.FromSeconds(5)));
        Assert.Throws<ArgumentException>(() => Qpdf.RunWithArgumentsOnStdin(["--password=a\0b"], TimeSpan.FromSeconds(5)));
    }
}
