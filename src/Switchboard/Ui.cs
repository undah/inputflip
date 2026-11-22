using Microsoft.UI.Xaml;

namespace Switchboard;

/// <summary>An icon an input can have, for pickers.</summary>
public sealed record IconChoice(string Key, string Label, string Glyph);

/// <summary>Small helpers used from x:Bind in XAML.</summary>
public static class Ui
{
    public static IReadOnlyList<IconChoice> IconChoices { get; } =
    [
        new("pc", "PC", ""),
        new("laptop", "Laptop", ""),
        new("console", "Console", ""),
        new("tv", "TV or media player", ""),
        new("display", "Plain monitor", ""),
    ];

    /// <summary>Scenes without an icon of their own: a stack of tiles.</summary>
    public const string SceneGlyph = "";

    public static string IconGlyph(string icon) =>
        icon == "scene" ? SceneGlyph : IconChoices.FirstOrDefault(c => c.Key == icon)?.Glyph ?? "";

    /// <summary>
    /// The input showing now is a coral tile. Styles rather than brushes, so the colors are theme resources that follow
    /// light and dark mode (a brush looked up here would be one theme's, whatever the window's theme).
    /// </summary>
    public static Style TileStyle(bool isCurrent) =>
        (Style)Application.Current.Resources[isCurrent ? "CurrentTileStyle" : "TileStyle"];

    public static Style TileIconStyle(bool isCurrent) =>
        (Style)Application.Current.Resources[isCurrent ? "CurrentTileIconStyle" : "TileIconStyle"];

    public static Style SceneStyle(bool isActive) =>
        (Style)Application.Current.Resources[isActive ? "CurrentSceneChipStyle" : "SceneChipStyle"];

    public static double HiddenOpacity(bool isHidden) => isHidden ? 0.5 : 1.0;

    public static double ShownOpacity(bool isShown) => isShown ? 1.0 : 0.5;

    /// <summary>Secondary text on a tile: a little fainter, readable on coral and on plain tiles alike.</summary>
    public static double SubtleOpacity(bool isCurrent) => isCurrent ? 0.8 : 0.72;

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static bool HasText(string? text) => !string.IsNullOrEmpty(text);

    public static bool Not(bool value) => !value;

    public static bool IsIcon(string key, string expected) => key == expected;

    public static string PinGlyph(bool alwaysOnTop) => alwaysOnTop ? "" : "";

    public static string MuteGlyph(bool muted) => muted ? "" : "";

    public static string Percent(double value) => $"{Math.Round(value):0}";
}
