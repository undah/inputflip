using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Switchboard;

/// <summary>Small helpers used from x:Bind in XAML.</summary>
public static class Ui
{
    public static string IconGlyph(string icon) => icon switch
    {
        "pc" => "",
        "laptop" => "",
        "console" => "",
        "tv" => "",
        _ => "",
    };

    // The input showing now gets the accent fill, like a switched-on Quick Settings tile.
    public static Brush RowBackground(bool isCurrent) =>
        Brush(isCurrent ? "AccentFillColorDefaultBrush" : "SubtleFillColorTransparentBrush");

    public static Brush RowForeground(bool isCurrent) =>
        Brush(isCurrent ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");

    public static Brush RowSecondaryForeground(bool isCurrent) =>
        Brush(isCurrent ? "TextOnAccentFillColorSecondaryBrush" : "TextFillColorSecondaryBrush");

    public static double HiddenOpacity(bool isHidden) => isHidden ? 0.45 : 1.0;

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static string PinGlyph(bool alwaysOnTop) => alwaysOnTop ? "" : "";

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
