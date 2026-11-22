using Microsoft.UI.Xaml.Controls;
using InputFlip.ViewModels;

namespace InputFlip.Pages;

public sealed partial class ScenesPage : UserControl
{
    public ScenesPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }
}
