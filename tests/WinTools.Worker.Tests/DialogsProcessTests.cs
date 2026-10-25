using System.Diagnostics;
using System.Text;
using WinTools.Core.Jobs;

namespace WinTools.Worker.Tests;

/// <summary>
/// The dialogs process as the host starts it (--lang, one request line on stdin): in either language it opens its window
/// and waits for the user, and a malformed request is answered "cancelled" at once. The window is closed by ending the
/// process; nothing is clicked.
/// </summary>
public sealed class DialogsProcessTests
{
    private static readonly string Exe = Path.Combine(AppContext.BaseDirectory, "WinTools.Dialogs.exe");

    private static Process Start(string language, string requestLine)
    {
        var psi = new ProcessStartInfo(Exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            ArgumentList = { "--lang", language },
        };
        var p = Process.Start(psi)!;
        p.StandardInput.WriteLine(requestLine);
        p.StandardInput.Close();
        return p;
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void A_window_opens_and_waits_in_either_language(string language)
    {
        var request = WorkerProtocol.Serialize(new PickRequest("Test", [new PickItem("a", "A"), new PickItem("b", "B")], 200, 200));
        using var p = Start(language, request);
        try
        {
            // A window that could not be made ends the process at once (a crash in its first window, PR #37 review).
            var exited = p.WaitForExit(3000);
            Assert.False(exited, exited ? $"exit {p.ExitCode}: {p.StandardError.ReadToEnd()}" : "");
        }
        finally
        {
            if (!p.HasExited) p.Kill();
        }
    }

    [Fact]
    public void A_malformed_request_is_answered_cancelled()
    {
        using var p = Start("ar", "{not json");
        var line = p.StandardOutput.ReadLine();
        Assert.True(p.WaitForExit(10_000));
        Assert.Null(WorkerProtocol.Deserialize<PickResult>(line)?.Chosen);
        Assert.NotNull(line);
    }
}
