using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WinTools.Core.Localization;

namespace WinTools.Settings;

/// <summary>
/// The four steps of the WinTools gesture, drawn: drag a file, press the trigger, choose on the wheel, drop. On Home they
/// are a compact row; in How to use (<see cref="Detailed"/>) each carries its explanation. The trigger key is shown by
/// its name, as the user set it.
/// </summary>
public sealed class FlowSteps : UserControl
{
    private readonly UniformGrid _row = new() { Rows = 1 };
    private string _trigger = "Space";

    public FlowSteps()
    {
        Content = _row;
        Loaded += (_, _) => Build();
    }

    /// <summary>Each step with its explanation (How to use) rather than its title alone (Home).</summary>
    public bool Detailed { get; set; }

    public string Trigger
    {
        get => _trigger;
        set { _trigger = value; if (IsLoaded) Build(); }
    }

    private void Build()
    {
        _row.Children.Clear();
        _row.Rows = Detailed ? 4 : 1;
        _row.Columns = Detailed ? 1 : 4;
        var steps = new (string Title, string Text, Func<FrameworkElement> Picture)[]
        {
            (UiText.T("howto.step1"), UiText.T("howto.step1.text"), DragPicture),
            (UiText.F("howto.step2", _trigger), UiText.T("howto.step2.text"), KeyPicture),
            (UiText.T("howto.step3"), UiText.T("howto.step3.text"), WheelPicture),
            (UiText.T("howto.step4"), UiText.T("howto.step4.text"), DropPicture),
        };
        for (var i = 0; i < steps.Length; i++)
        {
            var (title, text, picture) = steps[i];
            var card = new Border
            {
                Style = (Style)FindResource("Card"),
                Padding = new Thickness(16, 14, 16, 14),
                Margin = Detailed ? new Thickness(0, 0, 0, 10) : new Thickness(0, 0, i < 3 ? 10 : 0, 0),
            };
            var number = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = (Brush)FindResource("Green"),
                Child = new TextBlock { Text = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Foreground = (Brush)FindResource("TextOnGreen"),
                    FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var heading = new StackPanel { Orientation = Orientation.Horizontal };
            heading.Children.Add(number);
            heading.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            FrameworkElement body;
            if (Detailed)
            {
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                var pic = picture();
                grid.Children.Add(pic);
                var words = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                words.Children.Add(heading);
                words.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("Hint"), FontSize = 13, Margin = new Thickness(34, 6, 0, 0) });
                Grid.SetColumn(words, 1);
                grid.Children.Add(words);
                body = grid;
            }
            else
            {
                var stack = new StackPanel();
                var pic = picture();
                pic.Margin = new Thickness(0, 0, 0, 10);
                stack.Children.Add(pic);
                stack.Children.Add(heading);
                body = stack;
            }
            card.Child = body;
            _row.Children.Add(card);
        }
    }

    private Brush B(string key) => (Brush)FindResource(key);

    // Drawings keep their direction in Arabic: a mirrored pointer or check mark reads wrong.
    private static Canvas Frame() => new() { Width = 96, Height = 64, HorizontalAlignment = HorizontalAlignment.Left, FlowDirection = FlowDirection.LeftToRight };

    /// <summary>A file with a folded corner, a pointer dragging it.</summary>
    private Canvas DragPicture()
    {
        var c = Frame();
        var file = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 24,8 L 50,8 L 62,20 L 62,58 L 24,58 Z M 50,8 L 50,20 L 62,20"),
            Stroke = B("Text"), StrokeThickness = 2, Fill = B("Raised"), StrokeLineJoin = PenLineJoin.Round,
        };
        var pointer = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 56,34 L 56,58 L 62,52 L 67,62 L 71,60 L 66,50 L 74,50 Z"),
            Fill = B("Green"), Stroke = B("Base"), StrokeThickness = 1.5,
        };
        c.Children.Add(file);
        c.Children.Add(pointer);
        return c;
    }

    /// <summary>A key cap with the trigger's name.</summary>
    private Canvas KeyPicture()
    {
        var key = new Border
        {
            Width = _trigger.Length > 3 ? 78 : 52, Height = 46, CornerRadius = new CornerRadius(8), Background = B("Raised"),
            BorderBrush = B("Green"), BorderThickness = new Thickness(2), Margin = new Thickness(8, 8, 0, 0),
            Child = new TextBlock { Text = _trigger, FontWeight = FontWeights.SemiBold, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var c = Frame();
        c.Children.Add(key);
        return c;
    }

    /// <summary>The wheel: a ring of sectors, one of them lit.</summary>
    private Canvas WheelPicture()
    {
        var c = Frame();
        const double cx = 40, cy = 32, outer = 30, inner = 12;
        for (var i = 0; i < 6; i++)
        {
            var a0 = (i * 60 - 90 + 3) * Math.PI / 180;
            var a1 = ((i + 1) * 60 - 90 - 3) * Math.PI / 180;
            Point P(double r, double a) => new(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
            var g = new PathGeometry([new PathFigure(P(inner, a0),
            [
                new LineSegment(P(outer, a0), true),
                new ArcSegment(P(outer, a1), new Size(outer, outer), 0, false, SweepDirection.Clockwise, true),
                new LineSegment(P(inner, a1), true),
                new ArcSegment(P(inner, a0), new Size(inner, inner), 0, false, SweepDirection.Counterclockwise, true),
            ], true)]);
            c.Children.Add(new System.Windows.Shapes.Path { Data = g, Fill = i == 1 ? B("Green") : B("Raised"), Stroke = B("Edge"), StrokeThickness = 1 });
        }
        return c;
    }

    /// <summary>A finished job: a check in a circle.</summary>
    private Canvas DropPicture()
    {
        var c = Frame();
        var ring = new Ellipse { Width = 52, Height = 52, Stroke = B("Green"), StrokeThickness = 3 };
        Canvas.SetLeft(ring, 14);
        Canvas.SetTop(ring, 6);
        c.Children.Add(ring);
        c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M 28,33 L 37,42 L 53,24"), Stroke = B("Green"), StrokeThickness = 4, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        return c;
    }
}
