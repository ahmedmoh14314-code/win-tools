using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Core.Localization;

namespace WinTools.Dialogs;

/// <summary>
/// Edit pages (ADR-0029): a PDF's pages as thumbnails, to reorder (buttons, Ctrl+arrows or dragging), turn, repeat or
/// delete, then save the whole new order, or extract only the selected pages. The answer is a <see cref="PagePlan"/>;
/// nothing is written here. Esc cancels, closing it any other way is a cancel.
/// </summary>
public partial class OrganizeWindow : Window
{
    /// <summary>One page of the new PDF as shown: which page of the source, its added turn and its picture.</summary>
    public sealed class PageItem(int page, int turn, ImageSource? image) : INotifyPropertyChanged
    {
        private int _turn = turn;

        public int Page { get; } = page;
        public ImageSource? Image { get; } = image;

        public int Turn
        {
            get => _turn;
            set
            {
                _turn = value;
                PropertyChanged?.Invoke(this, new(nameof(Turn)));
                PropertyChanged?.Invoke(this, new(nameof(Caption)));
            }
        }

        public string Caption => Turn == 0 ? UiText.F("organize.page", Page) : UiText.F("organize.pageTurned", Page, Turn);

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly OrganizeRequest _request;
    private readonly ObservableCollection<PageItem> _items = [];
    private Point? _dragFrom;

    /// <summary>The plan in its one spelling, or null when cancelled.</summary>
    public string? Answer { get; private set; }

    public OrganizeWindow(OrganizeRequest request)
    {
        _request = request;
        InitializeComponent();
        Title = request.Title;
        Heading.Text = request.Title;
        for (var i = 0; i < request.Pages; i++) _items.Add(new PageItem(i + 1, 0, Thumbnail(i)));
        Pages.ItemsSource = _items;
        Loaded += (_, _) =>
        {
            WindowPlacement.NearPoint(this, _request.X, _request.Y);
            Activate();
            Pages.Focus();
            if (_items.Count > 0) Pages.SelectedIndex = 0;
            Refresh();
        };
    }

    /// <summary>A page's picture from the host's folder, read whole so no file stays open; a missing one shows an empty box.</summary>
    private BitmapImage? Thumbnail(int index)
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

    private List<PageItem> Selected() => [.. _items.Where(Pages.SelectedItems.Contains)];

    private void Refresh()
    {
        var selected = Pages.SelectedItems.Count;
        foreach (var b in new[] { TurnLeft, TurnRight, Repeat, Remove, Earlier, Later }) b.IsEnabled = selected > 0;
        Remove.IsEnabled = selected > 0 && selected < _items.Count; // at least one page stays
        Repeat.IsEnabled = selected > 0 && _items.Count + selected <= PagePlan.MaxEntries;
        Extract.IsEnabled = selected > 0;
        Save.IsEnabled = _items.Count > 0 && !Plan(extract: false).KeepsEverything(_request.Pages);
        Status.Text = UiText.F("organize.status", _items.Count, selected);
    }

    private PagePlan Plan(bool extract) =>
        new(extract, [.. (extract ? Selected() : [.. _items]).Select(i => new PlannedPage(i.Page, i.Turn))]);

    private void Finish(bool extract)
    {
        var plan = Plan(extract);
        if (plan.Pages.Count == 0 || PagePlan.Read(plan.Format(), _request.Pages) is null) return;
        Answer = plan.Format();
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e) => Finish(extract: false);
    private void OnExtract(object sender, RoutedEventArgs e) => Finish(extract: true);
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => Refresh();
    private void OnTurnLeft(object sender, RoutedEventArgs e) => Turn(270);
    private void OnTurnRight(object sender, RoutedEventArgs e) => Turn(90);
    private void OnEarlier(object sender, RoutedEventArgs e) => Move(-1);
    private void OnLater(object sender, RoutedEventArgs e) => Move(+1);

