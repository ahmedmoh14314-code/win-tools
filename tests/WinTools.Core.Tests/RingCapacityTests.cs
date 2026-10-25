using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Interaction;

namespace WinTools.Core.Tests;

/// <summary>
/// V2 PR 1.3, the "More…" policy (V2 plan §5): a ring holds at most <see cref="RadialMenu.RingCapacity"/> sectors. A
/// category ring is Back plus its commands, so beyond 7 commands the 7th sector becomes "More…", which opens the rest
/// in a list; beyond 8 categories the 8th becomes a "More…" category holding the rest. Nothing is ever dropped.
/// </summary>
public sealed class RingCapacityTests
{
    private static CapabilityDescriptor Cmd(string id, string category) =>
        new(id, id, category, "test", new InputRule(FileKinds.All.Select(k => k.Id).ToHashSet()), CapabilityShape.PerFile, CapabilityOutput.Facts, false);

    private static IReadOnlyList<MenuGroup> Menu(IEnumerable<CapabilityDescriptor> commands, IEnumerable<CapabilityCategory> categories) =>
        RadialMenu.Build(new CapabilityResolver([.. commands], [.. categories]), [@"C:\p\a.png"]);

    [Fact]
    public void Seven_commands_fill_a_ring_without_more()
    {
        var menu = Menu(Enumerable.Range(1, 7).Select(i => Cmd($"c{i}", "a")), [new("a", "A", "a", 1)]);
        Assert.Equal(7, menu.Single().Commands.Count);
        Assert.DoesNotContain(menu.Single().Commands, RadialMenu.IsMore);
    }

    [Fact]
    public void Beyond_seven_commands_the_sixth_sector_is_more_holding_the_rest_in_order()
    {
        // V2 PR 7.2: an overflowing ring shows five commands and More…, so its long labels fit (seven did not, at any DPI).
        var menu = Menu(Enumerable.Range(1, 10).Select(i => Cmd($"c{i}", "a")), [new("a", "A", "a", 1)]);
        var ring = menu.Single().Commands;
        Assert.Equal(RadialMenu.ShownBeforeMore + 1, ring.Count);
        Assert.Equal(["c1", "c2", "c3", "c4", "c5"], ring.Take(5).Select(c => c.Id));
        Assert.True(RadialMenu.IsMore(ring[5]));
        Assert.Equal("More…", ring[5].Label);
        Assert.Equal(["c6", "c7", "c8", "c9", "c10"], RadialMenu.Overflow(ring[5]).Select(c => c.Id));
    }

    [Fact]
    public void Beyond_eight_categories_the_eighth_is_a_more_category_holding_the_rest()
    {
        var categories = Enumerable.Range(1, 10).Select(i => new CapabilityCategory($"k{i}", $"K{i}", "x", i)).ToArray();
        var menu = Menu(categories.Select(c => Cmd($"{c.Id}.cmd", c.Id)), categories);
        Assert.Equal(RadialMenu.RingCapacity, menu.Count);
        Assert.Equal(["K1", "K2", "K3", "K4", "K5", "K6", "K7", "More…"], menu.Select(g => g.Label));
        Assert.Equal(["k8.cmd", "k9.cmd", "k10.cmd"], menu[^1].Commands.Select(c => c.Id));
        Assert.Equal(CapabilityCategory.More, menu[^1].Category);
    }

    [Fact]
    public void A_more_category_that_is_itself_too_full_gets_a_more_command()
    {
        var categories = Enumerable.Range(1, 9).Select(i => new CapabilityCategory($"k{i}", $"K{i}", "x", i)).ToArray();
        var commands = categories.SelectMany(c => Enumerable.Range(1, 5).Select(j => Cmd($"{c.Id}.{j}", c.Id)));
        var more = Menu(commands, categories)[^1];
        Assert.Equal(6, more.Commands.Count);                       // 10 commands from K8 and K9: 5 + More… (V2 PR 7.2)
        Assert.Equal(5, RadialMenu.Overflow(more.Commands[5]).Count);
    }

