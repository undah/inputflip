using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Switchboard.Native;
using Switchboard.Pages;
using Switchboard.ViewModels;

namespace Switchboard;

/// <summary>Settings: General, Monitors, Scenes, Rules, Shortcuts, About. Changes apply and save at once.</summary>
public sealed partial class SettingsWindow : Window
{
    private readonly AppController _app;
    private readonly SettingsViewModel _viewModel;
    private readonly Dictionary<string, UserControl> _pages = new();
    private string? _pendingMonitor;

    public SettingsWindow(AppController app)
    {
        _app = app;
        _viewModel = new SettingsViewModel(app);
        InitializeComponent();
        WindowChrome.SetUp(this, TitleArea, "InputFlip settings");
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 640;
            presenter.PreferredMinimumHeight = 480;
        }

        _app.StateChanged += OnStateChanged;
        Closed += (_, _) => _app.StateChanged -= OnStateChanged;
    }

    /// <summary>Opens in the middle of the display the main window is on.</summary>
    public void Place(DisplayArea display, double scale) => WindowChrome.CenterOn(this, display, 1000, 720, scale);

    /// <summary>Shows a page ("general", "monitors", "shortcuts", "about"); for monitors, optionally one monitor.</summary>
    public void Navigate(string? page, string? monitorId = null)
    {
        _pendingMonitor = monitorId;
        string tag = page is "general" or "monitors" or "scenes" or "rules" or "shortcuts" or "about" ? page : (Nav.SelectedItem as NavigationViewItem)?.Tag as string ?? "general";
        var item = Nav.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == tag);
        if (ReferenceEquals(Nav.SelectedItem, item)) Show(tag);
        else Nav.SelectedItem = item;
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag }) Show(tag);
    }

    private void Show(string tag)
    {
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "monitors" => new MonitorsPage(_viewModel),
                "scenes" => new ScenesPage(_viewModel),
                "rules" => new RulesPage(_viewModel),
                "shortcuts" => new ShortcutsPage(_viewModel),
                "about" => new AboutPage(_viewModel),
                _ => new GeneralPage(_viewModel),
            };
            _pages[tag] = page;
        }
        if (!ReferenceEquals(PageHost.Content, page))
        {
            PageHost.Content = page;
            Scroller.ChangeView(null, 0, null, disableAnimation: true);
        }
        if (_pendingMonitor is { } id && page is MonitorsPage monitors) monitors.ShowMonitor(id);
        _pendingMonitor = null;
    }

    private void OnStateChanged() => _viewModel.Sync();
}
