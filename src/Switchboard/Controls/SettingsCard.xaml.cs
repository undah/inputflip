using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Switchboard.Controls;

/// <summary>A Windows 11 Settings-style row: icon, title and description on the left, a control on the right.</summary>
public sealed partial class SettingsCard : UserControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingsCard), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsCard), new PropertyMetadata(null));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingsCard), new PropertyMetadata(null));

    public static readonly DependencyProperty ActionProperty =
        DependencyProperty.Register(nameof(Action), typeof(object), typeof(SettingsCard), new PropertyMetadata(null));

    public SettingsCard() => InitializeComponent();

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    public string? Glyph { get => (string?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    /// <summary>The control on the right: a toggle, a button, a shortcut recorder.</summary>
    public object? Action { get => GetValue(ActionProperty); set => SetValue(ActionProperty, value); }
}
