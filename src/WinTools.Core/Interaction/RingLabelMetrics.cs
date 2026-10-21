namespace WinTools.Core.Interaction;

/// <summary>
/// The visual layout of the ring that the label layout depends on, in pixels at a given DPI: the band the sectors are
/// drawn in (inside the hit-tested ring of <see cref="RingGeometry"/>), where labels sit and how much room they get.
/// The categories' ring shows an icon above each label, so its boxes are taller.
/// </summary>
public readonly record struct RingLabelMetrics(double Radius, double HalfHeight, double MaxHalfWidth, double Inner, double Outer, double Gap, double MinUsefulHalfWidth)
{
    /// <summary>Inner and outer edge of the drawn sectors at 96 DPI; the hub is drawn inside, the window edge outside.</summary>
    public const double BandInner96 = 34, BandOuter96 = 103;
    /// <summary>Radius of the drawn hub at 96 DPI (the dead zone is <see cref="RingGeometry.InnerRadius96"/>).</summary>
    public const double Hub96 = 29;
    /// <summary>Text sizes at 96 DPI: the labels and the small text in the hub.</summary>
    public const double LabelText96 = 13, SmallText96 = 11, LineHeight96 = 15;
    /// <summary>Icon size at 96 DPI and the space between an icon and its label.</summary>
    public const double Icon96 = 16, IconGap96 = 3;

    public static RingLabelMetrics For(int count, uint dpi, bool withIcons)
    {
        var s = dpi / 96.0;
        var halfHeight = withIcons ? (Icon96 + IconGap96 + LineHeight96 + 2) / 2 : LineHeight96; // icon + one line, or two lines
        var radius = withIcons ? 66 : count > 6 ? 71 : 70;
        // On the categories' ring the box's corner nearest the hub is empty (the icon is centred above the text), so the
        // box may reach 4 px into the gap before the hub; the text itself stays on the sector.
        var inner = withIcons ? BandInner96 - 4 : BandInner96 + 2;
        return new(radius * s, halfHeight * s, 40 * s, inner * s, BandOuter96 * s, 3 * s, 16 * s);
    }
}
