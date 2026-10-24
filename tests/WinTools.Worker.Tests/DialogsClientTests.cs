using WinTools.Core.Jobs;
using WinTools.Host;

namespace WinTools.Worker.Tests;

/// <summary>
/// The host side of the dialogs process (V2 PR 1.3): a missing program is reported as such, never as a silent cancel, and
/// an answer that is not a pick result is a cancel. The dialog itself (WPF) is not started here.
/// </summary>
public sealed class DialogsClientTests
{
    private static readonly PickRequest Request = new("More commands", [new("a.one", "One")], 0, 0);

    [Fact]
    public async Task A_missing_dialogs_program_is_unavailable()
    {
        var client = new DialogsClient(Path.Combine(AppContext.BaseDirectory, "no-such-dir", "WinTools.Dialogs.exe"));
        Assert.Equal(new PickOutcome(PickEnd.Unavailable), await client.PickAsync(Request));
    }

    [Fact]
    public async Task A_program_that_ends_without_answering_is_unavailable()
    {
        // The image worker skips a line that is not one of its messages and exits when its stdin closes: no answer at all,
        // which is a dialog that could not be shown, not a choice to cancel.
        var client = new DialogsClient(Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.exe"));
        Assert.Equal(new PickOutcome(PickEnd.Unavailable), await client.PickAsync(Request).WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Theory]
    [InlineData("{\"chosen\":\"a.one\"}", true)]
    [InlineData("{\"chosen\":\"a.two\"}", false)]   // not offered
    [InlineData("{\"id\":\"a.one\"}", false)]       // not a pick result
    [InlineData("not json", false)]
    public void Only_an_offered_id_is_a_pick(string line, bool picked) =>
        Assert.Equal(picked, WorkerProtocol.Deserialize<PickResult>(line)?.ChosenFrom(Request) is not null);
}
