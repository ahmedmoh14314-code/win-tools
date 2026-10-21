namespace WinTools.Windows.Rendering;

/// <summary>A colour as 0xAARRGGBB (straight alpha).</summary>
internal readonly record struct Argb(uint Value)
{
    public static Argb Rgb(uint rgb) => new(0xFF000000 | rgb);

    public byte A => (byte)(Value >> 24);
    public byte R => (byte)(Value >> 16);
    public byte G => (byte)(Value >> 8);
    public byte B => (byte)Value;

    public Argb WithAlpha(double a) => new((uint)Math.Round(Math.Clamp(a, 0, 1) * 255) << 24 | (Value & 0xFFFFFF));

    public static Argb Lerp(Argb from, Argb to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        static uint L(byte a, byte b, double t) => (uint)Math.Round(a + (b - a) * t);
        return new(L(from.A, to.A, t) << 24 | L(from.R, to.R, t) << 16 | L(from.G, to.G, t) << 8 | L(from.B, to.B, t));
    }

    /// <summary>The colour as GDI's COLORREF (0x00BBGGRR), alpha dropped.</summary>
    public uint ColorRef => (uint)(R | G << 8 | B << 16);

    /// <summary>WCAG relative luminance.</summary>
    public double Luminance()
    {
        static double C(byte v) { var c = v / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        return 0.2126 * C(R) + 0.7152 * C(G) + 0.0722 * C(B);
    }

    /// <summary>WCAG contrast ratio between two opaque colours (1 to 21).</summary>
    public static double Contrast(Argb a, Argb b)
    {
        var (l1, l2) = (a.Luminance(), b.Luminance());
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}

/// <summary>
/// The product's dark palette: neutral green-tinted surfaces with emerald as the accent. Green marks only selection,
/// the armed command, progress, success and the product mark; failures and warnings use restrained red and amber.
/// </summary>
internal static class Palette
{
    public static readonly Argb Base = Argb.Rgb(0x111714);        // overlay base, window background
    public static readonly Argb Surface = Argb.Rgb(0x1D2621);     // ring sectors, cards
    public static readonly Argb Raised = Argb.Rgb(0x263129);      // hovered sector, secondary surface
    public static readonly Argb Sunken = Argb.Rgb(0x161D19);      // Back, the hub
    public static readonly Argb Edge = Argb.Rgb(0x2C3832);        // hairlines and rims
    public static readonly Argb Green = Argb.Rgb(0x22C55E);       // primary accent
    public static readonly Argb GreenHover = Argb.Rgb(0x2DDC78);  // hover accent
    public static readonly Argb GreenArmed = Argb.Rgb(0x4ADE80);  // armed / ready to drop
    public static readonly Argb Text = Argb.Rgb(0xF4F7F5);
    public static readonly Argb TextSecondary = Argb.Rgb(0x9AA69F);
    public static readonly Argb TextOnGreen = Argb.Rgb(0x07140C);
    public static readonly Argb Error = Argb.Rgb(0xF07171);
    public static readonly Argb Warning = Argb.Rgb(0xE8B04B);
    public static readonly Argb Shadow = Argb.Rgb(0x000000);
}
