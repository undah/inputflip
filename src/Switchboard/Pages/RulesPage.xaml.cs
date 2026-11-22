using Microsoft.UI.Xaml.Controls;
using Switchboard.ViewModels;

namespace Switchboard.Pages;

public sealed partial class RulesPage : UserControl
{
    public RulesPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }
}
