using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using LiteHibernate.UI;
using LiteHibernate.Windows;

namespace LiteHibernate;

public partial class MainWindow : Window
{
    public bool AllowClose { get; set; }
    public MainWindow(MainViewModel model)
    {
        InitializeComponent(); DataContext = model;
        InteractionSurface.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler(OnSurfaceMouseUp), true);
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, Math.Max(600, area.Width - 24));
        MinHeight = Math.Min(MinHeight, Math.Max(500, area.Height - 24));
        Width = Math.Min(Width, Math.Max(MinWidth, area.Width - 24));
        Height = Math.Min(Height, Math.Max(MinHeight, area.Height - 24));
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(ConstrainMaximizedBounds);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var chrome = WindowChrome.GetWindowChrome(this);
        // Nonzero glass lets WindowChrome remove its custom region and hand the frame
        // to DWM. The opaque client background still covers the glass itself.
        chrome.GlassFrameThickness = new Thickness(1);
        if (SetFrameAttribute(handle, 2, 2) // DWMWA_NCRENDERING_POLICY: DWMNCRP_ENABLED
            && SetFrameAttribute(handle, 33, 2) // DWMWA_WINDOW_CORNER_PREFERENCE: DWMWCP_ROUND
            && SetFrameAttribute(handle, 34, 0x004A352A)) // DWMWA_BORDER_COLOR: RGB(42, 53, 74)
        {
            InteractionSurface.BorderThickness = new Thickness(0);
            SetFrameAttribute(handle, 20, 1); // DWMWA_USE_IMMERSIVE_DARK_MODE
            return;
        }
        // Older/unsupported compositors use one square client border, never two outlines.
        chrome.GlassFrameThickness = new Thickness(0);
        SetFrameAttribute(handle, 2, 1); // DWMNCRP_DISABLED
        SetFrameAttribute(handle, 33, 1); // DWMWCP_DONOTROUND
    }
    private static IntPtr ConstrainMaximizedBounds(IntPtr window, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        if (message != 0x0024) return IntPtr.Zero; // WM_GETMINMAXINFO
        var monitor = Native.MonitorFromWindow(window, 2); // MONITOR_DEFAULTTONEAREST
        var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
        if (monitor == IntPtr.Zero || !Native.GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var limits = Marshal.PtrToStructure<Native.MinMaxInfo>(lparam);
        // Our client content replaces the native frame. Do not include its normally
        // hidden resize borders or the taskbar in the maximized rectangle.
        // Monitor coordinates are physical pixels, including on secondary monitors.
        limits.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        limits.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        limits.MaxSize.X = info.Work.Right - info.Work.Left;
        limits.MaxSize.Y = info.Work.Bottom - info.Work.Top;
        Marshal.StructureToPtr(limits, lparam, false);
        // Let WPF continue enforcing MinWidth/MinHeight and its tracking limits.
        return IntPtr.Zero;
    }
    private static bool SetFrameAttribute(IntPtr window, uint attribute, uint value)
        => Native.DwmSetWindowAttribute(window, attribute, in value, sizeof(uint)) >= 0;
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (MaximizeCaptionButton != null) MaximizeCaptionButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        if (DataContext is MainViewModel model) model.WindowVisible = IsVisible && WindowState != WindowState.Minimized;
    }
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnChromeClose(object sender, RoutedEventArgs e) => Close();
    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // WPF reports an outside click as the capturing ComboBox itself.
        // Popup items have their own source and must finish their normal selection handling.
        if (e.OriginalSource is ComboBox { IsDropDownOpen: true } captured)
        {
            captured.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
            FocusSurfaceAfterInput();
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is Visual visual && PresentationSource.FromVisual(visual) is { } source
            && source != PresentationSource.FromVisual(InteractionSurface)) return;
        for (var target = e.OriginalSource as DependencyObject; target != null;
            target = target is Visual ? VisualTreeHelper.GetParent(target) : LogicalTreeHelper.GetParent(target))
        {
            if (target is TextBoxBase or ComboBox or ComboBoxItem or ButtonBase or ScrollBar or Thumb) return;
        }
        FocusSurfaceAfterInput();
        e.Handled = true;
    }
    private void FocusSurfaceAfterInput() => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
    {
        // ComboBox's bubbling mouse handler can reclaim focus even for a handled event.
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(InteractionSurface), InteractionSurface);
        Keyboard.Focus(InteractionSurface);
    }));
    private void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        for (var target = e.OriginalSource as DependencyObject; target != null;
            target = target is Visual ? VisualTreeHelper.GetParent(target) : LogicalTreeHelper.GetParent(target))
        {
            if (target is ComboBoxItem or CheckBox || target is Button { Name: "PinListButton" }) { FocusSurfaceAfterInput(); return; }
        }
    }
    private void OnApplicationsMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is DataGrid grid && grid.Template.FindName("DG_ScrollViewer", grid) is ScrollViewer viewer)
        {
            // Pixel scrolling preserves virtualization and handles partial wheel deltas.
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset - e.Delta / 120.0 * 32);
            e.Handled = true;
        }
    }
    private void OnApplicationsSorting(object sender, DataGridSortingEventArgs e)
    {
        if (DataContext is not MainViewModel { IsListPinned: true } model || string.IsNullOrEmpty(e.Column.SortMemberPath)) return;
        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        var append = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        model.SortPinnedApplications(e.Column.SortMemberPath, direction, append);
        if (!append)
            foreach (var column in ((DataGrid)sender).Columns) column.SortDirection = null;
        e.Column.SortDirection = direction;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!AllowClose) { e.Cancel = true; Hide(); }
    }
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is MainViewModel model) model.WindowVisible = IsVisible && WindowState != WindowState.Minimized;
    }
}
