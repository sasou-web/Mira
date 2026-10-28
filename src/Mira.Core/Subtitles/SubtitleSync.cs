using System.Numerics;

namespace Mira.Core.Subtitles;

/// <summary>
/// What aligning a subtitle file on its video found: shift and speed to apply to its times (time × Ratio + Shift),
/// how much of its lines then fall on speech (Fit, 0 to 1, and FitBefore, as fetched), and how far the best position
/// stands above all the others tried (Confidence, in standard deviations). Confident: Mira may apply it, and may take a
/// subtitle that was only worth taking once aligned (<see cref="MatchVerdict.IfSynced"/>).
/// </summary>
public sealed record SubtitleSync(TimeSpan Shift, double Ratio, double Fit, double FitBefore, double Confidence, bool Confident)
{
    /// <summary>Whether applying it changes anything worth changing (more than a frame of the reference, or another speed).</summary>
    public bool Changes => Confident && (Math.Abs(Shift.TotalMilliseconds) >= SubtitleAlign.FrameMs * 2 || Ratio != 1);
}

/// <summary>
/// Aligns subtitles on a reference of when people speak in the video: another subtitle track of the file (any
/// language: the lines fall at the same moments), or its audio, where speech is told from silence and noise by its
/// energy (<see cref="SpeechDetector"/>). Both become one value per <see cref="FrameMs"/> ms; the subtitles' own lines,
/// at each speed a release may have been made for (<see cref="Ratios"/>), are slid along the reference, and the
/// position where most of their lines fall on speech wins (a cross-correlation, computed by FFT). The method of
/// ffsubsync and alass, written for Mira. Never anything from the network: the video's own file only.
/// </summary>
public static class SubtitleAlign
{
    public const int FrameMs = 20;
    /// <summary>Furthest a file is moved: further is another cut or another file, not a shift.</summary>
    public static readonly TimeSpan MaxShift = TimeSpan.FromSeconds(90);
    /// <summary>The speeds a release is made at, paired: a subtitle for a 25 fps release (PAL) on a 23.976 fps file, and the other ways.</summary>
    public static readonly double[] Ratios = [1, 25 / 23.976, 23.976 / 25, 24 / 23.976, 23.976 / 24, 25 / 24.0, 24 / 25.0];
    /// <summary>
    /// The best position must stand this many standard deviations above the others: one clear peak, not a plateau.
    /// Measured on synthesized French speech over music and noise: about 7 for the right file at any shift or speed,
    /// about 3.5 for the lines of another episode.
    /// </summary>
    public const double MinConfidence = 5;
    /// <summary>
    /// And its lines must then fall on speech this many times more than chance would put them (the share of the film
    /// that is speech): about 2 for the right file, 1.2 for another episode.
    /// </summary>
    public const double MinLift = 1.5;

    /// <summary>The frames of a duration.</summary>
    public static int Frames(TimeSpan duration) => (int)Math.Ceiling(duration.TotalMilliseconds / FrameMs);

    /// <summary>When the lines are on screen, one value per frame (1 during a line), at a speed and shift.</summary>
    public static float[] Lines(IReadOnlyList<SubtitleCue> cues, int frames, double ratio = 1, TimeSpan shift = default)
    {
        var lines = new float[frames];
        foreach (var cue in cues)
        {
            var from = (int)Math.Round((cue.Start.TotalMilliseconds * ratio + shift.TotalMilliseconds) / FrameMs);
            var to = (int)Math.Round((cue.End.TotalMilliseconds * ratio + shift.TotalMilliseconds) / FrameMs);
            for (var i = Math.Max(0, from); i < Math.Min(frames, to); i++) lines[i] = 1;
        }
        return lines;
    }

