using System.Diagnostics;
using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;
using WinTools.Windows;
using WinTools.Windows.Feedback;

namespace WinTools.Host;

/// <summary>
/// Turns job events into what the user sees and hears: a progress toast for a batch that takes a while, a result toast
/// (success, partial, failure, nothing to do), the info panel for "Image info", and the result sounds. Job events arrive
/// on pool threads and are handed to the UI thread; the windows are only touched there.
/// </summary>
internal sealed class FeedbackPresenter(MessageWindow ui, ToastWindow toast, InfoPanel info, SoundPlayer sounds, HostLog log) : IJobFeedback
{
    /// <summary>A batch shows progress only when it is still running after this long, so quick jobs show one toast.</summary>
    public static readonly TimeSpan ProgressDelay = TimeSpan.FromMilliseconds(450);

    private (int X, int Y) _anchor;       // where the last drop happened: its monitor shows the toast
    private Func<bool> _overlayVisible = static () => false;
    private Action _raiseOverlay = static () => { };
    private (InfoSection[] Sections, int More)? _deferredInfo; // an info panel that finished while a ring was open
    private long _startedAt;
    private int _batch;                   // counts batches, so a late progress update of an old batch is ignored
    private int _total, _done;

    /// <summary>
    /// Lets the presenter keep a live ring on top: a toast that appears during a drag is put below it again, and the
    /// info panel (which is not click-through and not a drop target) waits until the ring is gone.
    /// </summary>
    public void AttachOverlay(Func<bool> isVisible, Action bringToFront)
    {
        _overlayVisible = isVisible;
        _raiseOverlay = bringToFront;
    }

    /// <summary>The radial menu has closed (UI thread): show an info panel that was held back.</summary>
    public void OverlayHidden()
    {
        if (_deferredInfo is not { } d) return;
        _deferredInfo = null;
        info.Show(d.Sections, d.More, _anchor.X, _anchor.Y);
    }

    private void ShowToast(ToastContent content)
    {
        toast.Show(content, _anchor.X, _anchor.Y);
        if (_overlayVisible()) _raiseOverlay();
    }

    /// <summary>Where the user dropped (UI thread): toasts and the info panel appear on that monitor.</summary>
    public void DroppedAt(int x, int y) => _anchor = (x, y);

    /// <summary>The radial menu is about to show (UI thread): the info panel closes, and a finished toast makes way.</summary>
    public void OverlayShowing()
    {
        info.Hide();
        _deferredInfo = null;
        if (toast.Content is { Progress: null }) toast.Hide();
    }

    public void Started(CapabilityDescriptor capability, IReadOnlyList<string> paths) => ui.Post(() =>
    {
        var words = capability.Words; // the module's words: the presenter knows no module (V2 PR 1.4)
        // An instant command (File info, Copy path) leaves a running batch's progress alone (evaluator, PR #18).
        if (!capability.LongRunning) return;
        _batch++;
        _total = paths.Count;
        _done = 0;
        _startedAt = Stopwatch.GetTimestamp();
        var batch = _batch;
        var title = paths.Count == 1 ? $"{words.Working} {Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar))}" : $"{words.Working} {words.Count(paths.Count)}";
        _ = Task.Delay(ProgressDelay).ContinueWith(_ => ui.Post(() =>
        {
            if (batch != _batch || _done >= _total) return; // finished in the meantime
            ShowToast(new ToastContent(FeedbackKind.Success, title, ProgressText(), Fraction()));
            log.Event("toast", new { kind = "progress", files = _total });
        }), TaskScheduler.Default);
    });

    public void Progress(CapabilityDescriptor capability, int done, int total) => ui.Post(() =>
    {
        _done = Math.Max(_done, done);
        if (toast.Content is { Progress: not null } c) toast.Update(c with { Detail = ProgressText(), Progress = Fraction() });
    });

