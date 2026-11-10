using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Core.Localization;

namespace WinTools.Dialogs;

/// <summary>
/// Sign: a page of the PDF to click where the signature goes, a pad to draw it on (or a box to type it), its size, and
/// whether every page gets it. The answer is a <see cref="SignPlan"/>; nothing is written here. Esc cancels.
/// </summary>
public partial class SignWindow : Window
{
    private readonly SignRequest _request;
    private int _page;
    private double _x = 0.72, _y = 0.88;

    /// <summary>The plan in its one spelling, or null when cancelled.</summary>
    public string? Answer { get; private set; }

    public SignWindow(SignRequest request)
    {
        _request = request;
        InitializeComponent();
        Title = request.Title;
        Heading.Text = request.Title;
        Pad.DefaultDrawingAttributes.Color = System.Windows.Media.Color.FromRgb(0x1A, 0x2B, 0x6D);
        Pad.DefaultDrawingAttributes.Width = Pad.DefaultDrawingAttributes.Height = 2.5;
        Pad.StrokeErased += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            WindowPlacement.NearPoint(this, _request.X, _request.Y);
            Activate();
            ShowPage();
            Pad.Focus();
        };
        PageImage.SizeChanged += (_, _) => Refresh();
    }

    private void ShowPage()
    {
        PageImage.Source = Picture(_page);
        PageLabel.Text = UiText.F("sign.page", _page + 1, _request.Pages);
        Previous.IsEnabled = _page > 0;
        Next.IsEnabled = _page < _request.Pages - 1;
        Refresh();
    }

    /// <summary>A page's picture from the host's folder, read whole so no file stays open; a missing one shows nothing.</summary>
    private BitmapImage? Picture(int index)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(Path.Combine(_request.Thumbnails, PdfRenderCapabilities.ThumbnailName(index)), UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or NotSupportedException or ArgumentException or InvalidOperationException) { return null; }
    }

    private void OnPrevious(object sender, RoutedEventArgs e) { if (_page > 0) { _page--; ShowPage(); } }
    private void OnNext(object sender, RoutedEventArgs e) { if (_page < _request.Pages - 1) { _page++; ShowPage(); } }

    private void OnPlace(object sender, MouseButtonEventArgs e)
    {
        var at = e.GetPosition(PageImage);
        if (PageImage.ActualWidth <= 0 || PageImage.ActualHeight <= 0) return;
        _x = Math.Clamp(at.X / PageImage.ActualWidth, 0, 1);
        _y = Math.Clamp(at.Y / PageImage.ActualHeight, 0, 1);
        Refresh();
    }

    private void OnClear(object sender, RoutedEventArgs e) { Pad.Strokes.Clear(); Refresh(); }
    private void OnInkChanged(object sender, InkCanvasStrokeCollectedEventArgs e) => Refresh();
    private void OnTypedChanged(object sender, TextChangedEventArgs e) => Refresh();
    private void OnSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Refresh();

    /// <summary>The signature box on the page as the plan will place it (its height from the pad's proportions or the typed line's).</summary>
    private void Refresh()
    {
        if (!IsLoaded || Mark is null) return;
        var w = Size.Value * PageImage.ActualWidth;
        var h = w * SignPlan.BoxHeight / SignPlan.BoxWidth;
        Mark.Width = Math.Max(4, w);
        Mark.Height = Math.Max(4, h);
        Canvas.SetLeft(Mark, _x * PageImage.ActualWidth - w / 2);
        Canvas.SetTop(Mark, _y * PageImage.ActualHeight - h / 2);
        Ok.IsEnabled = Plan() is not null;
    }

    /// <summary>The plan from what is on screen, or null while there is no signature yet.</summary>
    private string? Plan()
    {
        var page = AllPages.IsChecked == true ? (int?)null : _page + 1;
        var x = Math.Round(_x, 3);
        var y = Math.Round(_y, 3);
        var w = Math.Round(Size.Value, 3);
        var typed = Typed.Text.Trim();
        SignPlan plan;
        if (typed.Length > 0) plan = new SignPlan(page, x, y, w, null, typed);
        else
        {
            var strokes = Strokes();
            if (strokes.Count == 0) return null;
            plan = new SignPlan(page, x, y, w, strokes, null);
        }
        var text = plan.Format();
        return SignPlan.Read(text, _request.Pages) is not null ? text : null;
    }

    /// <summary>The pad's strokes in the plan's 1000 × 400 box, repeated points dropped, thinned to the plan's limits.</summary>
    private List<InkStroke> Strokes()
    {
        var sx = SignPlan.BoxWidth / Math.Max(1, Pad.ActualWidth);
        var sy = SignPlan.BoxHeight / Math.Max(1, Pad.ActualHeight);
        var total = Pad.Strokes.Sum(s => s.StylusPoints.Count);
        var step = Math.Max(1, (int)Math.Ceiling(total / (double)(SignPlan.MaxPoints - 2 * SignPlan.MaxStrokes)));
        var strokes = new List<InkStroke>();
        foreach (var stroke in Pad.Strokes.Take(SignPlan.MaxStrokes))
        {
            var points = new List<(int X, int Y)>();
            var all = stroke.StylusPoints;
            for (var i = 0; i < all.Count; i++)
            {
                if (i % step != 0 && i != all.Count - 1) continue;
                var p = ((int)Math.Clamp(Math.Round(all[i].X * sx), 0, SignPlan.BoxWidth), (int)Math.Clamp(Math.Round(all[i].Y * sy), 0, SignPlan.BoxHeight));
                if (points.Count == 0 || points[^1] != p) points.Add(p);
            }
            if (points.Count > 0) strokes.Add(new InkStroke(points));
        }
        return strokes;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Plan() is not { } plan) return;
        Answer = plan;
        Close();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        AllPages.Checked += (_, _) => Refresh();
        AllPages.Unchecked += (_, _) => Refresh();
    }
}
