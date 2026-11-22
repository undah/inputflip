using Microsoft.UI.Xaml.Controls;
using InputFlip.ViewModels;

namespace InputFlip.Pages;

public sealed partial class AboutPage : UserControl
{
    public AboutPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }
}
