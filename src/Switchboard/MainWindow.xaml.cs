using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
        WindowChrome.SetUp(this, TitleArea, "Switchboard");

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
        Root.Loaded += (_, _) =>
        {
            UpdateCaptionSpace();
            // Dragged to a display with other scaling (100% to 150%): the content keeps its size in DIPs,
            // so the window needs a new size in pixels.
            double lastScale = Root.XamlRoot.RasterizationScale;
            Root.XamlRoot.Changed += (_, _) =>
            {
                if (Math.Abs(Root.XamlRoot.RasterizationScale - lastScale) < 0.01) return;
                lastScale = Root.XamlRoot.RasterizationScale;
                UpdateCaptionSpace();
                FitToContent();
            };
        };
        ViewModel.PropertyChanged += OnViewModelChanged;
        PinButton.IsChecked = ViewModel.AlwaysOnTop;
        // Ctrl+, opens settings, as in many Windows apps. Not a KeyboardAccelerator: WinUI crashes on the comma key
        // (VK_OEM_COMMA has no VirtualKey name) when it builds the accelerator's tooltip text.
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Root_KeyDown), handledEventsToo: true);

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
    }

    /// <summary>The display the window is on, for opening other windows next to it.</summary>
    public DisplayArea Display => DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);

    /// <summary>Where the window is on screen in physical pixels, or null when it's minimized or hidden.</summary>
    public Switchboard.Core.Monitors.ScreenRect? ScreenBounds
    {
        get
        {
            if (!AppWindow.IsVisible || _presenter.State == OverlappedPresenterState.Minimized) return null;
            var p = AppWindow.Position;
            var s = AppWindow.Size;
            return new Switchboard.Core.Monitors.ScreenRect(p.X, p.Y, p.X + s.Width, p.Y + s.Height);
        }
    }

    /// <summary>
    /// Has Narrator read a message without showing it, for news the window already shows another way (the highlight
    /// moving to the new input).
    /// </summary>
    public void Announce(string text)
    {
        try
        {
            var peer = FrameworkElementAutomationPeer.FromElement(SettingsButton) ?? FrameworkElementAutomationPeer.CreatePeerForElement(SettingsButton);
            peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, text, "SwitchboardSwitch");
        }
        catch (Exception ex)
        {
            Switchboard.Core.Log.Warn($"Announcing \"{text}\" failed: {ex.Message}");
        }
    }

    /// <summary>Sizes the window to its content, keeping its top-left corner where it is.</summary>
    public void FitToContent()
    {
        double scale = WindowChrome.Scale(this);
        Root.UpdateLayout();
        Root.Measure(new Size(WidthDip, double.PositiveInfinity));
        double height = Math.Ceiling(Root.DesiredSize.Height);
        var work = Display.WorkArea;
        double maxHeight = work.Height / scale - 2 * EdgeMarginDip;
        Resize(WidthDip, Math.Clamp(height, MinHeightDip, Math.Max(MinHeightDip, maxHeight)));
    }

    private void Resize(double widthDip, double heightDip)
    {
        double scale = WindowChrome.Scale(this);
        int width = (int)Math.Round(widthDip * scale), height = (int)Math.Round(heightDip * scale);
        // Our content runs under the title bar, so the area XAML gets isn't AppWindow's client area
        // (that one leaves out a system title bar we don't show). Size the window from what XAML gets.
        var xaml = Root.XamlRoot?.Size;
        if (xaml is { Width: > 0, Height: > 0 } area)
        {
            var outer = AppWindow.Size;
            int extraW = outer.Width - (int)Math.Round(area.Width * scale);
            int extraH = outer.Height - (int)Math.Round(area.Height * scale);
            AppWindow.Resize(new SizeInt32(width + extraW, height + extraH));
        }
        else
        {
            AppWindow.ResizeClient(new SizeInt32(width, height));
        }
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
        int margin = (int)Math.Round(EdgeMarginDip * WindowChrome.Scale(this));
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(work.X + work.Width - size.Width - margin, work.Y + margin));
    }

    private static bool IsOnScreen(int x, int y) =>
        DisplayArea.GetFromPoint(new PointInt32(x + 40, y + 20), DisplayAreaFallback.None) is not null;

    private void UpdateCaptionSpace()
    {
        double inset = AppWindow.TitleBar.RightInset / WindowChrome.Scale(this);
        if (inset > 0) CaptionSpace.Width = new GridLength(inset);
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
            // Moving to a monitor with different scaling changes the caption button width and the window's size in pixels.
            UpdateCaptionSpace();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.AlwaysOnTop))
        {
            PinButton.IsChecked = ViewModel.AlwaysOnTop;
        }
        else if (e.PropertyName == nameof(MainViewModel.Status) && ViewModel.HasStatus)
        {
            // Narrator reads status changes ("switched to PS5") without moving focus.
            DispatcherQueue.TryEnqueue(() =>
                FrameworkElementAutomationPeer.FromElement(StatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged));
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        const Windows.System.VirtualKey Comma = (Windows.System.VirtualKey)0xBC;
        if (e.Key != Comma) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        if (!ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        e.Handled = true;
        _controller.OpenSettings(null);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _ = _controller.RefreshAsync(enumerate: true);
        _ = _controller.CheckForUpdatesAsync(quiet: true);
    }

    private void Update_Click(object sender, RoutedEventArgs e) => _ = _controller.InstallUpdateAsync();

    private void AlwaysOnTop_Click(object sender, RoutedEventArgs e) =>
        _controller.SetAlwaysOnTop(PinButton.IsChecked == true);

    private void Settings_Click(object sender, RoutedEventArgs e) => _controller.OpenSettings(null);

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
}
