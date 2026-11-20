using Microsoft.UI.Xaml;

namespace Switchboard;

/// <summary>An icon an input can have, for pickers.</summary>
public sealed record IconChoice(string Key, string Label, string Glyph);

/// <summary>Small helpers used from x:Bind in XAML.</summary>
public static class Ui
{
    public static IReadOnlyList<IconChoice> IconChoices { get; } =
    [
        new("pc", "PC", "\uE977"),
        new("laptop", "Laptop", "\uE7F8"),
        new("console", "Console", "\uE7FC"),
        new("tv", "TV or media player", "\uE714"),
        new("display", "Plain monitor", "\uE7F4"),
    ];

    public static string IconGlyph(string icon) =>
        IconChoices.FirstOrDefault(c => c.Key == icon)?.Glyph ?? "\uE7F4";

    /// <summary>
    /// The input showing now gets the accent fill, like a switched-on Quick Settings tile. Styles rather than brushes,
    /// so the colors are theme resources that follow light and dark mode.
    /// </summary>
    public static Style RowStyle(bool isCurrent) =>
        (Style)Application.Current.Resources[isCurrent ? "CurrentInputRowStyle" : "InputRowStyle"];

    public static double HiddenOpacity(bool isHidden) => isHidden ? 0.5 : 1.0;

    public static double ShownOpacity(bool isShown) => isShown ? 1.0 : 0.5;

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static bool HasText(string? text) => !string.IsNullOrEmpty(text);

    public static bool Not(bool value) => !value;

    public static bool IsIcon(string key, string expected) => key == expected;

    public static string PinGlyph(bool alwaysOnTop) => alwaysOnTop ? "\uE77A" : "\uE718";
}
