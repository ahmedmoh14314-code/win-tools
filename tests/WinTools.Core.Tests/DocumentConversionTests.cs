using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Files;
using WinTools.Core.Interaction;
using WinTools.Core.Localization;

namespace WinTools.Core.Tests;

/// <summary>
/// ADR-0033 in Core: which conversions the wheel offers for which files, To PDF only when LibreOffice was found, where
/// LibreOffice is looked for (and what is not accepted as it), and every new word in English and Arabic.
/// </summary>
[Collection("Language")]
public sealed class DocumentConversionTests
{
    private static readonly IReadOnlySet<string> WithLibreOffice = ExternalPrograms.Set(@"C:\Program Files\LibreOffice\program\soffice.exe");

    private static string[] Ids(CapabilityResolver r, params string[] paths) => [.. r.Resolve(paths).Select(c => c.Id)];

    [Theory]
    [InlineData(@"C:\p\data.csv", DocumentCapabilities.ToXlsxId)]
    [InlineData(@"C:\p\data.TSV", DocumentCapabilities.ToXlsxId)]
    [InlineData(@"C:\p\book.xlsx", DocumentCapabilities.ToCsvId)]
    [InlineData(@"C:\p\notes.md", DocumentCapabilities.ToHtmlId)]
    [InlineData(@"C:\p\notes.markdown", DocumentCapabilities.ToHtmlId)]
    public void Each_table_and_markdown_file_gets_its_conversion(string path, string id)
    {
        Assert.Contains(id, Ids(CapabilityResolver.For(null), path));
        Assert.Contains(FilesCapabilities.CopyPathId, Ids(CapabilityResolver.For(null), path)); // still a file: copy, rename, hash
    }

    [Theory]
    [InlineData(@"C:\p\a.docx")]
    [InlineData(@"C:\p\a.doc")]
    [InlineData(@"C:\p\a.rtf")]
    [InlineData(@"C:\p\a.odt")]
    [InlineData(@"C:\p\a.xlsx")]
    [InlineData(@"C:\p\a.xls")]
    [InlineData(@"C:\p\a.ods")]
    [InlineData(@"C:\p\a.pptx")]
    [InlineData(@"C:\p\a.ppt")]
    [InlineData(@"C:\p\a.odp")]
    [InlineData(@"C:\p\a.txt")]
    [InlineData(@"C:\p\a.csv")]
    public void To_pdf_is_on_the_wheel_only_when_LibreOffice_was_found(string path)
    {
        Assert.DoesNotContain(OfficeCapabilities.ToPdfId, Ids(CapabilityResolver.For(null), path));
        Assert.DoesNotContain(OfficeCapabilities.ToPdfId, Ids(CapabilityResolver.Default, path));
        Assert.Contains(OfficeCapabilities.ToPdfId, Ids(CapabilityResolver.For(null, WithLibreOffice), path));
    }

    [Fact]
    public void To_pdf_is_never_offered_for_markdown_pdf_or_images_and_works_on_mixed_documents()
    {
        var r = CapabilityResolver.For(null, WithLibreOffice);
        Assert.DoesNotContain(OfficeCapabilities.ToPdfId, Ids(r, @"C:\p\a.md"));
        Assert.DoesNotContain(OfficeCapabilities.ToPdfId, Ids(r, @"C:\p\a.pdf"));
        Assert.DoesNotContain(OfficeCapabilities.ToPdfId, Ids(r, @"C:\p\a.png"));
        Assert.Contains(OfficeCapabilities.ToPdfId, Ids(r, @"C:\p\a.docx", @"C:\p\b.odt", @"C:\p\c.csv"));
        Assert.DoesNotContain(OfficeCapabilities.ToPdfId, Ids(r, @"C:\p\a.docx", @"C:\p\b.png"));
    }

    [Fact]
    public void Every_menu_with_the_new_commands_still_fits_its_rings()
    {
        var r = CapabilityResolver.For(null, WithLibreOffice);
        foreach (var path in new[] { @"C:\p\a.csv", @"C:\p\a.tsv", @"C:\p\a.xlsx", @"C:\p\a.md", @"C:\p\a.docx", @"C:\p\a.txt", @"C:\p\a.doc" })
            Assert.All(RadialMenu.Build(r, [path]), g => Assert.True(g.Commands.Count <= RadialMenu.RingCapacity - 1));
        Assert.Equal(["documents.text", "documents.to-csv", "office.to-pdf"], RadialMenu.Build(r, [@"C:\p\a.xlsx"]).Single(g => g.Category.Id == "convert").Commands.Select(c => c.Id));
    }

