using System.Text;
using System.Windows;
using WinTools.Core.Jobs;

namespace WinTools.Dialogs;

/// <summary>
/// Reads one request line from stdin, shows the dialog, writes one answer line to stdout and exits. A malformed request
/// shows nothing and answers "cancelled": the host decides what that means.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var request = WorkerProtocol.Deserialize<PickRequest>(ReadRequestLine());
        if (request is not { Title: not null, Items.Count: > 0 })
        {
            Answer(new PickResult(null));
            return;
        }
        var window = new PickWindow(request);
        window.Closed += (_, _) => Answer(new PickResult(window.Chosen));
        window.Show();
    }

    private static string? ReadRequestLine()
    {
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        return input.ReadLine();
    }

    private void Answer(PickResult result)
    {
        using (var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)))
            output.WriteLine(WorkerProtocol.Serialize(result));
        Shutdown();
    }
}
