using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace InputFlip;

/// <summary>
/// The tile flip: when an input becomes the one showing now, its tile turns over around its vertical middle and lands
/// blue side up. A Composition animation on the element's visual, not a XAML transition: WinUI 1.8's
/// BrushTransition/ScalarTransition on these rows crashed the app on a theme change (CLAUDE.md, WinUI pitfalls).
/// Skipped when Windows' "Animation effects" setting is off.
/// </summary>
public static class Motion
{
    public static readonly DependencyProperty FlipWhenProperty = DependencyProperty.RegisterAttached(
        "FlipWhen", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnFlipWhenChanged));

    public static bool GetFlipWhen(DependencyObject element) => (bool)element.GetValue(FlipWhenProperty);

    public static void SetFlipWhen(DependencyObject element, bool value) => element.SetValue(FlipWhenProperty, value);

    private static readonly Windows.UI.ViewManagement.UISettings Settings = new();

    private static void OnFlipWhenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Only a change while the tile is on screen; not the first value when the window fills in.
        if (e.NewValue is not true || e.OldValue is not false || d is not FrameworkElement { IsLoaded: true } element) return;
        try
        {
            if (!Settings.AnimationsEnabled || element.ActualWidth <= 0) return;
            Flip(element);
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"Tile animation failed: {ex.Message}");
        }
    }

    private static void Flip(FrameworkElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        visual.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        visual.RotationAxis = new Vector3(0, 1, 0);

        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1.0f));
        var turn = compositor.CreateScalarKeyFrameAnimation();
        turn.InsertKeyFrame(0f, 80f);
        turn.InsertKeyFrame(1f, 0f, ease);
        turn.Duration = TimeSpan.FromMilliseconds(380);
        visual.StartAnimation(nameof(Visual.RotationAngleInDegrees), turn);

        // A little lift as it lands.
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3(0.94f, 0.94f, 1f));
        scale.InsertKeyFrame(0.7f, new Vector3(1.02f, 1.02f, 1f), ease);
        scale.InsertKeyFrame(1f, Vector3.One);
        scale.Duration = TimeSpan.FromMilliseconds(380);
        visual.StartAnimation(nameof(Visual.Scale), scale);
    }
}
