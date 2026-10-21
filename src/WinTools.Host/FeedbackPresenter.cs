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
    private long _startedAt;
    private int _batch;                   // counts batches, so a late progress update of an old batch is ignored
    private int _total, _done;

    /// <summary>Where the user dropped (UI thread): toasts and the info panel appear on that monitor.</summary>
    public void DroppedAt(int x, int y) => _anchor = (x, y);

    /// <summary>The radial menu is about to show (UI thread): the info panel closes, and a finished toast makes way.</summary>
    public void OverlayShowing()
    {
        info.Hide();
        if (toast.Content is { Progress: null }) toast.Hide();
    }

    public void Started(Operation operation, IReadOnlyList<string> paths) => ui.Post(() =>
    {
        _batch++;
        _total = paths.Count;
        _done = 0;
        _startedAt = Stopwatch.GetTimestamp();
        if (operation.Kind == OperationKind.Info) return;
        var batch = _batch;
        var title = paths.Count == 1 ? $"{Working(operation)} {Path.GetFileName(paths[0])}" : $"{Working(operation)} {paths.Count} images";
        _ = Task.Delay(ProgressDelay).ContinueWith(_ => ui.Post(() =>
        {
            if (batch != _batch || _done >= _total) return; // finished in the meantime
            toast.Show(new ToastContent(FeedbackKind.Success, title, ProgressText(), Fraction()), _anchor.X, _anchor.Y);
            log.Event("toast", new { kind = "progress", files = _total });
        }), TaskScheduler.Default);
    });

    public void Progress(Operation operation, int done, int total) => ui.Post(() =>
    {
        _done = Math.Max(_done, done);
        if (toast.Content is { Progress: not null } c) toast.Update(c with { Detail = ProgressText(), Progress = Fraction() });
    });

    public void Finished(Operation operation, IReadOnlyList<JobItem> items) => ui.Post(() =>
    {
        _done = _total;
        var facts = items.Where(i => i.Result.Info is not null).ToArray();
        if (operation.Kind == OperationKind.Info && facts.Length > 0)
        {
            toast.Hide();
            var sections = facts.Take(3).Select(i => new InfoSection(i.SourceName, InfoRows.For(i.Result.Info!))).ToArray();
            info.Show(sections, facts.Length - sections.Length, _anchor.X, _anchor.Y);
            log.Event("info.show", new { files = facts.Length, shown = sections.Length });
            if (facts.Length < items.Count) sounds.Play(UiSound.Failure);
            return;
        }
        var message = FeedbackText.For(operation, items);
        toast.Show(ToastContent.From(message), _anchor.X, _anchor.Y);
        sounds.Play(message.Kind switch
        {
            FeedbackKind.Success => UiSound.Success,
            FeedbackKind.Nothing => UiSound.Nothing,
            _ => UiSound.Failure,
        });
        log.Event("toast", new { kind = message.Kind.ToString(), files = items.Count, ms = Math.Round(Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds, 1) });
    });

    public void Crashed(Operation operation, int files) => ui.Post(() =>
    {
        _done = _total;
        toast.Show(new ToastContent(FeedbackKind.Failure, $"Could not {FeedbackText.Verb(operation)} {(files == 1 ? "the image" : $"{files} images")}",
            "Something went wrong. The originals are unchanged."), _anchor.X, _anchor.Y);
        sounds.Play(UiSound.Failure);
        log.Event("toast", new { kind = "Failure", files });
    });

    private string ProgressText() => _total == 1 ? "Working…" : $"{_done} of {_total}";

    private double Fraction() => _total == 0 ? 0 : (double)_done / _total;

    private static string Working(Operation op) => op.Kind switch
    {
        OperationKind.Convert => "Converting",
        OperationKind.Compress => "Compressing",
        OperationKind.Resize => "Resizing",
        OperationKind.Rotate => "Rotating",
        OperationKind.StripMetadata => "Removing metadata from",
        _ => "Reading",
    };
}
