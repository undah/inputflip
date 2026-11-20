using Microsoft.UI.Xaml.Controls;
using Switchboard.ViewModels;

namespace Switchboard.Pages;

public sealed partial class GeneralPage : UserControl
{
    public GeneralPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }
}
