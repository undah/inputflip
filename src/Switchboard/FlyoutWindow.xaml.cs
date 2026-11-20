using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Switchboard.Core.Settings;
using Switchboard.Native;
using Switchboard.ViewModels;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace Switchboard;

/// <summary>
/// The switch window. Normally a flyout above the tray that hides when you click elsewhere;
/// with "keep open" it stays on top and can be dragged by its header, for example onto a monitor
/// that isn't the one being switched.
/// </summary>
public sealed partial class FlyoutWindow : Window
{
    private const double WidthDip = 340;
    private const double EdgeMarginDip = 12;

    private readonly AppController _controller;
    private readonly IntPtr _hwnd;
    private bool _keepOpen;
    private bool _closingForGood;
    private DateTime _hiddenAt = DateTime.MinValue;

    public FlyoutWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Title = "Switchboard";
        AppWindow.Closing += (_, e) =>
        {
            if (_closingForGood) return;
            e.Cancel = true;
            HideFlyout();
        };

        int corner = Win32.DWMWCP_ROUND;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        SystemBackdrop = new DesktopAcrylicBackdrop();

        Activated += OnActivated;
        Root.KeyDown += Root_KeyDown;
        Resize(WidthDip, 240);
    }

    public MainViewModel ViewModel => _controller.ViewModel;

    public bool IsShown => AppWindow.IsVisible;

    /// <summary>True right after the window hid itself; a tray click that caused the hide shouldn't reopen it.</summary>
    public bool RecentlyHidden => DateTime.UtcNow - _hiddenAt < TimeSpan.FromMilliseconds(300);

    public void ShowFlyout(bool keepOpen, WindowPlacement? keptPosition)
    {
        _keepOpen = keepOpen;
        bool wasVisible = AppWindow.IsVisible;
        FitToContent();

        if (keepOpen && keptPosition is not null && IsOnScreen(keptPosition.X, keptPosition.Y))
            AppWindow.Move(new PointInt32(keptPosition.X, keptPosition.Y));
        else if (!keepOpen || !wasVisible)
            MoveNearTray();

        AppWindow.Show(true);
        Activate();
        Win32.SetForegroundWindow(_hwnd);
        // Layout isn't final until the window has rendered once.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FitToContent);
    }

    public void HideFlyout()
    {
        if (!AppWindow.IsVisible) return;
        _hiddenAt = DateTime.UtcNow;
        foreach (var monitor in ViewModel.Monitors)
            foreach (var input in monitor.Inputs)
                input.CancelRename();
        AppWindow.Hide();
    }

    public void ApplyKeepOpen(bool keepOpen)
    {
        _keepOpen = keepOpen;
        if (keepOpen && AppWindow.IsVisible)
            _controller.SaveKeepOpenPosition(AppWindow.Position.X, AppWindow.Position.Y);
    }

    public void CloseForGood()
    {
        _closingForGood = true;
        Close();
    }

    /// <summary>Sizes the window to its content. When it sits above the tray, it grows upwards.</summary>
    public void FitToContent()
    {
        double scale = Scale();
        Root.UpdateLayout();
        Root.Measure(new Size(WidthDip, double.PositiveInfinity));
        double height = Math.Ceiling(Root.DesiredSize.Height);
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double maxHeight = work.Height / scale - 2 * EdgeMarginDip;
        height = Math.Clamp(height, 120, Math.Max(120, maxHeight));

        var old = AppWindow.Size;
        Resize(WidthDip, height);
        if (AppWindow.IsVisible && !_keepOpen && old.Height != AppWindow.Size.Height) MoveNearTray();
    }

    private void Resize(double widthDip, double heightDip)
    {
        double scale = Scale();
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(widthDip * scale), (int)Math.Round(heightDip * scale)));
    }

    private void MoveNearTray()
    {
        // The tray is on the main display's taskbar; sit just above it, at the right.
        var work = DisplayArea.Primary.WorkArea;
        int margin = (int)Math.Round(EdgeMarginDip * Scale());
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(work.X + work.Width - size.Width - margin, work.Y + work.Height - size.Height - margin));
    }

    private static bool IsOnScreen(int x, int y) =>
        DisplayArea.GetFromPoint(new PointInt32(x + 40, y + 20), DisplayAreaFallback.None) is not null;

    private double Scale()
    {
        uint dpi = GetDpiForWindow(_hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && !_keepOpen)
            HideFlyout();
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && !_keepOpen)
        {
            HideFlyout();
            e.Handled = true;
        }
    }

    private void Header_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_keepOpen || !e.GetCurrentPoint(Header).Properties.IsLeftButtonPressed) return;
        // Hand the drag to Windows, as if the header were a title bar. Returns when the drag ends.
        Win32.ReleaseCapture();
        Win32.SendMessage(_hwnd, Win32.WM_NCLBUTTONDOWN, Win32.HTCAPTION, IntPtr.Zero);
        _controller.SaveKeepOpenPosition(AppWindow.Position.X, AppWindow.Position.Y);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = _controller.RefreshAsync(enumerate: true);

    private void KeepOpen_Click(object sender, RoutedEventArgs e) => _controller.SetKeepOpen(!ViewModel.KeepOpen);

    private void ShowHidden_Click(object sender, RoutedEventArgs e) => _controller.ToggleShowHidden();

    private void RenameBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        }
    }

    private void RenameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: InputViewModel input } box) return;
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            input.CommitRename(box.Text);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            input.CancelRename();
            e.Handled = true;
        }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: InputViewModel input } box) input.CommitRename(box.Text);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