    [Fact]
    public void Every_capability_stays_reachable()
    {
        var categories = Enumerable.Range(1, 12).Select(i => new CapabilityCategory($"k{i}", $"K{i}", "x", i)).ToArray();
        var commands = categories.SelectMany((c, n) => Enumerable.Range(1, n + 1).Select(j => Cmd($"{c.Id}.{j}", c.Id))).ToArray();
        var reachable = Menu(commands, categories).SelectMany(g => g.Commands).SelectMany(c => RadialMenu.IsMore(c) ? RadialMenu.Overflow(c) : [c]);
        Assert.Equal(commands.Select(c => c.Id).Order(), reachable.Select(c => c.Id).Order());
    }

    /// <summary>
    /// The cap of 8 sectors is about width: no sector may be narrower than V1's narrowest, which was tested by hand
    /// (a BMP's Resize: 7 commands beside a 90° Back with 4 categories, 38.6°), whatever the number of categories. With one
    /// category, Back used to take the whole circle and no command could be reached (evaluator, PR #17).
    /// </summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void No_command_sector_is_narrower_than_v1s_narrowest(int groups)
    {
        const double narrowest = 38.5 * Math.PI / 180;
        for (var commands = 1; commands <= RadialMenu.RingCapacity - 1; commands++)
        {
            var back = MenuNavigator.BackSweep(groups, commands);
            Assert.True(back >= narrowest - 1e-9, $"{groups} groups, {commands} commands: Back {back}");
            if (groups is 4 or 5) Assert.Equal(Math.Max(2 * Math.PI / (commands + 1), 2 * Math.PI / groups), back, 9); // V1's layout is unchanged
            Assert.True((2 * Math.PI - back) / commands >= narrowest - 1e-9, $"{groups} groups, {commands} commands: commands {(2 * Math.PI - back) / commands}");
        }
    }

    [Fact]
    public void Every_command_of_a_single_category_can_be_armed_by_a_stroke()
    {
        var menu = Menu(Enumerable.Range(1, 7).Select(i => Cmd($"c{i}", "a")), [new("a", "A", "a", 1)]);
        for (var c = 0; c < 7; c++)
        {
            var n = new MenuNavigator(menu, 96);
            var t = TimeSpan.Zero;
            n.Move(0, 0, t);
            var (ox, oy) = RingGeometry.PointAt(RingGeometry.SectorCentre(0, 1), RingGeometry.OpenRadius96 + 10);
            Assert.True(n.Move((int)ox, (int)oy, t));
            n.Shown(0, 0, t, 96);
            n.Move(0, 0, t);
            var (from, to) = RingGeometry.SectorBounds(c + 1, 8, MenuNavigator.LevelStart(0, 1, 7), MenuNavigator.BackSweep(1, 7));
            var (x, y) = RingGeometry.PointAt((from + to) / 2, 69);
            n.Move((int)Math.Round(x), (int)Math.Round(y), t);
            Assert.Equal($"c{c + 1}", n.Hot?.Id);
        }
    }

    [Fact]
    public void Overflow_of_an_ordinary_command_is_an_error()
    {
        Assert.Throws<ArgumentException>(() => RadialMenu.Overflow(Cmd("c", "a")));
    }
}

/// <summary>The pick list's answer is believed only for an id the host offered.</summary>
public sealed class PickContractTests
{
    private static readonly WinTools.Core.Jobs.PickRequest Request = new("More commands", [new("a.one", "One"), new("a.two", "Two")], 10, 20);

    [Theory]
    [InlineData("a.two", "a.two")]
    [InlineData(null, null)]
    [InlineData("a.three", null)]
    [InlineData("", null)]
    public void Only_an_offered_id_counts_as_a_choice(string? answered, string? chosen) =>
        Assert.Equal(chosen, new WinTools.Core.Jobs.PickResult(answered).ChosenFrom(Request));

    [Fact]
    public void A_request_crosses_the_pipe_intact()
    {
        var back = WinTools.Core.Jobs.WorkerProtocol.Deserialize<WinTools.Core.Jobs.PickRequest>(WinTools.Core.Jobs.WorkerProtocol.Serialize(Request))!;
        Assert.Equal(Request.Items, back.Items);
        Assert.Equal((Request.Title, Request.X, Request.Y), (back.Title, back.X, back.Y));
    }
}
