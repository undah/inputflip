namespace InputFlip.Core.Monitors;

/// <summary>
/// Where the "MSI → PS5" note goes after a switch: on a screen you can still see, so not on a monitor that just
/// switched (it shows the other device now, or it's black for a few seconds while it comes back).
/// </summary>
public static class ToastPlacement
{
    /// <summary>
    /// The screen with the mouse, else the one with InputFlip's window, else the main display, else any other screen,
    /// leaving out the monitors that switched. When every screen switched: the one with the mouse.
    /// </summary>
    /// <param name="screens">Every screen.</param>
    /// <param name="switched">Bounds of the monitors that just switched.</param>
    public static ScreenRect? PickScreen(IReadOnlyList<(ScreenRect Bounds, bool IsPrimary)> screens, IReadOnlyList<ScreenRect> switched,
        (int X, int Y)? mouse, ScreenRect? window)
    {
        if (screens.Count == 0) return null;
        bool Switched(ScreenRect s) => switched.Any(w => w == s || MouseParking.Contains(w, Middle(s)));
        var visible = screens.Where(s => !Switched(s.Bounds)).ToList();

        ScreenRect? With((int X, int Y) point, IEnumerable<(ScreenRect Bounds, bool IsPrimary)> among) =>
            among.Where(s => MouseParking.Contains(s.Bounds, point)).Select(s => (ScreenRect?)s.Bounds).FirstOrDefault();

        if (mouse is { } m && With(m, visible) is { } underMouse) return underMouse;
        if (window is { } w && With(Middle(w), visible) is { } underWindow) return underWindow;
        if (visible.FirstOrDefault(s => s.IsPrimary) is { Bounds: var primary } && primary != default) return primary;
        if (visible.Count > 0) return visible[0].Bounds;
        if (mouse is { } m2 && With(m2, screens) is { } any) return any;
        return screens[0].Bounds;
    }

    /// <summary>Top-left corner for a note of this size: centered, <paramref name="margin"/> above the bottom of the screen.</summary>
    public static (int X, int Y) Position(ScreenRect screen, int width, int height, int margin) =>
        ((screen.Left + screen.Right - width) / 2, screen.Bottom - height - margin);

    private static (int X, int Y) Middle(ScreenRect r) => ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
}
