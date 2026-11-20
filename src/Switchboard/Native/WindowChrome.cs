using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace Switchboard.Native;

/// <summary>What every Switchboard window has in common: our header as the title bar, Mica, the app icon, the theme.</summary>
internal static class WindowChrome
{
    public static string IconPath { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "Switchboard.ico");

    public static void SetUp(Window window, UIElement titleArea, string title)
    {
        window.AppWindow.Title = title;
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(titleArea);
        var bar = window.AppWindow.TitleBar;
        bar.PreferredHeightOption = TitleBarHeightOption.Tall;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        if (File.Exists(IconPath)) window.AppWindow.SetIcon(IconPath);
        window.SystemBackdrop = new MicaBackdrop();

        bar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
    }

    /// <summary>Light, dark, or Default to follow Windows. The caption buttons (minimize, close) follow too.</summary>
    public static void ApplyTheme(Window window, ElementTheme theme)
    {
        if (window.Content is FrameworkElement root) root.RequestedTheme = theme;
        window.AppWindow.TitleBar.PreferredTheme = theme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    public static double Scale(Window window)
    {
        uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(window));
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>Sizes a window (in DIPs, at the scale of the display it will open on) and centers it on that display.</summary>
    public static void CenterOn(Window window, DisplayArea display, double widthDip, double heightDip, double scale)
    {
        var work = display.WorkArea;
        int w = Math.Min((int)Math.Round(widthDip * scale), work.Width);
        int h = Math.Min((int)Math.Round(heightDip * scale), work.Height);
        window.AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
