namespace WinTools.Worker.Imaging;

/// <summary>
/// How far a re-encoded image is from the pixels it was made from, on several axes, because no single number catches
/// every visible loss (ADR-0017):
/// <list type="bullet">
/// <item><see cref="LumaSsim"/>: structural similarity of brightness (8×8 windows every 4 px, the usual constants).</item>
/// <item><see cref="WorstLumaSsim"/>: the 1st percentile of those windows, so damage concentrated in one region (text,
/// a face, a sharp edge) is not averaged away by a large clean background.</item>
/// <item><see cref="ChromaSsim"/>: the same on the two colour-difference planes (Cb, Cr): lost colour resolution and
/// colour blocking, which brightness SSIM does not see.</item>
/// <item><see cref="MeanDeltaE"/> and <see cref="DeltaE99"/>: CIE76 colour difference in CIELAB (sRGB, D65) on every
/// second pixel of every row, the columns alternating from row to row, so a line one pixel thin in either direction is
/// sampled: shifted colours, in perceptual units (about 2.3 is just noticeable).</item>
/// <item><see cref="EdgeSsim"/>: structural similarity of the brightness gradient (Sobel magnitude): ringing,
/// blurring and blocking at edges.</item>
/// <item>The worst 32×32 tile, on four measures: its mean luma SSIM (<see cref="WorstTileLumaSsim"/>), its mean chroma
/// SSIM (<see cref="WorstTileChromaSsim"/>), its mean ΔE (<see cref="WorstTileDeltaE"/>), and the share of its samples
/// whose ΔE exceeds 4, 6 or 10 (<see cref="WorstTileOver4"/>, <see cref="WorstTileOver6"/>, <see cref="WorstTileOver10"/>).
/// A percentile over the whole image cannot see damage confined to less than 1 % of it, and a mean over a large tile
/// dilutes a word 32×14 px across (evaluator, PR #12). Tiles start every 16 px, so damage 16 px or more across lies
/// almost wholly inside one; the share of badly wrong samples does not dilute with the clean pixels around them.</item>
/// </list>
/// Transparent pixels are compared as composited onto white, which is how the user sees them on a light background.
/// </summary>
internal readonly record struct FidelityReport(double LumaSsim, double WorstLumaSsim, double ChromaSsim, double MeanDeltaE, double DeltaE99, double EdgeSsim,
    double WorstTileLumaSsim, double WorstTileDeltaE, double WorstTileChromaSsim = 1, double WorstTileOver4 = 0, double WorstTileOver6 = 0, double WorstTileOver10 = 0);

/// <summary>
/// Computes a <see cref="FidelityReport"/> streaming over rows of 4×4 blocks, in parallel bands. Its own working memory is
/// a few rows, a histogram and the 16×16 cell sums per band (a few tens of MB at 48 MP); it builds no plane of its own. The two
/// images it compares are, of course, whole: the source and the decoded candidate are each a full BGRA buffer.
/// </summary>
internal static class Fidelity
{
    private const int Block = 4;   // windows are 2×2 blocks (8×8 px) placed every block (4 px)
    private const int Planes = 4;  // Y, Cb, Cr, gradient
    private const int Sums = 5;    // Σa, Σb, Σa², Σb², Σab
    private const double C1 = 0.01 * 255 * 0.01 * 255, C2 = 0.03 * 255 * 0.03 * 255;

    public static FidelityReport Compare(RasterImage a, RasterImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height) throw new ArgumentException("Images differ in size.");
        var (bw, bh) = (a.Width / Block, a.Height / Block);
        var cells = new Cells(a.Width, a.Height);
        var de = DeltaE(a, b, cells);
        if (bw < 2 || bh < 2) return Thin(a, b, de);