    /// <summary>
    /// Where the subtitles fit the reference best (see the class). Null when either has nothing to align (no line, no
    /// speech). The reference is as long as the video.
    /// </summary>
    public static SubtitleSync? Align(float[] reference, IReadOnlyList<SubtitleCue> cues)
    {
        if (cues.Count == 0 || reference.Length == 0) return null;
        var speech = reference.Sum();
        if (speech <= 0) return null;
        var max = Math.Min(Frames(MaxShift), reference.Length);
        var size = (int)BitOperations.RoundUpToPowerOf2((uint)(reference.Length + max + 1));
        // The reference less its mean: a position counts the lines on speech beyond what chance would give.
        var mean = speech / reference.Length;
        var spectrum = new Complex[size];
        for (var i = 0; i < reference.Length; i++) spectrum[i] = reference[i] - mean;
        Fft(spectrum, false);

        var before = Fit(reference, Lines(cues, reference.Length));
        (double Ratio, int Shift, double Value, double Confidence)? best = null;
        foreach (var ratio in Ratios)
        {
            var lines = Lines(cues, reference.Length, ratio);
            var total = lines.Sum();
            if (total <= 0) continue;
            var other = new Complex[size];
            for (var i = 0; i < lines.Length; i++) other[i] = lines[i];
            Fft(other, false);
            for (var i = 0; i < size; i++) other[i] = spectrum[i] * Complex.Conjugate(other[i]);
            Fft(other, true);
            // other[k] = Σ reference'[i + k] · lines[i]: the lines moved k frames later (k < 0: earlier, at size + k).
            double Value(int k) => other[k >= 0 ? k : size + k].Real;
            var top = 0; var topValue = double.MinValue; double sum = 0, squares = 0; var count = 0;
            for (var k = -max; k <= max; k++)
            {
                var v = Value(k);
                sum += v; squares += v * v; count++;
                if (v > topValue) { topValue = v; top = k; }
            }
            var average = sum / count;
            var deviation = Math.Sqrt(Math.Max(1e-9, squares / count - average * average));
            var confidence = (topValue - average) / deviation;
            // Another speed has to win clearly: at the same confidence, the release's own speed is the likelier.
            var weighted = ratio == 1 ? confidence : confidence * 0.9;
            if (best is null || weighted > (best.Value.Ratio == 1 ? best.Value.Confidence : best.Value.Confidence * 0.9))
                best = (ratio, top, topValue, confidence);
        }
        if (best is not { } found) return null;
        var shift = TimeSpan.FromMilliseconds(found.Shift * (double)FrameMs);
        var speed = found.Ratio;
        var fit = Fit(reference, Lines(cues, reference.Length, speed, shift));
        // Finer than the frame and the listed speeds: each line's start against the speech that starts nearest to it.
        // A drift worth undoing only: under 80 ms over the whole film, it is the measure's noise.
        if (Refine(reference, cues, speed, shift) is { } refined && Math.Abs(refined.Ratio / speed - 1) * reference.Length * FrameMs >= FrameMs * 4)
        {
            var refinedFit = Fit(reference, Lines(cues, reference.Length, refined.Ratio, refined.Shift));
            if (refinedFit >= fit - 0.02) (speed, shift, fit) = (refined.Ratio, refined.Shift, refinedFit);
        }
        var lift = fit / mean;
        return new SubtitleSync(shift, speed, fit, before, found.Confidence, found.Confidence >= MinConfidence && lift >= MinLift);
    }

    /// <summary>
    /// Once roughly aligned: the start of each line against the nearest start of speech (within 0.6 s), and a straight
    /// line through those gaps over the film (outliers left out), whose slope corrects the speed finer than the listed
    /// ones. Null when too few lines start near speech to tell, or the slope is beyond a speed error (0.2 %).
    /// </summary>
    public static (double Ratio, TimeSpan Shift)? Refine(float[] reference, IReadOnlyList<SubtitleCue> cues, double ratio, TimeSpan shift)
    {
        var onsets = new List<double>();
        for (var i = 1; i < reference.Length; i++)
            if (reference[i] > 0.5 && reference[i - 1] <= 0.5) onsets.Add(i * (double)FrameMs);
        if (onsets.Count < 20) return null;
        var points = new List<(double At, double Gap)>();
        foreach (var cue in cues)
        {
            var at = cue.Start.TotalMilliseconds * ratio + shift.TotalMilliseconds;
            var index = onsets.BinarySearch(at);
            if (index < 0) index = ~index;
            var nearest = double.MaxValue;
            if (index < onsets.Count) nearest = onsets[index] - at;
            if (index > 0 && Math.Abs(onsets[index - 1] - at) < Math.Abs(nearest)) nearest = onsets[index - 1] - at;
            if (Math.Abs(nearest) <= 600) points.Add((at, nearest));
        }
        if (points.Count < 20 || points.Count < cues.Count / 4) return null;
        var median = points.Select(p => p.Gap).OrderBy(x => x).ElementAt(points.Count / 2);
        var spread = points.Select(p => Math.Abs(p.Gap - median)).OrderBy(x => x).ElementAt(points.Count / 2);
        var kept = points.Where(p => Math.Abs(p.Gap - median) <= Math.Max(3 * spread, FrameMs * 2)).ToList();
        if (kept.Count < 20) return null;
        // Gap = a + b · at. Only the drift b is taken: a line starts on screen a little before the voice (by habit), which
        // the starts of speech would undo; the rough alignment, made on whole lines, has no such lean. The times become
        // at + b · (at − middle): unchanged in the middle of the film, the drift undone toward its ends.
        double meanAt = kept.Average(p => p.At), meanGap = kept.Average(p => p.Gap);
        double covariance = kept.Sum(p => (p.At - meanAt) * (p.Gap - meanGap)), variance = kept.Sum(p => (p.At - meanAt) * (p.At - meanAt));
        var b = variance > 0 ? covariance / variance : 0;
        if (Math.Abs(b) > 0.002) return null;
        return (ratio * (1 + b), TimeSpan.FromMilliseconds(shift.TotalMilliseconds * (1 + b) - b * meanAt));
    }

    /// <summary>The share of the lines' time that falls on speech.</summary>
    public static double Fit(float[] reference, float[] lines)
    {
        double on = 0, total = 0;
        for (var i = 0; i < Math.Min(reference.Length, lines.Length); i++) { total += lines[i]; on += lines[i] * reference[i]; }
        return total > 0 ? on / total : 0;
    }

