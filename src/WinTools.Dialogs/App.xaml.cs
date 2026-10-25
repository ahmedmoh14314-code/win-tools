using System.Text;
using System.Windows;
using WinTools.Core.Jobs;
using WinTools.Core.Localization;

namespace WinTools.Dialogs;

/// <summary>
/// Reads one request line from stdin, shows the dialog it asks for (a pick list, or a secret since V2 PR 2.2), writes one
/// answer line to stdout and exits. A malformed request shows nothing and answers "cancelled": the host decides what
/// that means.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The host's language ("--lang en|ar"): every window speaks it, Arabic right to left.
        for (var i = 0; i + 1 < e.Args.Length; i++) if (e.Args[i] == "--lang") UiText.Use(e.Args[i + 1]);
        var line = ReadRequestLine();
        if (WorkerProtocol.Deserialize<DialogEnvelope>(line)?.Kind == DialogKinds.Rename)
        {
            var rename = WorkerProtocol.Deserialize<RenameRequest>(line);
            if (rename is not { Title.Length: > 0, Names.Count: > 0 and <= RenameRequest.MaxNames })
            {
                Answer(new RenameResult(null));
                return;
            }
            var window = new RenameWindow(rename);
            window.Closed += (_, _) => Answer(new RenameResult(window.Rule));
            Present(window);
            return;
        }
        if (WorkerProtocol.Deserialize<DialogEnvelope>(line)?.Kind == DialogKinds.Secret)
        {
            var secret = WorkerProtocol.Deserialize<SecretRequest>(line);
            if (secret is not { Title.Length: > 0 })
            {
                Answer(new SecretResult(null));
                return;
            }
            var window = new SecretWindow(secret);
            window.Closed += (_, _) => Answer(new SecretResult(window.Secret));
            Present(window);
            return;
        }
        var request = WorkerProtocol.Deserialize<PickRequest>(line);
        if (request is not { Title: not null, Items.Count: > 0 })
        {
            Answer(new PickResult(null));
            return;
        }
        var pick = new PickWindow(request);
        pick.Closed += (_, _) => Answer(new PickResult(pick.Chosen));
        Present(pick);
    }

    /// <summary>
    /// Shows a window in the host's language: Arabic right to left. Set on each window, never by overriding Window's
    /// FlowDirection metadata, which WPF's own Window already overrides (doing it again throws when the first window is made).
    /// </summary>
    private static void Present(Window window)
    {
        window.FlowDirection = UiText.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        window.Show();
    }

    private static string? ReadRequestLine()
    {
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        return input.ReadLine();
    }

    private void Answer<T>(T result)
    {
        try
        {
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            output.WriteLine(WorkerProtocol.Serialize(result));
        }
        catch (IOException) { } // the host stopped listening (it gave up or exited): there is nobody left to answer
        Shutdown();
    }
}
