using Microsoft.UI.Xaml.Controls;
using InputFlip.ViewModels;

namespace InputFlip.Pages;

public sealed partial class GeneralPage : UserControl
{
    public GeneralPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }
}
