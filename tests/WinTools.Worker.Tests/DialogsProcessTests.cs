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

    /// <summary>
    /// The process is still running after a few seconds and has written nothing to stderr. A crash in its first window
    /// prints the exception at once, though Windows Error Reporting may keep the process alive for seconds after it (PR
    /// #37 re-review), so stderr is watched as well as the exit.
    /// </summary>
    private static void AssertWaitsForTheUser(Process p)
    {
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) errors.Enqueue(e.Data); };
        p.BeginErrorReadLine();
        try
        {
            var exited = p.WaitForExit(3000);
            Assert.True(!exited && errors.IsEmpty, $"exited={exited}: {string.Join(" | ", errors.Take(3))}");
        }
        finally
        {
            if (!p.HasExited) p.Kill();
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void A_window_opens_and_waits_in_either_language(string language)
    {
        var request = WorkerProtocol.Serialize(new PickRequest("Test", [new PickItem("a", "A"), new PickItem("b", "B")], 200, 200));
        using var p = Start(language, request);
        AssertWaitsForTheUser(p); // a window that could not be made crashes the process (PR #37 review)
    }

    [Theory]
    [InlineData("en", "media.trim")]
    [InlineData("ar", "media.split")]
    [InlineData("ar", "subtitles.shift-custom")]
    public void A_form_opens_and_waits_in_either_language(string language, string form)
    {
        using var p = Start(language, WorkerProtocol.Serialize(new FormRequest("Test", form, 200, 200)));
        try
        {
            var exited = p.WaitForExit(3000);
            Assert.False(exited, exited ? $"exit {p.ExitCode}: {p.StandardError.ReadToEnd()} {p.StandardOutput.ReadToEnd()}" : "");
        }
        finally
        {
            if (!p.HasExited) p.Kill();
        }
    }

    [Fact]
    public void A_form_for_a_command_without_one_is_answered_cancelled()
    {
        using var p = Start("en", WorkerProtocol.Serialize(new FormRequest("Test", "media.mono", 200, 200)));
        var line = p.StandardOutput.ReadLine();
        Assert.True(p.WaitForExit(10_000));
        Assert.Null(WorkerProtocol.Deserialize<FormResult>(line)!.Answer);
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
