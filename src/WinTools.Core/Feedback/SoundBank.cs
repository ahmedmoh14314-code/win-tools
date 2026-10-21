using System.Buffers.Binary;

namespace WinTools.Core.Feedback;

/// <summary>
/// The UI sounds, synthesised here as 16-bit mono PCM WAV images: no recorded or third-party audio is used. Each
/// sound is a few decaying sine partials with a short fade at both ends, so it starts and ends at silence (no click),
/// and its peak stays far below full scale, so it is quiet at any system volume.
/// </summary>
public static class SoundBank
{
    public const int SampleRate = 44_100;

    /// <summary>Largest sample of any sound, as a fraction of full scale.</summary>
    public const double MaxPeak = 0.2;

    private readonly record struct Partial(double Hz, double Amplitude, double DecayMs, double StartMs = 0);

    private readonly record struct Voice(double LengthMs, Partial[] Partials);

    private static Voice Define(UiSound sound) => sound switch
    {
        // A tiny, dry tick: high partials that die within a few milliseconds.
        UiSound.Tick => new(22, [new(2_450, 0.07, 3.0), new(3_900, 0.03, 2.0)]),
        // Opening a category or going back: a softer, lower "tock".
        UiSound.Level => new(70, [new(1_180, 0.08, 14), new(1_770, 0.035, 9)]),
        // Success: two rising notes (A5, E6), bell-like.
        UiSound.Success => new(300, [new(880, 0.09, 90), new(1_760, 0.02, 50), new(1_318.5, 0.09, 110, 70), new(2_637, 0.018, 60, 70)]),
        // Failure: two falling, lower notes (G4, E♭4), muted rather than harsh.
        UiSound.Failure => new(360, [new(392, 0.11, 110), new(784, 0.02, 60), new(311.1, 0.11, 140, 95), new(622.3, 0.02, 70, 95)]),
        // Nothing ran or nothing needed doing: one soft neutral note.
        UiSound.Nothing => new(140, [new(660, 0.07, 40), new(990, 0.02, 25)]),
        _ => throw new ArgumentOutOfRangeException(nameof(sound)),
    };

    /// <summary>The PCM samples of <paramref name="sound"/>, in [-1, 1].</summary>
    public static double[] Samples(UiSound sound)
    {
        var v = Define(sound);
        var n = (int)Math.Round(v.LengthMs * SampleRate / 1000);
        var fade = Math.Min(n / 4, SampleRate / 1000); // 1 ms in and out
        var s = new double[n];
        for (var i = 0; i < n; i++)
        {
            var tMs = i * 1000.0 / SampleRate;
            double x = 0;
            foreach (var p in v.Partials)
            {
                if (tMs < p.StartMs) continue;
                var local = tMs - p.StartMs;
                var onset = Math.Min(1, local / 0.6); // 0.6 ms attack per partial
                x += p.Amplitude * onset * Math.Exp(-local / p.DecayMs) * Math.Sin(2 * Math.PI * p.Hz * local / 1000);
            }
            var edge = Math.Min(1, Math.Min(i, n - 1 - i) / (double)fade);
            s[i] = x * edge;
        }
        return s;
    }

    /// <summary>A complete RIFF/WAVE file image of <paramref name="sound"/> (PCM, 16-bit, mono).</summary>
    public static byte[] Wav(UiSound sound) => Wav(Samples(sound));

    public static byte[] Wav(double[] samples)
    {
        var data = samples.Length * 2;
        var wav = new byte[44 + data];
        var w = wav.AsSpan();
        "RIFF"u8.CopyTo(w);
        BinaryPrimitives.WriteInt32LittleEndian(w[4..], 36 + data);
        "WAVEfmt "u8.CopyTo(w[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(w[16..], 16);         // fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(w[20..], 1);          // PCM
        BinaryPrimitives.WriteInt16LittleEndian(w[22..], 1);          // mono
        BinaryPrimitives.WriteInt32LittleEndian(w[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(w[28..], SampleRate * 2); // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(w[32..], 2);          // block align
        BinaryPrimitives.WriteInt16LittleEndian(w[34..], 16);         // bits per sample
        "data"u8.CopyTo(w[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(w[40..], data);
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(w[(44 + i * 2)..], (short)Math.Round(Math.Clamp(samples[i], -1, 1) * short.MaxValue));
        return wav;
    }
}
