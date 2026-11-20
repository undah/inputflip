using Microsoft.UI.Xaml.Controls;
using Switchboard.ViewModels;

namespace Switchboard.Pages;

public sealed partial class AboutPage : UserControl
{
    public AboutPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }
}