    private void Turn(int by)
    {
        foreach (var item in Selected()) item.Turn = (item.Turn + by) % 360;
        Refresh();
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        if (selected.Count == 0 || _items.Count + selected.Count > PagePlan.MaxEntries) return;
        // Each copy right after the last selected page, in order, and the copies become the selection.
        var at = _items.IndexOf(selected[^1]) + 1;
        var copies = selected.Select(s => new PageItem(s.Page, s.Turn, s.Image)).ToList();
        for (var i = 0; i < copies.Count; i++) _items.Insert(at + i, copies[i]);
        Reselect(copies);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        if (selected.Count == 0 || selected.Count >= _items.Count) return;
        var at = _items.IndexOf(selected[0]);
        foreach (var item in selected) _items.Remove(item);
        Pages.SelectedIndex = Math.Min(at, _items.Count - 1);
        Refresh();
    }

    /// <summary>Moves the selected pages one place earlier or later, keeping their order and the selection.</summary>
    private void Move(int by)
    {
        var selected = Selected();
        if (selected.Count == 0) return;
        var indexes = selected.Select(_items.IndexOf).Order().ToList();
        if (by < 0 && indexes[0] == 0 || by > 0 && indexes[^1] == _items.Count - 1) return;
        foreach (var i in by < 0 ? indexes : Enumerable.Reverse(indexes)) _items.Move(i, i + by);
        Reselect(selected);
    }

    private void Reselect(IReadOnlyList<PageItem> items)
    {
        Pages.SelectedItems.Clear();
        foreach (var item in items) Pages.SelectedItems.Add(item);
        Pages.ScrollIntoView(items[^1]);
        Refresh();
    }

    private void OnPagesKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        // "Earlier" is the reading direction's start: left in English, right in Arabic.
        var startKey = FlowDirection == FlowDirection.RightToLeft ? Key.Right : Key.Left;
        var endKey = FlowDirection == FlowDirection.RightToLeft ? Key.Left : Key.Right;
        if (e.Key == Key.Delete) OnDelete(sender, e);
        else if (ctrl && e.Key == startKey) Move(-1);
        else if (ctrl && e.Key == endKey) Move(+1);
        else if (ctrl && e.Key == Key.R) Turn((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 270 : 90);
        else if (ctrl && e.Key == Key.D) OnDuplicate(sender, e);
        else return;
        e.Handled = true;
    }

    // ---- Dragging a page to another place ----

    private void OnPagesMouseDown(object sender, MouseButtonEventArgs e) => _dragFrom = e.GetPosition(Pages);

    private void OnPagesMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragFrom is not { } from) return;
        var now = e.GetPosition(Pages);
        if (Math.Abs(now.X - from.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - from.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragFrom = null;
        if (ItemAt(from) is not { } item) return;
        if (!Pages.SelectedItems.Contains(item)) Pages.SelectedItem = item;
        DragDrop.DoDragDrop(Pages, new DataObject(typeof(PageItem), item), DragDropEffects.Move);
    }

    private void OnPagesDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(PageItem)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPagesDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(PageItem))) return;
        var moving = Selected();
        var target = ItemAt(e.GetPosition(Pages));
        if (moving.Count == 0 || target is not null && moving.Contains(target)) return;
        var anchor = target is null ? _items.Count : _items.IndexOf(target);
        var before = moving.Count(m => _items.IndexOf(m) < anchor);
        foreach (var m in moving) _items.Remove(m);
        var at = Math.Clamp(anchor - before, 0, _items.Count);
        for (var i = 0; i < moving.Count; i++) _items.Insert(at + i, moving[i]);
        Reselect(moving);
        e.Handled = true;
    }

    private PageItem? ItemAt(Point point) =>
        Pages.InputHitTest(point) is DependencyObject hit && ItemsControl.ContainerFromElement(Pages, hit) is ListBoxItem container ? container.DataContext as PageItem : null;
}
