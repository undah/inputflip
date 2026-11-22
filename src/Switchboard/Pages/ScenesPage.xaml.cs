using Microsoft.UI.Xaml.Controls;
using Switchboard.ViewModels;

namespace Switchboard.Pages;

public sealed partial class ScenesPage : UserControl
{
    public ScenesPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }
}
