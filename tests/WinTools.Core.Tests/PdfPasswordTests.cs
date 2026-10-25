using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Interaction;
using WinTools.Core.Jobs;
using WinTools.Core.Output;

namespace WinTools.Core.Tests;

/// <summary>
/// V2 PR 2.2 (ADR-0026): PDF Protect and Remove protection, the password policy that the dialog, the host and the worker
/// share, and the secret prompt a command declares. Written before the code.
/// </summary>
public sealed class PdfPasswordTests
{
    private static readonly CapabilityResolver Resolver = CapabilityResolver.For(customResizeId: null);

    [Theory]
    [InlineData("correct horse")]
    [InlineData("سرّ 😀 with spaces \"quotes\" and --dashes")]
    [InlineData(" leading and trailing spaces are part of it ")]
    public void A_password_of_any_text_is_accepted(string password)
    {
        var check = PasswordPolicy.Check(password);
        Assert.True(check.Ok, check.Problem);
        Assert.Equal(password, check.Password);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("two\nlines")]
    [InlineData("two\r\nlines")]
    [InlineData("carriage\rreturn")]
    [InlineData("nul\0inside")]
    [InlineData("bidi\u200Fmark")]
    [InlineData("zero\u200Bwidth")]
    [InlineData("tab\tinside")]
    public void Empty_line_breaks_and_invisible_characters_are_refused(string? password)
    {
        // qpdf reads its arguments one per line through @-, so a line break would end the password and start an argument.
        var check = PasswordPolicy.Check(password);
        Assert.False(check.Ok);
        Assert.Null(check.Password);
        Assert.False(string.IsNullOrWhiteSpace(check.Problem));
    }

    [Fact]
    public void The_password_is_normalized_first_and_measured_after()
    {
        // The product runs with invariant globalization, so the caller passes the normalizer (Windows' NFKC, tested in
        // WinTools.Worker.Tests): qpdf does not normalize, and the standard tells the writer to (qpdf manual, Unicode
        // Passwords). A normalizer that fails refuses the password.
        static string? Fake(string s) => s.Replace("\uFB01", "fi", StringComparison.Ordinal);
        Assert.Equal("fine", PasswordPolicy.Check("\uFB01ne", Fake).Password);
        Assert.False(PasswordPolicy.Check(new string('a', 126) + "\uFB01", Fake).Ok); // 129 bytes before, 128 after
        Assert.False(PasswordPolicy.Check("ok", _ => null).Ok);
        Assert.False(PasswordPolicy.Check("ok", _ => "two\nlines").Ok); // what the normalizer returns is checked too
    }

    [Fact]
    public void At_most_127_bytes_of_utf8_since_a_longer_password_is_silently_cut()
    {
        Assert.True(PasswordPolicy.Check(new string('a', 127)).Ok);
        Assert.False(PasswordPolicy.Check(new string('a', 128)).Ok);
        Assert.True(PasswordPolicy.Check(new string('س', 63)).Ok); // 126 bytes
        Assert.False(PasswordPolicy.Check(new string('س', 64)).Ok); // 128 bytes
        Assert.False(PasswordPolicy.Check(string.Concat(Enumerable.Repeat("😀", 32))).Ok); // 128 bytes
        Assert.Equal(127, PasswordPolicy.MaxUtf8Bytes);
    }

    [Fact]
    public void A_pdf_gets_a_protect_category_with_both_commands_each_asking_for_the_password()
    {
        var menu = RadialMenu.Build(Resolver, [@"C:\p\a.pdf"]);
        Assert.Equal(["Organize", "Optimize", "Rotate", "Protect", "Tools"], menu.Select(g => g.Label));
        var protect = menu.Single(g => g.Label == "Protect").Commands;
        Assert.Equal([PdfCapabilities.ProtectId, PdfCapabilities.UnprotectId], protect.Select(c => c.Id));
        Assert.Equal(["Protect…", "Remove protection…"], protect.Select(c => c.Label));
        Assert.True(protect[0].Prompt is { Confirm: true });  // a new password is typed twice
        Assert.True(protect[1].Prompt is { Confirm: false }); // the existing one once
        Assert.All(protect, c => Assert.Equal(CapabilityShape.PerFile, c.Shape));
    }

    [Fact]
    public void Only_pdfs_get_them_and_no_other_command_asks_for_a_secret()
    {
        var ids = Resolver.Resolve([@"C:\p\a.png"]).Select(c => c.Id).ToArray();
        Assert.DoesNotContain(PdfCapabilities.ProtectId, ids);
        Assert.DoesNotContain(Resolver.Resolve([@"C:\p\a.pdf", @"C:\p\b.png"]), c => c.Module == PdfCapabilities.Module);
        Assert.Equal([PdfCapabilities.ProtectId, PdfCapabilities.UnprotectId],
            Resolver.Catalog.Where(c => c.Prompt is not null).Select(c => c.Id));
    }

    [Fact]
    public void Outputs_are_named_protected_and_unprotected()
    {
        Assert.Equal("protected", PdfCapabilities.NameSuffix(PdfCapabilities.ProtectId));
        Assert.Equal("unprotected", PdfCapabilities.NameSuffix(PdfCapabilities.UnprotectId));
        Assert.Equal(@"C:\p\report (protected).pdf", OutputNaming.CandidatesWithExtension(@"C:\p\report.pdf", ".pdf", "protected").First());
    }

    [Fact]
    public void A_wrong_password_is_said_as_such_never_as_damage()
    {
        var protect = Resolver.Catalog.Single(c => c.Id == PdfCapabilities.UnprotectId);
        var m = FeedbackText.For(protect.Words, [new("report.pdf", JobResult.Fail("id", ErrorCode.PasswordIncorrect, "detail"))]);
        Assert.Contains("password", m.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("damaged", m.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_secret_request_carries_no_path_and_says_which_dialog_it_is()
    {
        var json = WorkerProtocol.Serialize(new SecretRequest("Protect with a password", Confirm: true, 10, 20));
        Assert.Equal(DialogKinds.Secret, WorkerProtocol.Deserialize<DialogEnvelope>(json)!.Kind);
        Assert.Equal(DialogKinds.Pick, WorkerProtocol.Deserialize<DialogEnvelope>(WorkerProtocol.Serialize(new PickRequest("More", [new("a", "A")], 0, 0)))!.Kind);
        Assert.DoesNotContain(@":\", json, StringComparison.Ordinal);
    }
}
