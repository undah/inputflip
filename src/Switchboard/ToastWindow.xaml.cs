using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Switchboard.Core.Monitors;
using Switchboard.Native;
using Windows.Graphics;

namespace Switchboard;

/// <summary>
/// The short note after a switch from a shortcut, a scene, a rule, the picker or the command line, shown at the bottom
/// of a screen you can still see. Never takes focus, has no taskbar button, hides itself after a couple of seconds.
/// </summary>
public sealed partial class ToastWindow : Window
{
    private const double WidthDip = 320, HeightDip = 64, BottomMarginDip = 96;
    private static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(2.4);

    private readonly OverlappedPresenter _presenter;
    private readonly DispatcherQueueTimer _hideTimer;
    private bool _closingForGood;

    public ToastWindow()
    {
        InitializeComponent();
        _presenter = OverlappedPresenter.Create();
        _presenter.IsResizable = false;
        _presenter.IsMaximizable = false;
        _presenter.IsMinimizable = false;
        _presenter.IsAlwaysOnTop = true;
        _presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(_presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Title = "InputFlip";
        WindowChrome.RoundCorners(this);
        WindowChrome.NoActivate(this);

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = ShowFor;
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => AppWindow.Hide();
        Root.PointerPressed += (_, _) => AppWindow.Hide();
        AppWindow.Closing += (_, e) =>
        {
            if (_closingForGood) return;
            e.Cancel = true;
            AppWindow.Hide();
        };
    }

    public void Show(string title, string detail, string glyph, ScreenRect screen)
    {
        TitleText.Text = title;
        DetailText.Text = detail;
        DetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        Glyph.Glyph = glyph;

        // Size for the target screen's scaling: move there first, then read the scale.
        AppWindow.Move(new PointInt32((screen.Left + screen.Right) / 2, (screen.Top + screen.Bottom) / 2));
        double scale = WindowChrome.Scale(this);
        int w = (int)Math.Round(WidthDip * scale), h = (int)Math.Round(HeightDip * scale);
        var (x, y) = ToastPlacement.Position(screen, w, h, (int)Math.Round(BottomMarginDip * scale));
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));

        AppWindow.Show(activateWindow: false);
        FadeIn();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void FadeIn()
    {
        try
        {
            if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
            ElementCompositionPreview.SetIsTranslationEnabled(Root, true);
            var visual = ElementCompositionPreview.GetElementVisual(Root);
            var c = visual.Compositor;
            var fade = c.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f);
            fade.Duration = TimeSpan.FromMilliseconds(160);
            var rise = c.CreateVector3KeyFrameAnimation();
            rise.InsertKeyFrame(0f, new Vector3(0, 8, 0));
            rise.InsertKeyFrame(1f, Vector3.Zero, c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1f)));
            rise.Duration = TimeSpan.FromMilliseconds(220);
            visual.StartAnimation("Opacity", fade);
            visual.StartAnimation("Translation", rise);
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"Toast animation failed: {ex.Message}");
        }
    }

    public void CloseForGood()
    {
        _closingForGood = true;
        Close();
    }
}
