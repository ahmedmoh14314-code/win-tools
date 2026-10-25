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
        var check = PasswordPolicy.CheckNew(password);
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
        var check = PasswordPolicy.CheckNew(password);
        Assert.False(check.Ok);
        Assert.Null(check.Password);
        Assert.False(string.IsNullOrWhiteSpace(check.Problem));
    }

    [Fact]
    public void A_new_password_is_kept_as_typed_and_must_already_be_normalized()
    {
        // The product runs with invariant globalization, so the caller passes the normalizer (Windows' NFKC, tested in
        // WinTools.Worker.Tests). A new password is never rewritten: one NFKC would change is refused (evaluator, PR 2.2),
        // so a reader that normalizes and one that does not derive the same key.
        static string? Fake(string s) => s.Replace("\uFB01", "fi", StringComparison.Ordinal);
        Assert.Equal("fine", PasswordPolicy.CheckNew("fine", Fake).Password);
        Assert.False(PasswordPolicy.CheckNew("\uFB01ne", Fake).Ok);
        Assert.False(PasswordPolicy.CheckNew("ok", _ => null).Ok); // a normalizer that fails refuses it
    }

    [Fact]
    public void At_most_127_bytes_of_utf8_since_a_longer_password_is_silently_cut()
    {
        Assert.True(PasswordPolicy.CheckNew(new string('a', 127)).Ok);
        Assert.False(PasswordPolicy.CheckNew(new string('a', 128)).Ok);
        Assert.True(PasswordPolicy.CheckNew(new string('س', 63)).Ok); // 126 bytes
        Assert.False(PasswordPolicy.CheckNew(new string('س', 64)).Ok); // 128 bytes
        Assert.False(PasswordPolicy.CheckNew(string.Concat(Enumerable.Repeat("😀", 32))).Ok); // 128 bytes
        Assert.Equal(127, PasswordPolicy.MaxUtf8Bytes);
    }

    // Evaluator, PR 2.2: NFKC is not applied to a password silently, and SASLprep's prohibitions hold for a new one.

    [Fact]
    public void A_new_password_that_normalization_would_change_is_refused()
    {
        // "x²" becomes "x2" under NFKC: a reader that does not normalize would refuse what the user typed.
        static string? Fake(string s) => s.Replace("\u00B2", "2", StringComparison.Ordinal);
        var check = PasswordPolicy.CheckNew("x\u00B2", Fake);
        Assert.False(check.Ok);
        Assert.Contains("normaliz", check.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.True(PasswordPolicy.CheckNew("x2", Fake).Ok);
    }

    [Fact]
    public void A_refused_new_password_says_what_to_change()
    {
        // Second review, PR 2.2: the reason must fit the case. Marks in another order (shadda then fatha, as an Arabic
        // keyboard types them) are a canonical difference; "²" is a compatibility one; joined emoji have a joiner.
        static string? Kc(string s) => s.Replace("\u0651\u064E", "\u064E\u0651", StringComparison.Ordinal).Replace("\u00B2", "2", StringComparison.Ordinal);
        static string? C(string s) => s.Replace("\u0651\u064E", "\u064E\u0651", StringComparison.Ordinal);
        Assert.Contains("order", PasswordPolicy.CheckNew("\u0634\u0651\u064E", Kc, C).Problem, StringComparison.Ordinal);
        Assert.Contains("² becomes 2", PasswordPolicy.CheckNew("x\u00B2", Kc, C).Problem, StringComparison.Ordinal);
        Assert.Contains("joined", PasswordPolicy.CheckNew("\U0001F468\u200D\U0001F469", Kc, C).Problem, StringComparison.Ordinal);
        Assert.Contains("emoji", PasswordPolicy.CheckNew("\u2764\uFE0F", Kc, C).Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("heart\u2764\uFE0F")]      // a variation selector (SASLprep maps it to nothing)
    [InlineData("tag\U000E0041")]           // a tag character outside the BMP (format)
    [InlineData("music\U0001D173")]         // a musical format character outside the BMP
    [InlineData("line\u2028separator")]     // a line separator
    [InlineData("private\uE000use")]        // private use
    [InlineData("unassigned\u0378")]        // not assigned
    [InlineData("grapheme\u034Fjoiner")]    // combining grapheme joiner (mapped to nothing)
    public void A_new_password_with_what_saslprep_forbids_or_drops_is_refused(string password) =>
        Assert.False(PasswordPolicy.CheckNew(password).Ok);

    [Fact]
    public void An_existing_password_is_taken_as_typed_with_its_normalized_form_beside_it()
    {
        static string? Fake(string s) => s.Replace("\u00B2", "2", StringComparison.Ordinal);
        var typed = PasswordPolicy.CheckExisting("x\u00B2", Fake);
        Assert.True(typed.Ok, typed.Problem);
        Assert.Equal("x\u00B2", typed.Password);
        Assert.Equal("x2", typed.Normalized);
        Assert.Null(PasswordPolicy.CheckExisting("plain", Fake).Normalized); // no second try when nothing changes
        // Set elsewhere: no 127-byte limit (readers cut it themselves), and SASLprep's choices are not ours to enforce.
        Assert.True(PasswordPolicy.CheckExisting(new string('a', 300)).Ok);
        Assert.True(PasswordPolicy.CheckExisting("heart\u2764\uFE0F").Ok);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("two\nlines")]
    [InlineData("nul\0inside")]
    [InlineData("eof\u001Achar")] // cuts qpdf's stdin short in text mode
    public void An_existing_password_still_cannot_break_the_channel(string? password) =>
        Assert.False(PasswordPolicy.CheckExisting(password).Ok);

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
