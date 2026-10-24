using WinTools.Core.Capabilities;
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
        new(id, id, category, "test", new InputRule(_ => true), CapabilityShape.PerFile, CapabilityOutput.Facts, false);

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
    public void Beyond_seven_commands_the_seventh_sector_is_more_holding_the_rest_in_order()
    {
        var menu = Menu(Enumerable.Range(1, 10).Select(i => Cmd($"c{i}", "a")), [new("a", "A", "a", 1)]);
        var ring = menu.Single().Commands;
        Assert.Equal(RadialMenu.RingCapacity - 1, ring.Count);       // with Back, a full ring
        Assert.Equal(["c1", "c2", "c3", "c4", "c5", "c6"], ring.Take(6).Select(c => c.Id));
        Assert.True(RadialMenu.IsMore(ring[6]));
        Assert.Equal("More…", ring[6].Label);
        Assert.Equal(["c7", "c8", "c9", "c10"], RadialMenu.Overflow(ring[6]).Select(c => c.Id));
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
        Assert.Equal(7, more.Commands.Count);                       // 10 commands from K8 and K9: 6 + More…
        Assert.Equal(4, RadialMenu.Overflow(more.Commands[6]).Count);
    }

    [Fact]
    public void Every_capability_stays_reachable()
    {
        var categories = Enumerable.Range(1, 12).Select(i => new CapabilityCategory($"k{i}", $"K{i}", "x", i)).ToArray();
        var commands = categories.SelectMany((c, n) => Enumerable.Range(1, n + 1).Select(j => Cmd($"{c.Id}.{j}", c.Id))).ToArray();
        var reachable = Menu(commands, categories).SelectMany(g => g.Commands).SelectMany(c => RadialMenu.IsMore(c) ? RadialMenu.Overflow(c) : [c]);
        Assert.Equal(commands.Select(c => c.Id).Order(), reachable.Select(c => c.Id).Order());
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
