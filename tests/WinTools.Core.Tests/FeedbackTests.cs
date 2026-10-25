using System.Buffers.Binary;
using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Files;
using WinTools.Core.Interaction;
using WinTools.Core.Jobs;
using WinTools.Core.Settings;

namespace WinTools.Core.Tests;

public sealed class NavigationSoundsTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void A_tick_plays_once_per_change_of_sector_and_never_while_staying()
    {
        var s = new NavigationSounds(enabled: true);
        Assert.Null(s.OnView(-1, -1, Ms(0)));                 // the ring appears, pointer in the dead zone
        Assert.Equal(UiSound.Tick, s.OnView(-1, 2, Ms(100)));
        for (var t = 110; t < 600; t += 10) Assert.Null(s.OnView(-1, 2, Ms(t))); // DragOver keeps coming on the same sector
        Assert.Equal(UiSound.Tick, s.OnView(-1, 3, Ms(700)));
        Assert.Equal(UiSound.Tick, s.OnView(-1, 2, Ms(800)));  // away and back is a real change
        Assert.Equal((3, 3), (s.Ticks, s.SectorChanges));
    }

    [Fact]
    public void Leaving_to_the_dead_zone_and_coming_back_to_the_same_sector_is_silent()
    {
        var s = new NavigationSounds(true);
        s.OnView(-1, -1, Ms(0));
        Assert.Equal(UiSound.Tick, s.OnView(-1, 1, Ms(100)));
        Assert.Null(s.OnView(-1, -1, Ms(200)));
        Assert.Null(s.OnView(-1, 1, Ms(300)));
        Assert.Equal(1, s.Ticks);
    }

    [Fact]
    public void A_fast_sweep_cannot_machine_gun()
    {
        var s = new NavigationSounds(true);
        s.OnView(-1, -1, Ms(0));
        var played = 0;
        // Pointer noise across sector borders every 5 ms for half a second.
        for (var i = 0; i < 100; i++)
            if (s.OnView(-1, i % 5, Ms(100 + i * 5)) == UiSound.Tick) played++;
        Assert.True(played <= 500 / NavigationSounds.MinTickInterval.TotalMilliseconds + 1, $"{played} ticks");
        Assert.True(played >= 1);
        Assert.Equal(100, s.SectorChanges);
    }

    [Fact]
    public void Opening_a_category_and_going_back_play_the_level_sound_and_the_first_sector_of_the_new_ring_waits()
    {
        var s = new NavigationSounds(true);
        s.OnView(-1, -1, Ms(0));
        Assert.Equal(UiSound.Tick, s.OnView(-1, 0, Ms(100)));
        Assert.Equal(UiSound.Level, s.OnView(0, -1, Ms(200)));  // Convert opened, ring re-centred
        Assert.Null(s.OnView(0, 2, Ms(240)));                   // within the quiet time after the level sound
        Assert.Equal(UiSound.Tick, s.OnView(0, 3, Ms(400)));
        Assert.Equal(UiSound.Level, s.OnView(-1, -1, Ms(600))); // Back
        Assert.Equal((2, 2, 2), (s.Ticks, s.LevelSounds, s.LevelChanges));
    }

    [Fact]
    public void Sounds_off_plays_nothing_but_still_counts()
    {
        var s = new NavigationSounds(enabled: false);
        s.OnView(-1, -1, Ms(0));
        Assert.Null(s.OnView(-1, 1, Ms(100)));
        Assert.Null(s.OnView(2, -1, Ms(200)));
        Assert.Null(s.OnView(2, 1, Ms(400)));
        Assert.Equal((0, 0, 2, 1), (s.Ticks, s.LevelSounds, s.SectorChanges, s.LevelChanges));
    }

    [Fact]
    public void Reset_starts_a_new_session_silently()
    {
        var s = new NavigationSounds(true);
        s.OnView(-1, -1, Ms(0));
        s.OnView(-1, 1, Ms(100));
        s.Reset();
        Assert.Null(s.OnView(0, 1, Ms(1000))); // first view of the next session, whatever it shows
        Assert.Equal(0, s.Ticks);
    }
}