    /// <summary>The lines moved and stretched as found; a line pushed before the start is dropped.</summary>
    public static List<SubtitleCue> Apply(IReadOnlyList<SubtitleCue> cues, SubtitleSync sync)
    {
        TimeSpan At(TimeSpan time) => TimeSpan.FromMilliseconds(Math.Round(time.TotalMilliseconds * sync.Ratio + sync.Shift.TotalMilliseconds));
        return cues.Select(c => c with { Start = At(c.Start), End = At(c.End) }).Where(c => c.End > TimeSpan.Zero)
            .Select(c => c.Start < TimeSpan.Zero ? c with { Start = TimeSpan.Zero } : c).ToList();
    }

    /// <summary>In-place radix-2 FFT (inverse scaled by 1/n). The length is a power of two.</summary>
    public static void Fft(Complex[] data, bool inverse)
    {
        var n = data.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = 2 * Math.PI / length * (inverse ? 1 : -1);
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var i = 0; i < n; i += length)
            {
                var w = Complex.One;
                for (var k = 0; k < length / 2; k++)
                {
                    var u = data[i + k];
                    var v = data[i + k + length / 2] * w;
                    data[i + k] = u + v;
                    data[i + k + length / 2] = u - v;
                    w *= step;
                }
            }
        }
        if (inverse)
            for (var i = 0; i < n; i++) data[i] /= n;
    }
}

/// <summary>
/// Tells speech from silence and background in a video's audio, from its energy: mono 16-bit samples at
/// <see cref="Rate"/> Hz, filtered to the voice's band beforehand (ffmpeg: highpass 200 Hz, lowpass 3 kHz), fed in any
/// pieces. A film is never silent (music, a room, the street): speech is what rises well above the background around
/// it, the lowest energy within <see cref="Around"/> frames, as speech comes in syllables with short dips between them
/// and a background stays. Short gaps are then filled and lone blips dropped, as speech comes in phrases.
/// </summary>
public sealed class SpeechDetector
{
    public const int Rate = 8000;
    private const int PerFrame = Rate * SubtitleAlign.FrameMs / 1000;
    /// <summary>The background is looked for this far on each side (2.5 s).</summary>
    private const int Around = 2500 / SubtitleAlign.FrameMs;
    /// <summary>How far above it speech rises: 5 dB (log10 of the energy).</summary>
    private const float Rise = 0.6f;
    private readonly List<float> _energy = [];
    private double _sum;
    private int _count;

    public void Add(ReadOnlySpan<short> samples)
    {
        foreach (var s in samples)
        {
            _sum += (double)s * s;
            if (++_count == PerFrame) { _energy.Add((float)Math.Log10(_sum / PerFrame + 1)); _sum = 0; _count = 0; }
        }
    }

    /// <summary>The same from raw little-endian bytes (what ffmpeg writes with -f s16le); an odd byte waits for the next piece.</summary>
    public void Add(ReadOnlySpan<byte> bytes) => Add(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes[..(bytes.Length & ~1)]));

    public int Frames => _energy.Count;

    /// <summary>One value per frame, 1 for speech.</summary>
    public float[] Speech()
    {
        var energy = _energy.ToArray();
        var result = new float[energy.Length];
        if (energy.Length == 0) return result;
        // Smoothed over three frames (60 ms), then the background: the lowest within Around frames on each side.
        var smooth = new float[energy.Length];
        for (var i = 0; i < energy.Length; i++)
            smooth[i] = (energy[Math.Max(0, i - 1)] + energy[i] + energy[Math.Min(energy.Length - 1, i + 1)]) / 3;
        var floor = RollingMinimum(smooth, Around);
        for (var i = 0; i < energy.Length; i++) result[i] = smooth[i] - floor[i] >= Rise && smooth[i] > 1 ? 1 : 0;
        Close(result, 300 / SubtitleAlign.FrameMs, 1);
        Close(result, 100 / SubtitleAlign.FrameMs, 0);
        return result;
    }

    /// <summary>The lowest value within <paramref name="radius"/> on each side of each one (a monotonic queue: one pass).</summary>
    public static float[] RollingMinimum(float[] values, int radius)
    {
        var result = new float[values.Length];
        var queue = new LinkedList<int>();
        for (var i = 0; i < values.Length + radius; i++)
        {
            if (i < values.Length)
            {
                while (queue.Count > 0 && values[queue.Last!.Value] >= values[i]) queue.RemoveLast();
                queue.AddLast(i);
            }
            var center = i - radius;
            if (center < 0) continue;
            while (queue.First!.Value < center - radius) queue.RemoveFirst();
            result[center] = values[queue.First.Value];
        }
        return result;
    }

    /// <summary>Runs of the other value shorter than <paramref name="frames"/>, between two of <paramref name="value"/>, become <paramref name="value"/>.</summary>
    private static void Close(float[] signal, int frames, float value)
    {
        var i = 0;
        while (i < signal.Length)
        {
            if (signal[i] == value) { i++; continue; }
            var start = i;
            while (i < signal.Length && signal[i] != value) i++;
            if (start > 0 && i < signal.Length && i - start < frames)
                for (var k = start; k < i; k++) signal[k] = value;
        }
    }
}