    [Fact]
    public void Every_new_command_and_word_has_english_and_arabic()
    {
        var r = CapabilityResolver.For(null, WithLibreOffice);
        foreach (var c in r.Catalog.Where(c => c.Module is DocumentCapabilities.Module or OfficeCapabilities.Module))
        {
            Assert.Equal(c.Label, UiText.Get(UiText.English, "cmd." + c.Id));
            Assert.False(string.IsNullOrWhiteSpace(UiText.Get(UiText.Arabic, "cmd." + c.Id)), c.Id);
        }
        foreach (var key in new[] { "module.office", "features.libreOfficeMissing", "noun.office", "nouns.office" })
            Assert.False(string.IsNullOrWhiteSpace(UiText.Get(UiText.Arabic, key)), key);
        try
        {
            UiText.Use("ar");
            var toPdf = r.Catalog.Single(c => c.Id == OfficeCapabilities.ToPdfId);
            var words = LocalWords.For(toPdf);
            Assert.Equal("لم يُعثر على LibreOffice. ثبّته (مجاني) ثم حاول مرة أخرى.", FeedbackText.Reason(Jobs.ErrorCode.UnsupportedOperation, words));
            Assert.Equal("جداول", LocalWords.For(r.Catalog.Single(c => c.Id == DocumentCapabilities.ToXlsxId)).Nouns);
            Assert.Equal("أُضيفت ' قبل الخلايا التي تبدأ بـ = + - @ ليعرضها Excel نصًا ولا ينفّذها.",
                UiText.Say("Cells starting with = + - @ got a leading ' so Excel shows them as text and does not run them."));
            UiText.Use("en");
            Assert.Equal("LibreOffice was not found. Install it (it is free), then try again.", FeedbackText.Reason(Jobs.ErrorCode.UnsupportedOperation, LocalWords.For(toPdf)));
        }
        finally { UiText.Use("en"); }
    }

    // ---- Where LibreOffice is looked for ----

    private sealed class FakeMachine
    {
        public Dictionary<(RegistryRoot, bool, string), string> Keys { get; } = [];
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Find(params string?[] programFolders) =>
            LibreOfficeLocator.Find((root, wow, key) => Keys.GetValueOrDefault((root, wow, key)), Files.Contains, programFolders);

        public void Install(string folder)
        {
            Files.Add(Path.Combine(folder, "soffice.exe"));
            Files.Add(Path.Combine(folder, "soffice.bin"));
        }
    }

    [Fact]
    public void LibreOffice_is_found_through_app_paths_first_quoted_or_not()
    {
        var m = new FakeMachine();
        m.Install(@"D:\Apps\LO\program");
        m.Install(@"C:\Program Files\LibreOffice\program");
        m.Keys[(RegistryRoot.LocalMachine, false, LibreOfficeLocator.AppPathKey)] = "\"D:\\Apps\\LO\\program\\soffice.exe\"";
        Assert.Equal(@"D:\Apps\LO\program\soffice.exe", m.Find(@"C:\Program Files"));
        m.Keys[(RegistryRoot.CurrentUser, false, LibreOfficeLocator.AppPathKey)] = @"C:\Program Files\LibreOffice\program\soffice.exe";
        Assert.Equal(@"C:\Program Files\LibreOffice\program\soffice.exe", m.Find()); // the user's own entry first
    }

    [Fact]
    public void LibreOffice_is_found_through_its_install_path_or_the_default_folder()
    {
        var m = new FakeMachine();
        m.Install(@"E:\LibreOffice 25\program");
        m.Keys[(RegistryRoot.LocalMachine, true, LibreOfficeLocator.UnoInstallPathKey)] = @"E:\LibreOffice 25\program";
        Assert.Equal(@"E:\LibreOffice 25\program\soffice.exe", m.Find());
        var plain = new FakeMachine();
        plain.Install(@"C:\Program Files (x86)\LibreOffice\program");
        Assert.Equal(@"C:\Program Files (x86)\LibreOffice\program\soffice.exe", plain.Find(@"C:\Program Files", @"C:\Program Files (x86)"));
        Assert.Null(new FakeMachine().Find(@"C:\Program Files", null, ""));
    }

    [Theory]
    [InlineData(@"\\server\share\LibreOffice\program\soffice.exe")] // a network share
    [InlineData(@"C:\Tools\evil.exe")] // not soffice.exe
    [InlineData(@"C:\Tools\..\Windows\soffice.exe")] // a path with ".."
    [InlineData(@"soffice.exe")] // not fully qualified
    [InlineData(@"\\?\C:\LibreOffice\program\soffice.exe")] // a device path
    public void A_registry_value_pointing_anywhere_else_is_ignored(string value)
    {
        var m = new FakeMachine();
        m.Files.Add(value);
        m.Files.Add(Path.Combine(Path.GetDirectoryName(value) ?? "", "soffice.bin"));
        m.Keys[(RegistryRoot.LocalMachine, false, LibreOfficeLocator.AppPathKey)] = value;
        Assert.Null(m.Find());
    }

    [Fact]
    public void A_soffice_exe_without_its_program_beside_it_is_not_LibreOffice()
    {
        var m = new FakeMachine();
        m.Files.Add(@"C:\Program Files\LibreOffice\program\soffice.exe");
        Assert.Null(m.Find(@"C:\Program Files"));
    }

    [Fact]
    public void The_new_document_kinds_are_named_by_their_extensions()
    {
        Assert.Same(FileKinds.Csv, FileKinds.IdentifyByName(@"C:\p\a.CSV"));
        Assert.Same(FileKinds.Markdown, FileKinds.IdentifyByName(@"C:\p\a.markdown"));
        Assert.Same(FileKinds.Odp, FileKinds.IdentifyByName(@"C:\p\a.odp"));
        var extensions = FileKinds.Declared.SelectMany(k => k.Extensions).ToArray();
        Assert.Equal(extensions.Length, extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(FileKinds.Declared.Count, FileKinds.Declared.Select(k => k.Id).Distinct().Count());
    }
}