public sealed class SoundBankTests
{
    [Theory]
    [InlineData(UiSound.Tick)]
    [InlineData(UiSound.Level)]
    [InlineData(UiSound.Success)]
    [InlineData(UiSound.Failure)]
    [InlineData(UiSound.Nothing)]
    public void Every_sound_is_a_valid_quiet_click_free_wav(UiSound sound)
    {
        var samples = SoundBank.Samples(sound);
        Assert.InRange(samples.Length, SoundBank.SampleRate / 100, SoundBank.SampleRate / 2); // 10–500 ms
        var peak = samples.Max(Math.Abs);
        Assert.InRange(peak, 0.01, SoundBank.MaxPeak);
        Assert.True(Math.Abs(samples[0]) < 1e-3 && Math.Abs(samples[^1]) < 1e-3, "starts and ends at silence");

        var wav = SoundBank.Wav(sound);
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal("WAVE"u8.ToArray(), wav[8..12]);
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)));  // PCM
        Assert.Equal(SoundBank.SampleRate, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(samples.Length * 2, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal(44 + samples.Length * 2, wav.Length);
    }

    [Fact]
    public void Navigation_sounds_are_shorter_and_softer_than_results()
    {
        double Peak(UiSound s) => SoundBank.Samples(s).Max(Math.Abs);
        Assert.True(SoundBank.Samples(UiSound.Tick).Length < SoundBank.Samples(UiSound.Level).Length);
        Assert.True(SoundBank.Samples(UiSound.Level).Length < SoundBank.Samples(UiSound.Success).Length);
        Assert.True(Peak(UiSound.Tick) < Peak(UiSound.Success));
    }
}

public sealed class FeedbackTextTests
{
    private static readonly Operation ToWebP = OperationCatalog.Convert(ImageFormat.WebP);

    private static JobItem Ok(string name, string output, long a = 2_000_000, long b = 500_000) =>
        new(name, JobResult.Ok("id", new JobOutput($@"C:\x\{output}", "00")), a, b);

    private static JobItem Fail(string name, ErrorCode code) => new(name, JobResult.Fail("id", code, "worker detail C:\\secret\\path"));

    [Fact]
    public void A_single_success_names_the_new_file_and_what_was_done()
    {
        var m = FeedbackText.For(ToWebP, [Ok("photo.jpg", "photo.webp")]);
        Assert.Equal(FeedbackKind.Success, m.Kind);
        Assert.Equal("photo.webp created", m.Title);
        Assert.Equal("Converted to WebP · 1.91 MB → 488.3 KB (−75%)", m.Detail);
    }

    [Fact]
    public void A_batch_is_summarised_and_failures_are_counted_with_the_common_reason()
    {
        var items = Enumerable.Range(1, 48).Select(i => Ok($"p{i}.jpg", $"p{i}.webp")).Concat([Fail("a.jpg", ErrorCode.DecodeFailed), Fail("b.jpg", ErrorCode.DecodeFailed)]).ToArray();
        var m = FeedbackText.For(ToWebP, items);
        Assert.Equal(FeedbackKind.Partial, m.Kind);
        Assert.Equal("48 images of 50 completed", m.Title);
        Assert.Equal("2 failed: The image is damaged or incomplete", m.Detail);

        var all = FeedbackText.For(ToWebP, Enumerable.Range(1, 48).Select(i => Ok($"p{i}.jpg", $"p{i}.webp", 1000, 1000)).ToArray());
        Assert.Equal(("48 images completed", "Converted to WebP · 46.9 KB → 46.9 KB"), (all.Title, all.Detail));
    }

    [Fact]
    public void A_failure_gives_the_reason_in_plain_words_never_the_worker_text_or_a_path()
    {
        var m = FeedbackText.For(ToWebP, [Fail("photo.jpg", ErrorCode.ImageTooLarge)]);
        Assert.Equal((FeedbackKind.Failure, "Could not convert photo.jpg", "The image is larger than 50 megapixels."), (m.Kind, m.Title, m.Detail));
        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            var reason = FeedbackText.Reason(code);
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.DoesNotContain(code.ToString(), reason, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", reason, StringComparison.Ordinal);
        }
        var batch = FeedbackText.For(OperationCatalog.Get("rotate.left"), [Fail("a.png", ErrorCode.DiskFull), Fail("b.png", ErrorCode.DiskFull)]);
        Assert.Equal(("Could not rotate 2 images", "The disk is full."), (batch.Title, batch.Detail));
    }

    [Fact]
    public void Nothing_to_do_is_not_a_failure()
    {
        var resize = OperationCatalog.Get("resize.box-1920x1080");
        var m = FeedbackText.For(resize, [new("small.png", JobResult.Fail("id", ErrorCode.NothingToDo, "fits"))]);
        Assert.Equal((FeedbackKind.Nothing, "Nothing to change", "Already that size or smaller"), (m.Kind, m.Title, m.Detail));
        var mixed = FeedbackText.For(resize, [Ok("a.png", "a (1920x1080).png"), new("b.png", JobResult.Fail("id", ErrorCode.NothingToDo, "fits"))]);
        Assert.Equal((FeedbackKind.Success, "1 image of 2 completed", "1 unchanged"), (mixed.Kind, mixed.Title, mixed.Detail));
    }

