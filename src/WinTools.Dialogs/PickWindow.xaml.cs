using System.Windows;
using System.Windows.Controls;
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
            WindowPlacement.NearPoint(this, _request.X, _request.Y);
            Activate();
            Items.Focus();
            (Items.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void Pick()
    {
        if (Items.SelectedItem is PickItem item) Chosen = item.Id;
        Close();
    }

    /// <summary>Only a double-click on an item runs it; one on the scroll bar or empty space does nothing (evaluator, PR #17).</summary>
    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject hit && ItemsControl.ContainerFromElement(Items, hit) is ListBoxItem) Pick();
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Pick();
    }
}
