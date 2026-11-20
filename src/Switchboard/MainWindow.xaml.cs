using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
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
/// The app window: a small, fixed-width window with its own taskbar button. It opens where you last put it
/// (by default on a monitor other than the main one), sizes itself to its content, and closing it quits the app.
/// </summary>
public sealed partial class MainWindow : Window
{
    // Room for the title, three header buttons and Windows' minimize, maximize (always shown, greyed out) and close.
    private const double WidthDip = 380;
    private const double MinHeightDip = 160;
    private const double EdgeMarginDip = 24;

    private readonly AppController _controller;
    private readonly IntPtr _hwnd;
    private readonly OverlappedPresenter _presenter;
    private readonly DispatcherQueueTimer _savePositionTimer;
    private IntPtr _icon;
    private bool _placed;
    private bool _closingForGood;

    public MainWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        _presenter = OverlappedPresenter.Create();
        _presenter.IsResizable = false;
        _presenter.IsMaximizable = false;
        _presenter.IsMinimizable = true;
        AppWindow.SetPresenter(_presenter);
        AppWindow.Title = "Switchboard";

        // Our header is the title bar; Windows draws minimize and close on the right.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleArea);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        _icon = IconFactory.CreateAppIcon(64);
        if (_icon != IntPtr.Zero) AppWindow.SetIcon(Win32Interop.GetIconIdFromIcon(_icon));
        SystemBackdrop = new MicaBackdrop();

        AppWindow.Closing += (_, e) =>
        {
            if (_closingForGood) return;
            e.Cancel = true;
            _controller.Quit();
        };
        AppWindow.Changed += OnAppWindowChanged;
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) _controller.OnWindowActivated();
        };
        Root.Loaded += (_, _) => UpdateCaptionSpace();

        _savePositionTimer = DispatcherQueue.CreateTimer();
        _savePositionTimer.Interval = TimeSpan.FromMilliseconds(600);
        _savePositionTimer.IsRepeating = false;
        _savePositionTimer.Tick += (_, _) => _controller.SaveWindowPosition(AppWindow.Position.X, AppWindow.Position.Y);

        Resize(WidthDip, 300);
    }

    public MainViewModel ViewModel => _controller.ViewModel;

    /// <summary>Shows the window, restoring it if minimized and bringing it to the front.</summary>
    public void ShowWindow(bool alwaysOnTop, WindowPlacement? savedPosition)
    {
        _presenter.IsAlwaysOnTop = alwaysOnTop;
        if (!_placed)
        {
            FitToContent();
            if (savedPosition is not null && IsOnScreen(savedPosition.X, savedPosition.Y))
                AppWindow.Move(new PointInt32(savedPosition.X, savedPosition.Y));
            else
                MoveToSideMonitor();
            _placed = true;
        }

        if (_presenter.State == OverlappedPresenterState.Minimized) _presenter.Restore();
        AppWindow.Show(true);
        Activate();
        Win32.SetForegroundWindow(_hwnd);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            UpdateCaptionSpace();
            FitToContent();
        });
    }

    public void SetAlwaysOnTop(bool alwaysOnTop) => _presenter.IsAlwaysOnTop = alwaysOnTop;

    public void CloseForGood()
    {
        _closingForGood = true;
        Close();
        if (_icon != IntPtr.Zero) Win32.DestroyIcon(_icon);
        _icon = IntPtr.Zero;
    }

    /// <summary>Sizes the window to its content, keeping its top-left corner where it is.</summary>
    public void FitToContent()
    {
        double scale = Scale();
        Root.UpdateLayout();
        Root.Measure(new Size(WidthDip, double.PositiveInfinity));
        double height = Math.Ceiling(Root.DesiredSize.Height);
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double maxHeight = work.Height / scale - 2 * EdgeMarginDip;
        Resize(WidthDip, Math.Clamp(height, MinHeightDip, Math.Max(MinHeightDip, maxHeight)));
    }

    private void Resize(double widthDip, double heightDip)
    {
        double scale = Scale();
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(widthDip * scale), (int)Math.Round(heightDip * scale)));
    }

    /// <summary>
    /// First launch: put the window on a monitor other than the main one, top right, so it stays visible
    /// while the main monitor shows another device.
    /// </summary>
    private void MoveToSideMonitor()
    {
        var primary = DisplayArea.Primary;
        DisplayArea target = primary;
        var all = DisplayArea.FindAll();
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].DisplayId.Value != primary.DisplayId.Value)
            {
                target = all[i];
                break;
            }
        }

        var work = target.WorkArea;
        int margin = (int)Math.Round(EdgeMarginDip * Scale());
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(work.X + work.Width - size.Width - margin, work.Y + margin));
    }

    private static bool IsOnScreen(int x, int y) =>
        DisplayArea.GetFromPoint(new PointInt32(x + 40, y + 20), DisplayAreaFallback.None) is not null;

    private void UpdateCaptionSpace()
    {
        double inset = AppWindow.TitleBar.RightInset / Scale();
        if (inset > 0) CaptionSpace.Width = new GridLength(inset);
    }

    private double Scale()
    {
        uint dpi = GetDpiForWindow(_hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange && _placed && sender.IsVisible && _presenter.State == OverlappedPresenterState.Restored)
        {
            _savePositionTimer.Stop();
            _savePositionTimer.Start();
        }
        if (args.DidPositionChange)
        {
            // Moving to a monitor with different scaling changes the caption button width.
            UpdateCaptionSpace();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = _controller.RefreshAsync(enumerate: true);

    private void AlwaysOnTop_Click(object sender, RoutedEventArgs e) => _controller.SetAlwaysOnTop(!ViewModel.AlwaysOnTop);

    private void ShowHidden_Click(object sender, RoutedEventArgs e) => _controller.ToggleShowHidden();

    private void EditSettings_Click(object sender, RoutedEventArgs e) => _controller.EditSettingsFile();

    private void OpenLog_Click(object sender, RoutedEventArgs e) => _controller.OpenLog();

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