    [Fact]
    public void A_mixed_compress_batch_says_what_was_saved_and_which_were_already_optimized()
    {
        // Evaluator, PR #12: a batch with both kinds said only "1 unchanged" and dropped the saving.
        var op = OperationCatalog.Get("compress.balanced");
        var m = FeedbackText.For(op,
        [
            new("a.jpg", JobResult.Ok("1", new JobOutput(@"C:\x\a (compressed).jpg", "0")), 2_000_000, 500_000),
            new("b.jpg", JobResult.Ok("2", new JobOutput(@"C:\x\b (compressed).jpg", "0")), 2_000_000, 1_000_000),
            new("c.jpg", JobResult.Fail("3", ErrorCode.NothingToDo, "")),
        ]);
        Assert.Equal((FeedbackKind.Success, "2 images of 3 completed"), (m.Kind, m.Title));
        Assert.Equal("3.81 MB → 1.43 MB, saved 62% · 1 already optimized", m.Detail);
    }

    [Fact]
    public void Every_operation_has_a_past_tense_phrase_and_a_verb()
    {
        foreach (var op in ImageCapabilities.Operations(new AppSettings().CustomResizeId()))
        {
            Assert.False(string.IsNullOrWhiteSpace(FeedbackText.Done(op)), op.Id);
            Assert.False(string.IsNullOrWhiteSpace(FeedbackText.Verb(op)), op.Id);
        }
        Assert.Equal("Resized to 50%", FeedbackText.Done(OperationCatalog.Get("resize.percent-50")));
        Assert.Equal("Rotated right", FeedbackText.Done(OperationCatalog.Get("rotate.right")));
        Assert.Equal("Compressed without loss", FeedbackText.Done(OperationCatalog.Get("compress.lossless")));
    }

    [Fact]
    public void Image_info_rows_use_plain_words_and_flag_a_gps_location()
    {
        var info = new ImageInfo("JPEG", 4032, 3024, 2_400_000, 72, 72, false, 6, true, true, false, true, false, false);
        var rows = InfoRows.For(info);
        Assert.Equal(["Format", "Dimensions", "File size", "Resolution", "Transparency", "Orientation", "Colour profile", "EXIF", "GPS location"], rows.Select(r => r.Label));
        Assert.Equal("4032 × 3024 px · 12.2 MP", rows[1].Value);
        Assert.Equal(("72 dpi", "Rotated 90° right", "Embedded"), (rows[3].Value, rows[5].Value, rows[6].Value));
        Assert.True(rows.Single(r => r.Label == "GPS location").Notice);
        Assert.False(rows.Single(r => r.Label == "EXIF").Notice);
        var plain = InfoRows.For(new ImageInfo("PNG", 64, 48, 900, 0, 0, true, 0, false, false, true, false, true, true, true));
        Assert.Equal(("PNG · 16-bit", "64 × 48 px", "Not set", "Not set"), (plain[0].Value, plain[1].Value, plain[3].Value, plain[5].Value));
        Assert.Equal("XMP, text or comments, other", plain.Single(r => r.Label == "Other metadata").Value);
    }

    [Fact]
    public void Sounds_are_on_by_default_and_round_trip()
    {
        Assert.True(new AppSettings().UiSounds);
        Assert.False(AppSettings.FromJson(new AppSettings { UiSounds = false }.ToJson())!.UiSounds);
        Assert.True(AppSettings.FromJson("{\"triggerKey\":\"F9\"}")!.UiSounds); // older files without the field
    }
}

public sealed class RingLabelsTests
{
    public static TheoryData<int> Dpis => [96, 120, 144, 192];

