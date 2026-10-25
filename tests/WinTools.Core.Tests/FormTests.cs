using WinTools.Core.Capabilities;
using WinTools.Core.Interaction;
using WinTools.Core.Jobs;
using WinTools.Core.Localization;

namespace WinTools.Core.Tests;

/// <summary>
/// The forms commands ask for (Trim, Split, Shift…): what is typed is read one way by the dialog, the host and the worker;
/// a time is "83", "1:23.5" or "1:02:03", never guessed; the answer has one spelling, so a second one is refused.
/// </summary>
public sealed class FormTests
{
    [Theory]
    [InlineData("83", 83)]
    [InlineData("83.5", 83.5)]
    [InlineData("1:23", 83)]
    [InlineData("1:23.25", 83.25)]
    [InlineData("1:02:03", 3723)]
    [InlineData("0:00", 0)]
    public void Times_read_as_seconds(string text, double seconds) => Assert.Equal(seconds, Forms.ReadTime(text));

    [Theory]
    [InlineData("1:75")]
    [InlineData("1:60:00")]
    [InlineData("-5")]
    [InlineData("1::2")]
    [InlineData("1:2:3:4")]
    [InlineData("1,5")]
    [InlineData("abc")]
    [InlineData("")]
    public void A_mistyped_time_is_refused_not_guessed(string text) => Assert.Null(Forms.ReadTime(text));

    [Fact]
    public void Trim_needs_a_start_and_an_end_after_it_or_none()
    {
        Assert.Equal("start=10;end=20.5", Forms.Check(MediaCapabilities.TrimVideoId, ["0:10", "0:20.5"]).Answer);
        Assert.Equal("start=0;end=", Forms.Check(MediaCapabilities.TrimAudioId, ["0:00", ""]).Answer);
        Assert.Equal(("form.endBeforeStart", 1), Problem(MediaCapabilities.TrimVideoId, "0:20", "0:10"));
        Assert.Equal(("form.badTime", 0), Problem(MediaCapabilities.TrimVideoId, "1:75", ""));
        Assert.Equal(("form.required", 0), Problem(MediaCapabilities.TrimVideoId, " ", ""));
        Assert.Equal(("form.range", 0), Problem(MediaCapabilities.TrimVideoId, "13:00:00", ""));
    }

    [Fact]
    public void Split_takes_2_to_20_whole_parts_and_Shift_any_nonzero_seconds()
    {
        Assert.Equal("parts=3", Forms.Check(MediaCapabilities.SplitVideoId, ["3"]).Answer);
        Assert.Equal(("form.range", 0), Problem(MediaCapabilities.SplitVideoId, "1"));
        Assert.Equal(("form.badNumber", 0), Problem(MediaCapabilities.SplitVideoId, "2.5"));
        Assert.Equal("seconds=-2.25", Forms.Check(SubtitleCapabilities.ShiftCustomId, ["-2.25"]).Answer);
        Assert.Equal("seconds=1.5", Forms.Check(SubtitleCapabilities.ShiftCustomId, ["+1.5"]).Answer);
        Assert.Equal(("form.zero", 0), Problem(SubtitleCapabilities.ShiftCustomId, "0"));
        Assert.Equal(("form.range", 0), Problem(SubtitleCapabilities.ShiftCustomId, "4000"));
    }

    [Fact]
    public void An_answer_is_read_back_only_in_its_one_spelling_with_exactly_its_keys()
    {
        Assert.Equal(10, Forms.Read(MediaCapabilities.TrimVideoId, "start=10;end=20")!.Get(Forms.TrimStart));
        Assert.Null(Forms.Read(MediaCapabilities.TrimVideoId, "start=10;end=20")!.Get("other"));
        Assert.Null(Forms.Read(MediaCapabilities.TrimVideoId, "start=0:10;end=20")); // the canonical answer is in seconds
        Assert.Null(Forms.Read(MediaCapabilities.TrimVideoId, "end=20;start=10"));
        Assert.Null(Forms.Read(MediaCapabilities.TrimVideoId, "start=10"));
        Assert.Null(Forms.Read(MediaCapabilities.TrimVideoId, "start=10;end=20;x=1"));
        Assert.Null(Forms.Read(MediaCapabilities.TrimVideoId, "start=30;end=20"));
        Assert.Null(Forms.Read(MediaCapabilities.MonoId, "start=0;end="));
        Assert.Null(Forms.Read(MediaCapabilities.TrimVideoId, null));
        Assert.Null(Forms.Read(MediaCapabilities.TrimVideoId, new string('1', 300)));
    }

