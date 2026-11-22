using Microsoft.UI.Xaml.Controls;
using InputFlip.ViewModels;

namespace InputFlip.Pages;

public sealed partial class RulesPage : UserControl
{
    public RulesPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }
}
