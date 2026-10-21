using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WinTools.Core.Feedback;
using WinTools.Windows.Native;

namespace WinTools.Windows.Feedback;

/// <summary>
/// Plays the UI sounds without ever blocking the caller: <see cref="Play"/> only enqueues. A dedicated background
/// thread, started on the first sound, hands each one to PlaySound (asynchronous, from memory), so the audio stack's
/// start-up cost and any device delay stay off the overlay thread. A new sound cuts the previous one short (PlaySound's
/// behaviour), which suits short ticks. Sounds that pile up while the thread is busy are collapsed to one (a result
/// sound wins over ticks) or dropped when the small queue is full, never played late.
/// The sound images are synthesised once (<see cref="SoundBank"/>) and pinned for the process's lifetime, as
/// PlaySound with SND_MEMORY | SND_ASYNC requires.
/// </summary>
public sealed class SoundPlayer : IDisposable
{
    private static readonly Lazy<Dictionary<UiSound, nint>> Bank = new(() =>
        Enum.GetValues<UiSound>().ToDictionary(s => s, s =>
        {
            var wav = SoundBank.Wav(s);
            var p = Marshal.AllocHGlobal(wav.Length); // never freed: a handful of kilobytes, alive while any sound may play
            Marshal.Copy(wav, 0, p, wav.Length);
            return p;
        }));

    private readonly BlockingCollection<UiSound> _queue = new(boundedCapacity: 4);
    private Thread? _thread;
    private volatile bool _enabled;

    public SoundPlayer(bool enabled) => _enabled = enabled;

    /// <summary>The "UI sounds" setting. Off, nothing is played and no audio thread is started.</summary>
    public bool Enabled { get => _enabled; set => _enabled = value; }

    /// <summary>How many sounds were handed to the audio thread (for the log and tests).</summary>
    public int Requested => _requested;
    private int _requested;

    /// <summary>Queues <paramref name="sound"/>; returns at once. Callable from any thread.</summary>
    public void Play(UiSound sound)
    {
        if (!_enabled || _queue.IsAddingCompleted) return;
        if (_thread is null) Warm();
        if (_queue.TryAdd(sound)) Interlocked.Increment(ref _requested);
    }

    /// <summary>Starts the audio thread now (when enabled), so the first real sound is not delayed by the audio stack starting.</summary>
    public void Warm()
    {
        if (!_enabled) return;
        lock (_queue)
        {
            if (_thread is not null) return;
            _thread = new Thread(Run) { Name = "ui sounds", IsBackground = true };
            _thread.Start();
        }
    }

    private void Run()
    {
        try
        {
            // 10 ms of silence: loads and opens the audio path once, on this thread.
            var silence = SoundBank.Wav(new double[SoundBank.SampleRate / 100]);
            var handle = GCHandle.Alloc(silence, GCHandleType.Pinned);
            try { _ = Ui.PlaySoundW(handle.AddrOfPinnedObject(), 0, Ui.SND_MEMORY | Ui.SND_NODEFAULT); } // synchronous: 10 ms
            finally { handle.Free(); }
            foreach (var first in _queue.GetConsumingEnumerable())
            {
                // Of a burst that arrived together only one is played: the latest, unless a result sound is among them.
                var sound = first;
                while (_queue.TryTake(out var newer))
                    if (newer is not (UiSound.Tick or UiSound.Level) || sound is UiSound.Tick or UiSound.Level) sound = newer;
                if (!_enabled) continue;
                _ = Ui.PlaySoundW(Bank.Value[sound], 0, Ui.SND_MEMORY | Ui.SND_ASYNC | Ui.SND_NODEFAULT);
            }
        }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (_thread is { IsAlive: true } t)
        {
            t.Join(500);
            _ = Ui.PlaySoundW(0, 0, 0); // stop whatever is playing
        }
        _queue.Dispose();
    }
}