    /// <summary>Every ring the menu can show for any supported input, with the overlay's label band.</summary>
    [Theory]
    [MemberData(nameof(Dpis))]
    public void Label_boxes_never_overlap_and_stay_on_the_ring(int dpi)
    {
        var resolver = CapabilityResolver.For(new AppSettings().CustomResizeId());
        foreach (var name in new[] { "a.png", "a.jpg", "a.webp", "a.bmp", "a.tif" })
        {
            var menu = RadialMenu.Build(resolver.Resolve([$@"C:\images\{name}"]));
            var rings = new List<(int Count, double Start, double First)> { (menu.Count, 0, 0) };
            for (var g = 0; g < menu.Count; g++)
                rings.Add((menu[g].Commands.Count + 1, MenuNavigator.LevelStart(g, menu.Count, menu[g].Commands.Count), MenuNavigator.BackSweep(menu.Count, menu[g].Commands.Count)));
            foreach (var (count, start, first) in rings)
            {
                var b = RingLabelMetrics.For(count, (uint)dpi, withIcons: first == 0);
                var boxes = RingLabels.Layout(count, start, first, b.Radius, b.HalfHeight, b.MaxHalfWidth, b.Inner, b.Outer, b.Gap);
                for (var i = 0; i < count; i++)
                {
                    var (near, far) = boxes[i].Reach();
                    Assert.True(near >= b.Inner - 0.01 && far <= b.Outer + 0.01, $"{name} ring {count}: box {i} leaves the band ({near:0.0}..{far:0.0})");
                    Assert.True(boxes[i].HalfWidth >= b.MinUsefulHalfWidth, $"{name} ring {count}: box {i} too narrow ({boxes[i].HalfWidth:0.0})");
                    for (var j = i + 1; j < count; j++)
                        Assert.False(boxes[i].Intersects(boxes[j], 0), $"{name} ring {count}: boxes {i} and {j} overlap");
                }
            }
        }
    }
}

public sealed class RingAnimationTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void A_ring_appears_readable_and_settles_within_its_durations()
    {
        var a = new RingAnimation(reducedMotion: false);
        a.Start(5, Ms(0));
        a.Target(-1, false, Ms(0));
        Assert.InRange(a.Appear, 0.6, 0.95); // the first frame is already readable, never empty
        Assert.False(a.Settled);
        a.Advance(Ms(60));
        var half = a.Appear;
        a.Advance(Ms(RingAnimation.AppearDuration.TotalMilliseconds + 1));
        Assert.True(half < a.Appear && a.Appear == 1);
        Assert.True(a.Settled);
    }

    [Fact]
    public void Hover_and_arm_animate_towards_the_target_and_a_new_sector_starts_unarmed()
    {
        var a = new RingAnimation(false);
        a.Start(5, Ms(0));
        a.Advance(Ms(500));
        a.Target(2, armed: true, Ms(500));
        Assert.Equal((0.0, 0.0), (a.Hover(2), a.Arm));
        a.Advance(Ms(530));
        Assert.InRange(a.Hover(2), 0.01, 0.99);
        a.Advance(Ms(700));
        Assert.Equal((1.0, 1.0), (a.Hover(2), a.Arm));
        a.Target(3, armed: true, Ms(700));     // another command: its own arming starts from zero
        Assert.Equal(0.0, a.Arm);
        a.Advance(Ms(1000));
        Assert.Equal((0.0, 1.0, 1.0), (a.Hover(2), a.Hover(3), a.Arm));
        Assert.True(a.Settled);
    }

    [Fact]
    public void Reduced_motion_shows_every_change_at_once()
    {
        var a = new RingAnimation(reducedMotion: true);
        a.Start(5, Ms(0));
        Assert.Equal(1.0, a.Appear);
        a.Target(1, true, Ms(0));
        Assert.Equal((1.0, 1.0), (a.Hover(1), a.Arm));
        Assert.True(a.Settled);
    }

    [Theory]
    [InlineData("System", false, false)]
    [InlineData("System", true, true)]
    [InlineData("On", true, false)]
    [InlineData("Off", false, true)]
    [InlineData("Sparkly", true, true)] // unknown → follow Windows
    public void The_animation_setting_overrides_windows_only_when_set(string setting, bool windowsReduced, bool expected) =>
        Assert.Equal(expected, new AppSettings { Animations = setting }.ReducedMotion(windowsReduced));
}

public sealed class TriggerKeyChangeTests
{
    [Fact]
    public void A_new_trigger_key_takes_effect_at_once_and_the_old_one_stops_triggering()
    {
        var d = new TriggerDetector(TriggerDetector.VkSpace);
        d.OnMouseButton(MouseButton.Left, true, 0, 0);
        d.OnMouseMove(20, 0);
        Assert.Equal(InputSignal.Trigger, d.OnKey(TriggerDetector.VkSpace, true));
        d.OnKey(TriggerDetector.VkSpace, false);
        d.TriggerVirtualKey = 0x78; // F9, from the settings window
        Assert.Equal(InputSignal.None, d.OnKey(TriggerDetector.VkSpace, true));
        d.OnKey(TriggerDetector.VkSpace, false);
        Assert.Equal(InputSignal.Trigger, d.OnKey(0x78, true));
        Assert.True(d.IsTriggerKey(0x78) && !d.IsTriggerKey(TriggerDetector.VkSpace));
    }
}
