using System.Diagnostics;
using System.Text;
using WinTools.Core.Jobs;

namespace WinTools.Host;

/// <summary>
/// Starts the dialogs process (WinTools.Dialogs.exe) for one dialog and waits for its answer (V2 PR 1.3). The request
/// goes on its stdin and the answer comes on its stdout, one JSON line each; it never receives file paths, which stay
/// in the host. A dialog that does not answer within <see cref="Patience"/> is closed and counts as cancelled.
/// </summary>
internal sealed class DialogsClient(string dialogsExe)
{
    /// <summary>How long a dialog may stay open before the host gives up on it.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromMinutes(10);

    public static DialogsClient Default { get; } = new(Path.Combine(AppContext.BaseDirectory, "WinTools.Dialogs.exe"));

    /// <summary>The id the user picked, or null when they cancelled, the dialog failed, or it was not there.</summary>
    public async Task<string?> PickAsync(PickRequest request, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(dialogsExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(dialogsExe)!,
        };
        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null."); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
        using (process)
        {
            _ = WinTools.Windows.Native.User32.AllowSetForegroundWindow((uint)process.Id); // the user just dropped: the dialog may take the keyboard
            using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            patience.CancelAfter(Patience);
            try
            {
                await process.StandardInput.WriteLineAsync(WorkerProtocol.Serialize(request).AsMemory(), patience.Token).ConfigureAwait(false);
                process.StandardInput.Close();
                var line = await process.StandardOutput.ReadLineAsync(patience.Token).ConfigureAwait(false);
                return WorkerProtocol.Deserialize<PickResult>(line)?.ChosenFrom(request);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                return null;
            }
        }
    }
}
