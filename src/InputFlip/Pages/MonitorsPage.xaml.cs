using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using InputFlip.ViewModels;

namespace InputFlip.Pages;

public sealed partial class MonitorsPage : UserControl
{
    public MonitorsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    /// <summary>Opens and scrolls to one monitor ("Monitor settings" from the main window).</summary>
    public void ShowMonitor(string monitorId)
    {
        void Go()
        {
            int index = ViewModel.Monitors.ToList().FindIndex(m => m.Id == monitorId);
            if (index < 0 || MonitorList.ContainerFromIndex(index) is not ContentPresenter { Content: not null } container) return;
            if (FindExpander(container) is not { } expander) return;
            expander.IsExpanded = true;
            expander.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
            expander.Focus(FocusState.Programmatic);
        }

        if (IsLoaded) DispatcherQueue.TryEnqueue(Go);
        else Loaded += (_, _) => DispatcherQueue.TryEnqueue(Go);
    }

    private static Expander? FindExpander(DependencyObject root)
    {
        if (root is Expander e) return e;
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            if (FindExpander(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private void AddBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || sender is not TextBox { DataContext: MonitorEditViewModel monitor }) return;
        monitor.AddCommand.Execute(null);
        e.Handled = true;
    }
}
