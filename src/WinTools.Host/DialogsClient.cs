using System.Diagnostics;
using System.Text;
using WinTools.Core.Jobs;

namespace WinTools.Host;

/// <summary>How a dialog ended: an offered id was picked, the user cancelled, or the dialog could not be shown at all.</summary>
internal enum PickEnd { Picked, Cancelled, Unavailable }

/// <summary>A dialog's outcome; <see cref="Id"/> is set only for <see cref="PickEnd.Picked"/>.</summary>
internal readonly record struct PickOutcome(PickEnd End, string? Id = null);

/// <summary>A secret dialog's outcome (V2 PR 2.2); <see cref="Secret"/> is set only for <see cref="PickEnd.Picked"/>. Never logged.</summary>
internal readonly record struct SecretOutcome(PickEnd End, string? Secret = null);

/// <summary>The rename dialog's outcome (V2 PR 7.2); <see cref="Rule"/> is set only for <see cref="PickEnd.Picked"/>.</summary>
internal readonly record struct RenameOutcome(PickEnd End, string? Rule = null);

/// <summary>
/// Starts the dialogs process (WinTools.Dialogs.exe) for one dialog and waits for its answer (V2 PR 1.3). The request
/// goes on its stdin and the answer comes on its stdout, one JSON line each; it never receives file paths, which stay
/// in the host. A dialog that does not answer within <see cref="Patience"/> is closed and counts as cancelled. The
/// process joins the host's kill-on-close job, so it never outlives the host, and is ended once it has answered.
/// </summary>
internal sealed class DialogsClient(string dialogsExe)
{
    /// <summary>How long a dialog may stay open before the host gives up on it.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromMinutes(10);

    /// <summary>How long a dialog that answered may take to exit before it is ended.</summary>
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);

    public static DialogsClient Default { get; } = new(Path.Combine(AppContext.BaseDirectory, "WinTools.Dialogs.exe"));

    public async Task<PickOutcome> PickAsync(PickRequest request, CancellationToken cancellationToken = default)
    {
        var (end, line) = await AskAsync(WorkerProtocol.Serialize(request), cancellationToken).ConfigureAwait(false);
        if (end != PickEnd.Picked) return new(end);
        return WorkerProtocol.Deserialize<PickResult>(line)?.ChosenFrom(request) is { } id ? new(PickEnd.Picked, id) : new(PickEnd.Cancelled);
    }

    /// <summary>
    /// Asks for a secret (V2 PR 2.2, ADR-0026). The answer line is parsed here and dropped; the secret is returned only to
    /// the caller, which passes it to one job. An empty answer counts as cancelled.
    /// </summary>
    public async Task<SecretOutcome> AskSecretAsync(SecretRequest request, CancellationToken cancellationToken = default)
    {
        var (end, line) = await AskAsync(WorkerProtocol.Serialize(request), cancellationToken).ConfigureAwait(false);
        if (end != PickEnd.Picked) return new(end);
        return WorkerProtocol.Deserialize<SecretResult>(line)?.Secret is { Length: > 0 } secret ? new(PickEnd.Picked, secret) : new(PickEnd.Cancelled);
    }

    /// <summary>Asks for a Batch rename rule (V2 PR 7.2): only one the host can parse counts as an answer.</summary>
    public async Task<RenameOutcome> AskRenameAsync(RenameRequest request, CancellationToken cancellationToken = default)
    {
        var (end, line) = await AskAsync(WorkerProtocol.Serialize(request), cancellationToken).ConfigureAwait(false);
        if (end != PickEnd.Picked) return new(end);
        return WorkerProtocol.Deserialize<RenameResult>(line)?.Rule is { } rule && WinTools.Core.Files.RenameRule.Parse(rule) is not null
            ? new(PickEnd.Picked, rule) : new(PickEnd.Cancelled);
    }

    /// <summary>One dialog: the request line in, the answer line out (Picked means "it answered").</summary>
    private async Task<(PickEnd End, string? Line)> AskAsync(string requestLine, CancellationToken cancellationToken)
    {
        if (!File.Exists(dialogsExe)) return (PickEnd.Unavailable, null);
        var psi = new ProcessStartInfo(dialogsExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(dialogsExe)!,
            ArgumentList = { "--lang", WinTools.Core.Localization.UiText.Language },
        };
        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null."); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (PickEnd.Unavailable, null); }
        using (process)
        {
            WinTools.Windows.WorkerJobObject.TryAssign(process); // ends with the host, like the workers
            _ = WinTools.Windows.Native.User32.AllowSetForegroundWindow((uint)process.Id); // effective only if the host may set the foreground itself (ADR-0019)
            using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            patience.CancelAfter(Patience);
            try
            {
                await process.StandardInput.WriteLineAsync(requestLine.AsMemory(), patience.Token).ConfigureAwait(false);
                process.StandardInput.Close();
                var line = await process.StandardOutput.ReadLineAsync(patience.Token).ConfigureAwait(false);
                return line is null ? (PickEnd.Unavailable, null) : (PickEnd.Picked, line); // no line: it could not show the dialog
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { return (PickEnd.Cancelled, null); }
            finally { await EndAsync(process).ConfigureAwait(false); }
        }
    }

    /// <summary>Lets an answered dialog exit on its own for a moment, then ends it. Never throws.</summary>
    private static async Task EndAsync(Process process)
    {
        try
        {
            using var grace = new CancellationTokenSource(ExitGrace);
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } // exited meanwhile, or access denied
        }
        catch (InvalidOperationException) { }
    }
}