        // Window rows 1..bh-1 (each uses block rows wy-1 and wy), split into bands that run in parallel and are merged.
        var bands = Math.Clamp(Math.Min(Environment.ProcessorCount, (bh - 1) / 16), 1, 8);
        var parts = new Band[bands];
        Parallel.For(0, bands, k => parts[k] = Band.Run(a, b, cells, 1 + (bh - 1) * k / bands, 1 + (bh - 1) * (k + 1) / bands));
        var all = parts[0];
        for (var k = 1; k < bands; k++) all.Merge(parts[k]);
        return new FidelityReport(all.Luma / all.Windows, all.Worst.Percentile(0.01), all.Chroma / all.Windows, de.Mean, de.P99, all.Edge / all.Windows,
            cells.WorstTile(all.TileLuma, all.TileWindows, lowest: true), de.WorstTile,
            cells.WorstTile(all.TileChroma, all.TileWindows, lowest: true), de.Over4, de.Over6, de.Over10);
    }

    /// <summary>
    /// The image cut into 16×16 cells (the last row and column take the remainder, under 16 px); a tile is 2×2 cells, so
    /// tiles are 32×32 and start every 16 px. Sums are kept per cell: about 190,000 cells at 48 MP.
    /// </summary>
    private sealed class Cells(int width, int height)
    {
        private const int Size = 16;
        public int X { get; } = Math.Max(1, width / Size);
        public int Y { get; } = Math.Max(1, height / Size);
        public int Count => X * Y;

        public int Of(int x, int y) => Math.Min(y / Size, Y - 1) * X + Math.Min(x / Size, X - 1);

        /// <summary>The lowest (or highest) tile mean, a tile being cells (cx..cx+1, cy..cy+1).</summary>
        public double WorstTile(double[] sums, long[] counts, bool lowest) => WorstTile(sums, counts, lowest, sum => sum);

        /// <summary>The worst tile share of <paramref name="hits"/> among <paramref name="counts"/>.</summary>
        public double WorstShare(long[] hits, long[] counts) => WorstTile(hits, counts, lowest: false, hit => hit);

        private double WorstTile<T>(T[] sums, long[] counts, bool lowest, Func<T, double> value)
        {
            var worst = lowest ? double.MaxValue : double.MinValue;
            for (var ty = 0; ty < Math.Max(1, Y - 1); ty++)
                for (var tx = 0; tx < Math.Max(1, X - 1); tx++)
                {
                    double s = 0; long n = 0;
                    for (var cy = ty; cy <= Math.Min(ty + 1, Y - 1); cy++)
                        for (var cx = tx; cx <= Math.Min(tx + 1, X - 1); cx++) { s += value(sums[cy * X + cx]); n += counts[cy * X + cx]; }
                    if (n == 0) continue;
                    worst = lowest ? Math.Min(worst, s / n) : Math.Max(worst, s / n);
                }
            return worst is double.MaxValue or double.MinValue ? (lowest ? 1 : 0) : worst;
        }
    }

    /// <summary>The windows of rows [first, end) of the window grid, from the block rows they need.</summary>
    private sealed class Band(Cells cells)
    {
        public double Luma, Chroma, Edge;
        public long Windows;
        public Histogram Worst { get; } = new(-1, 1, 4000);
        public double[] TileLuma { get; } = new double[cells.Count];
        public double[] TileChroma { get; } = new double[cells.Count];
        public long[] TileWindows { get; } = new long[cells.Count];

        public static Band Run(RasterImage a, RasterImage b, Cells cells, int first, int end)
        {
            var band = new Band(cells);
            var w = a.Width;
            var bw = w / Block;
            var lumaA = new LumaRows(a);
            var lumaB = new LumaRows(b);
            var previous = new double[bw * Planes * Sums];
            var current = new double[bw * Planes * Sums];
            Span<double> win = stackalloc double[Sums];
            for (var by = first - 1; by < end; by++)
            {
                Array.Clear(current);
                for (var y = by * Block; y < by * Block + Block; y++)
                    for (var x = 0; x < bw * Block; x++)
                    {
                        var i = (y * w + x) * 4;
                        var (ya, cba, cra) = Ycc(a.Bgra, i);
                        var (yb, cbb, crb) = Ycc(b.Bgra, i);
                        var s = (x / Block) * Planes * Sums;
                        Add(current, s, ya, yb);
                        Add(current, s + Sums, cba, cbb);
                        Add(current, s + 2 * Sums, cra, crb);
                        Add(current, s + 3 * Sums, lumaA.Gradient(x, y), lumaB.Gradient(x, y));
                    }
                if (by >= first)
                    for (var bx = 0; bx < bw - 1; bx++)
                    {
                        var l = Window(previous, current, bx, 0, win);
                        band.Luma += l;
                        band.Worst.Add(l);
                        var cell = cells.Of(bx * Block, (by - 1) * Block);
                        var chroma = (Window(previous, current, bx, 1, win) + Window(previous, current, bx, 2, win)) / 2;
                        band.TileLuma[cell] += l;
                        band.TileChroma[cell] += chroma;
                        band.TileWindows[cell]++;
                        band.Chroma += chroma;
                        band.Edge += Window(previous, current, bx, 3, win);
                        band.Windows++;
                    }
                (previous, current) = (current, previous);
            }
            return band;
        }

        public void Merge(Band other)
        {
            (Luma, Chroma, Edge, Windows) = (Luma + other.Luma, Chroma + other.Chroma, Edge + other.Edge, Windows + other.Windows);
            Worst.Merge(other.Worst);
            for (var c = 0; c < TileLuma.Length; c++) { TileLuma[c] += other.TileLuma[c]; TileChroma[c] += other.TileChroma[c]; TileWindows[c] += other.TileWindows[c]; }
        }
    }

    private static void Add(double[] sums, int at, double p, double q)
    {
        sums[at] += p; sums[at + 1] += q; sums[at + 2] += p * p; sums[at + 3] += q * q; sums[at + 4] += p * q;
    }

    /// <summary>SSIM of the 8×8 window made of blocks bx and bx+1 of the previous and current block rows, for one plane.</summary>
    private static double Window(double[] previous, double[] current, int bx, int plane, Span<double> win)
    {
        int a = (bx * Planes + plane) * Sums, b = ((bx + 1) * Planes + plane) * Sums;
        for (var k = 0; k < Sums; k++) win[k] = previous[a + k] + previous[b + k] + current[a + k] + current[b + k];
        return Ssim(win, 4 * Block * Block);
    }

    private static double Ssim(ReadOnlySpan<double> s, int n)
    {
        double ma = s[0] / n, mb = s[1] / n;
        double va = Math.Max(0, s[2] / n - ma * ma), vb = Math.Max(0, s[3] / n - mb * mb), cov = s[4] / n - ma * mb;
        return (2 * ma * mb + C1) * (2 * cov + C2) / ((ma * ma + mb * mb + C1) * (va + vb + C2));
    }

    /// <summary>
    /// Images under 8 px on a side, where no 8×8 window fits: windows of the full short side by 8 px of the long side
    /// (or the whole image when both sides are short), every 4 px along it, so a damaged stretch of a long thin strip is
    /// not averaged away. The gradient is left out (edge = luma); the worst window stands in for the worst tile.
    /// </summary>
    private static FidelityReport Thin(RasterImage a, RasterImage b, DeltaEReport de)
    {
        var alongX = a.Width >= a.Height;
        var (length, across) = alongX ? (a.Width, a.Height) : (a.Height, a.Width);
        var span = Math.Min(8, length);
        var sums = new double[3 * Sums];
        double luma = 0, chroma = 0, worst = double.MaxValue, worstChroma = double.MaxValue;
        var windows = 0;
        for (var start = 0; start + span <= length; start += 4)
        {
            Array.Clear(sums);
            for (var t = start; t < start + span; t++)
                for (var u = 0; u < across; u++)
                {
                    var i = (alongX ? u * a.Width + t : t * a.Width + u) * 4;
                    var (ya, cba, cra) = Ycc(a.Bgra, i);
                    var (yb, cbb, crb) = Ycc(b.Bgra, i);
                    Add(sums, 0, ya, yb); Add(sums, Sums, cba, cbb); Add(sums, 2 * Sums, cra, crb);
                }
            var n = span * across;
            var l = Ssim(sums.AsSpan(0, Sums), n);
            luma += l;
            worst = Math.Min(worst, l);
            var c = (Ssim(sums.AsSpan(Sums, Sums), n) + Ssim(sums.AsSpan(2 * Sums, Sums), n)) / 2;
            chroma += c;
            worstChroma = Math.Min(worstChroma, c);
            windows++;
            if (span == length) break;
        }
        return new FidelityReport(luma / windows, worst, chroma / windows, de.Mean, de.P99, luma / windows, worst, de.WorstTile,
            worstChroma, de.Over4, de.Over6, de.Over10);
    }

    /// <summary>Y, Cb, Cr (BT.601 full range, as JPEG uses) of one pixel composited onto white.</summary>
    private static (float Y, float Cb, float Cr) Ycc(byte[] px, int i)
    {
        var (b, g, r) = OnWhite(px, i);
        return (0.299f * r + 0.587f * g + 0.114f * b,
                128 - 0.168736f * r - 0.331264f * g + 0.5f * b,
                128 + 0.5f * r - 0.418688f * g - 0.081312f * b);
    }

    private static (float B, float G, float R) OnWhite(byte[] px, int i)
    {
        if (px[i + 3] == 255) return (px[i], px[i + 1], px[i + 2]);
        var a = px[i + 3] / 255f;
        return (px[i] * a + 255 * (1 - a), px[i + 1] * a + 255 * (1 - a), px[i + 2] * a + 255 * (1 - a));
    }

    /// <summary>Brightness rows computed on demand, the last few kept, for the Sobel gradient.</summary>
    private sealed class LumaRows(RasterImage image)
    {
        private const int Kept = 8;
        private readonly float[][] _rows = [.. Enumerable.Range(0, Kept).Select(_ => new float[image.Width])];
        private readonly int[] _tags = [.. Enumerable.Repeat(-1, Kept)];

        private float[] Row(int y)
        {
            var slot = y % Kept;
            if (_tags[slot] == y) return _rows[slot];
            var row = _rows[slot];
            for (var x = 0; x < image.Width; x++) row[x] = Ycc(image.Bgra, (y * image.Width + x) * 4).Y;
            _tags[slot] = y;
            return row;
        }

        /// <summary>Sobel magnitude at (x, y), scaled to the 0–255 range the constants assume; 0 on the border.</summary>
        public float Gradient(int x, int y)
        {
            if (x == 0 || y == 0 || x == image.Width - 1 || y == image.Height - 1) return 0;
            float[] up = Row(y - 1), mid = Row(y), down = Row(y + 1);
            var gx = up[x + 1] + 2 * mid[x + 1] + down[x + 1] - up[x - 1] - 2 * mid[x - 1] - down[x - 1];
            var gy = down[x - 1] + 2 * down[x] + down[x + 1] - up[x - 1] - 2 * up[x] - up[x + 1];
            return MathF.Sqrt(gx * gx + gy * gy) / 4;
        }
    }

    private readonly record struct DeltaEReport(double Mean, double P99, double WorstTile, double Over4, double Over6, double Over10);

    /// <summary>
    /// CIE76 ΔE on every second pixel of every row, starting one pixel later on odd rows: its mean, 99th percentile, the
    /// worst tile's mean, and the worst tile's share of samples above 4, 6 and 10.
    /// </summary>
    private static DeltaEReport DeltaE(RasterImage a, RasterImage b, Cells cells)
    {
        var bands = Math.Clamp(Math.Min(Environment.ProcessorCount, a.Height / 64), 1, 8);
        var histograms = new Histogram[bands];
        var sums = new double[bands];
        var cellSums = new double[bands][];
        var cellCounts = new long[bands][];
        var over = new long[bands][][];
        Parallel.For(0, bands, k =>
        {
            var histogram = histograms[k] = new Histogram(0, 200, 4000);
            var cs = cellSums[k] = new double[cells.Count];
            var cn = cellCounts[k] = new long[cells.Count];
            var o = over[k] = [new long[cells.Count], new long[cells.Count], new long[cells.Count]];
            for (var y = a.Height * k / bands; y < a.Height * (k + 1) / bands; y++)
                for (var x = y & 1; x < a.Width; x += 2)
                {
                    var i = (y * a.Width + x) * 4;
                    var (l1, a1, b1) = Lab(a.Bgra, i);
                    var (l2, a2, b2) = Lab(b.Bgra, i);
                    var de = MathF.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
                    sums[k] += de;
                    histogram.Add(de);
                    var c = cells.Of(x, y);
                    cs[c] += de;
                    cn[c]++;
                    if (de > 4) o[0][c]++;
                    if (de > 6) o[1][c]++;
                    if (de > 10) o[2][c]++;
                }
        });
        for (var k = 1; k < bands; k++)
        {
            histograms[0].Merge(histograms[k]);
            for (var c = 0; c < cells.Count; c++)
            {
                cellSums[0][c] += cellSums[k][c];
                cellCounts[0][c] += cellCounts[k][c];
                for (var t = 0; t < 3; t++) over[0][t][c] += over[k][t][c];
            }
        }
        if (histograms[0].Total == 0) return new DeltaEReport(0, 0, 0, 0, 0, 0);
        return new DeltaEReport(sums.Sum() / histograms[0].Total, histograms[0].Percentile(0.99, upper: true), cells.WorstTile(cellSums[0], cellCounts[0], lowest: false),
            cells.WorstShare(over[0][0], cellCounts[0]), cells.WorstShare(over[0][1], cellCounts[0]), cells.WorstShare(over[0][2], cellCounts[0]));
    }

    private static readonly float[] Linear = [.. Enumerable.Range(0, 256).Select(v =>
    {
        var c = v / 255.0;
        return (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
    })];

    private static (float L, float A, float B) Lab(byte[] px, int i)
    {
        var (b8, g8, r8) = OnWhite(px, i);
        float r = Lin(r8), g = Lin(g8), b = Lin(b8);
        var x = (0.4124f * r + 0.3576f * g + 0.1805f * b) / 0.95047f;
        var y = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        var z = (0.0193f * r + 0.1192f * g + 0.9505f * b) / 1.08883f;
        float fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));

        static float F(float t) => t > 216f / 24389 ? MathF.Cbrt(t) : (24389f / 27 * t + 16) / 116;
    }

    private static float Lin(float v)
    {
        var i = (int)v;
        if (i >= 255) return Linear[255];
        return Linear[i] + (Linear[i + 1] - Linear[i]) * (v - i);
    }

    /// <summary>A fixed-bin histogram for percentiles in bounded memory.</summary>
    private sealed class Histogram(double min, double max, int bins)
    {
        private readonly long[] _counts = new long[bins];
        private long _total;

        public long Total => _total;

        public void Merge(Histogram other)
        {
            for (var k = 0; k < bins; k++) _counts[k] += other._counts[k];
            _total += other._total;
        }

        public void Add(double value)
        {
            var k = (int)((value - min) / (max - min) * bins);
            _counts[Math.Clamp(k, 0, bins - 1)]++;
            _total++;
        }

        /// <summary>The value at <paramref name="p"/>: the bin's lower edge, or its upper edge when <paramref name="upper"/>
        /// (a conservative ceiling for a "no more than" gate).</summary>
        public double Percentile(double p, bool upper = false)
        {
            var target = Math.Max(1, (long)Math.Ceiling(_total * p));
            long seen = 0;
            for (var k = 0; k < bins; k++)
                if ((seen += _counts[k]) >= target) return min + (k + (upper ? 1 : 0)) * (max - min) / bins;
            return max;
        }
    }
}
