namespace Switchboard.Core.Monitors;

/// <summary>
/// Where the mouse goes while a monitor shows another device. Windows still counts that monitor as connected (over
/// HDMI the MSI G27CQ4 keeps the PC's link up while it shows the PS5), so a pointer left on it is lost from view.
/// </summary>
public static class MouseParking
{
    /// <summary>
    /// The middle of Switchboard's window when it's on another screen, else the middle of the main display, else of
    /// any other screen. Null when there's no other screen to go to.
    /// </summary>
    /// <param name="leaving">The monitor that's switching to another device.</param>
    /// <param name="screens">Every screen, the leaving one included.</param>
    /// <param name="window">Switchboard's window, or null when it's minimized or hidden.</param>
    public static (int X, int Y)? Spot(ScreenRect leaving, IReadOnlyList<(ScreenRect Bounds, bool IsPrimary)> screens, ScreenRect? window)
    {
        if (window is { } w && !Contains(leaving, Middle(w)) && screens.Any(s => Contains(s.Bounds, Middle(w))))
            return Middle(w);

        // Screens that show the same picture as the leaving one (duplicated displays) don't help.
        var others = screens.Where(s => !Contains(leaving, Middle(s.Bounds))).ToList();
        var pick = others.FirstOrDefault(s => s.IsPrimary);
        if (pick.Bounds == default) pick = others.FirstOrDefault();
        return pick.Bounds == default ? null : Middle(pick.Bounds);
    }

    public static bool Contains(ScreenRect r, (int X, int Y) p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    private static (int X, int Y) Middle(ScreenRect r) => ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
}
