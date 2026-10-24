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
            Place();
            Activate();
            Items.Focus();
            (Items.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>
    /// Near the drop point, kept inside the work area of the monitor that holds it. Everything is in physical pixels
    /// (the host's coordinates), so the monitor's own DPI and position need no conversion: WPF rescales the window when
    /// it lands on a monitor of another DPI, and the second move clamps its final size (evaluator, PR #17: it used the
    /// primary monitor's work area and the DPI of wherever the window first appeared).
    /// </summary>
    private void Place()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var monitor = Native.MonitorFromPoint(new Native.POINT(_request.X, _request.Y), Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (hwnd == 0 || !Native.GetMonitorInfoW(monitor, ref info)) return;
        Native.SetWindowPos(hwnd, 0, _request.X, _request.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        if (!Native.GetWindowRect(hwnd, out var r)) return;
        var work = info.rcWork;
        var x = Math.Clamp(_request.X, work.Left, Math.Max(work.Left, work.Right - (r.Right - r.Left)));
        var y = Math.Clamp(_request.Y, work.Top, Math.Max(work.Top, work.Bottom - (r.Bottom - r.Top)));
        Native.SetWindowPos(hwnd, 0, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
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
