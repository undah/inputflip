using Microsoft.UI.Xaml.Controls;
using Switchboard.ViewModels;

namespace Switchboard.Pages;

public sealed partial class ShortcutsPage : UserControl
{
    public ShortcutsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    /// <summary>The setup window has its own heading.</summary>
    public bool ShowTitle
    {
        get => PageTitle.Visibility == Microsoft.UI.Xaml.Visibility.Visible;
        set => PageTitle.Visibility = PageIntro.Visibility = value ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    }
}
