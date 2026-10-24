using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WinTools.Core.Jobs;

namespace WinTools.Dialogs;

/// <summary>
/// The "More…" list: the commands that did not fit the ring. Opens next to where the files were dropped, keeps the
/// keyboard (Up, Down, Enter, Esc) and the mouse (double-click). Closing it any other way is a cancel.
/// </summary>
public partial class PickWindow : Window
{
    private readonly PickRequest _request;

    public string? Chosen { get; private set; }

    public PickWindow(PickRequest request)
    {
        _request = request;
        InitializeComponent();
        Title = request.Title;
        Heading.Text = request.Title;
        Items.ItemsSource = request.Items;
        Items.SelectedIndex = 0;
        Loaded += (_, _) =>
        {
            Place();
            Activate();
            Items.Focus();
            (Items.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>Near the drop point (physical pixels from the host), kept inside the work area of that screen.</summary>
    private void Place()
    {
        var scale = VisualTreeHelper.GetDpi(this);
        var (x, y) = (_request.X / scale.DpiScaleX, _request.Y / scale.DpiScaleY);
        var work = SystemParameters.WorkArea;
        Left = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - ActualWidth));
        Top = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - ActualHeight));
    }

    private void Pick()
    {
        if (Items.SelectedItem is PickItem item) Chosen = item.Id;
        Close();
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => Pick();

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Pick();
    }
}