    [Fact]
    public void The_custom_shift_names_its_output_after_the_amount()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(-2250), SubtitleCapabilities.ShiftOf(SubtitleCapabilities.ShiftCustomId, "seconds=-2.25"));
        Assert.Null(SubtitleCapabilities.ShiftOf(SubtitleCapabilities.ShiftCustomId, null));
        Assert.Equal(@"C:\p\film (2.25 s earlier).srt",
            SubtitleCapabilities.Candidates(SubtitleCapabilities.ShiftCustomId, @"C:\p\film.srt", null, "seconds=-2.25").First());
    }

    [Fact]
    public void Every_form_field_has_its_label_and_hint_in_both_languages_and_every_form_command_its_title()
    {
        var resolver = CapabilityResolver.For(null);
        var withForms = resolver.Catalog.Where(c => c.Dialog == DialogKinds.Form).ToArray();
        Assert.Equal(5, withForms.Length);
        foreach (var c in withForms)
        {
            Assert.EndsWith("…", c.Label, StringComparison.Ordinal);
            Assert.NotNull(Forms.For(c.Id));
            Assert.NotNull(Forms.Check(c.Id, [.. Forms.For(c.Id)!.Select(f => f.Default)]).Answer); // the form opens ready to go
            foreach (var language in new[] { UiText.English, UiText.Arabic })
            {
                Assert.NotNull(UiText.Get(language, "dialog.form." + c.Id));
                foreach (var f in Forms.For(c.Id)!)
                {
                    Assert.NotNull(UiText.Get(language, f.LabelKey));
                    Assert.NotNull(UiText.Get(language, f.LabelKey + ".tip"));
                }
            }
        }
        foreach (var key in new[] { "form.required", "form.badTime", "form.badNumber", "form.range", "form.endBeforeStart", "form.zero", "form.invalid", "form.go" })
            Assert.NotNull(UiText.Get(UiText.Arabic, key));
    }

    [Fact]
    public void Video_offers_trim_split_and_crop_and_audio_trim_and_split()
    {
        var resolver = CapabilityResolver.For(null);
        var video = RadialMenu.Build(resolver, [@"C:\p\clip.mp4"]);
        Assert.Equal(["Convert", "Tools", "Compress", "Edit", "Crop", "Actions"], video.Select(g => g.Label)); // six groups at most: every label fits
        Assert.Equal(["Trim…", "Split…", "2× speed", "½ speed", "Remove audio"], video.Single(g => g.Label == "Edit").Commands.Select(c => c.Label));
        Assert.Equal(["Square 1:1", "Wide 16:9", "Tall 9:16"], video.Single(g => g.Label == "Crop").Commands.Select(c => c.Label));
        var audio = RadialMenu.Build(resolver, [@"C:\p\song.mp3"]);
        Assert.Equal(["Normalize", "Trim…", "Split…", "To mono"], audio.Single(g => g.Label == "Edit").Commands.Select(c => c.Label).Take(4));
        Assert.Contains("Shift…", RadialMenu.Build(resolver, [@"C:\p\film.srt"]).Single(g => g.Label == "Timing").Commands.Select(c => c.Label));
    }

    private static (string?, int) Problem(string id, params string[] texts)
    {
        var c = Forms.Check(id, texts);
        Assert.Null(c.Answer);
        return (c.ProblemKey, c.Field);
    }
}