    public void Finished(CapabilityDescriptor capability, IReadOnlyList<JobItem> items) => ui.Post(() =>
    {
        if (capability.LongRunning) _done = _total;
        var words = capability.Words;
        var progressShowing = toast.Content is { Progress: not null };
        if (capability.Produces == CapabilityOutput.Clipboard && items.Count == 1)
        {
            var copied = FeedbackText.Copied(words, items[0]);
            if (!progressShowing) ShowToast(ToastContent.From(copied)); // a batch's progress toast is never replaced; the sound still says it
            sounds.Play(copied.Kind == FeedbackKind.Success ? UiSound.Success : UiSound.Failure);
            log.Event("toast", new { kind = copied.Kind.ToString(), copied = items[0].Inputs, shown = !progressShowing });
            return;
        }
        var facts = items.Where(i => i.Result.Success && i.Result.Facts.Count > 0).ToArray(); // only a believed success shows facts
        if (capability.Produces == CapabilityOutput.Facts && facts.Length > 0)
        {
            if (!progressShowing || capability.LongRunning) toast.Hide();
            var sections = facts.Take(InfoPanelRenderer.MaxSections)
                .Select(i => new InfoSection(i.SourceName, [.. i.Result.Facts.Select(f => new InfoRow(f.Label, f.Value, f.Notice))])).ToArray();
            if (_overlayVisible()) _deferredInfo = (sections, facts.Length - sections.Length);
            else info.Show(sections, facts.Length - sections.Length, _anchor.X, _anchor.Y);
            log.Event("info.show", new { files = facts.Length, shown = sections.Length, deferred = _deferredInfo is not null });
            var unread = items.Where(i => i.Result.Facts.Count == 0).ToArray();
            if (unread.Length > 0)
            {
                // Say which files could not be read, and why; never over a batch's progress toast (its updates would stop).
                if (!progressShowing || capability.LongRunning) ShowToast(ToastContent.From(FeedbackText.For(words, unread)));
                sounds.Play(UiSound.Failure);
            }
            return;
        }
        var message = FeedbackText.For(words, items);
        ShowToast(ToastContent.From(message));
        sounds.Play(message.Kind switch
        {
            FeedbackKind.Success => UiSound.Success,
            FeedbackKind.Nothing => UiSound.Nothing,
            _ => UiSound.Failure,
        });
        log.Event("toast", new { kind = message.Kind.ToString(), files = items.Count, ms = Math.Round(Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds, 1) });
    });

    /// <summary>
    /// The trigger was pressed during a drag with Alt held (UI thread): tell the user what to do instead. A batch's
    /// progress toast is never replaced (its updates would stop); the sound still says that nothing opened.
    /// </summary>
    public void TriggerRefused(string triggerKey, int x, int y)
    {
        sounds.Play(UiSound.Nothing);
        if (toast.Content is { Progress: not null })
        {
            log.Event("toast", new { kind = "triggerRefused", shown = false, reason = "progressShowing" });
            return;
        }
        toast.Show(new ToastContent(FeedbackKind.Nothing, $"Release Alt, then press {triggerKey}",
            "Windows uses Alt with keys for its own shortcuts, so WinTools does not open while Alt is held."), x, y);
        log.Event("toast", new { kind = "triggerRefused", shown = true });
    }

    /// <summary>A dialog the user asked for could not be shown (its program is missing or failed to start). Any thread.</summary>
    public void DialogUnavailable(string what = "the list of commands") => ui.Post(() =>
    {
        ShowToast(new ToastContent(FeedbackKind.Failure, $"Could not open {what}",
            "WinTools.Dialogs.exe is missing from this copy or could not start. Nothing was run; reinstall to fix it."));
        sounds.Play(UiSound.Failure);
        log.Event("toast", new { kind = "dialogUnavailable" });
    });

    /// <summary>A password the host's own check refused (V2 PR 2.2); the reason never contains the password. Any thread.</summary>
    public void SecretRefused(string reason) => ui.Post(() =>
    {
        ShowToast(new ToastContent(FeedbackKind.Failure, "The password was not used", $"{reason} Nothing was run."));
        sounds.Play(UiSound.Failure);
        log.Event("toast", new { kind = "secretRefused" });
    });

    public void Crashed(CapabilityDescriptor capability, int files) => ui.Post(() =>
    {
        if (capability.LongRunning) _done = _total;
        var words = capability.Words;
        ShowToast(new ToastContent(FeedbackKind.Failure, $"Could not {words.Verb} {(files == 1 ? $"the {words.Noun}" : words.Count(files))}",
            "Something went wrong. The originals are unchanged."));
        sounds.Play(UiSound.Failure);
        log.Event("toast", new { kind = "Failure", files });
    });

    private string ProgressText() => _total == 1 ? "Working…" : $"{_done} of {_total}";

    private double Fraction() => _total == 0 ? 0 : (double)_done / _total;

}
