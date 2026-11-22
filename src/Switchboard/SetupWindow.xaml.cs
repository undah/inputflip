using System.Collections.ObjectModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Switchboard.Native;
using Switchboard.Pages;
using Switchboard.ViewModels;

namespace Switchboard;

/// <summary>
/// First-run setup, shown when there's no settings file: monitors found and whether they answer, input names and
/// icons, a screens-off suggestion for monitors like the MSI over DisplayPort, and shortcuts. Every step applies
/// at once, so Skip just closes the window and keeps what was found.
/// </summary>
public sealed partial class SetupWindow : Window
{
    private const string Monitors = "monitors", Names = "names", ScreensOff = "screensoff", Shortcuts = "shortcuts";

    private readonly AppController _app;
    private readonly List<string> _steps = [Monitors, Names, Shortcuts];
    private int _index;
    private bool _suggestionsDecided;

    public SetupWindow(AppController app)
    {
        _app = app;
        ViewModel = new SettingsViewModel(app);
        InitializeComponent();
        WindowChrome.SetUp(this, TitleArea, "Set up InputFlip");
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.PreferredMinimumWidth = 560;
            presenter.PreferredMinimumHeight = 480;
        }
        ShortcutsStep.Content = new ShortcutsPage(ViewModel) { ShowTitle = false };

        _app.StateChanged += OnStateChanged;
        Closed += (_, _) => _app.StateChanged -= OnStateChanged;
        ShowStep();
    }

    public SettingsViewModel ViewModel { get; }

    public ObservableCollection<ScreensOffSuggestionViewModel> Suggestions { get; } = new();

    public void Place(DisplayArea display, double scale) => WindowChrome.CenterOn(this, display, 760, 680, scale);

    private void OnStateChanged()
    {
        ViewModel.Sync();
        UpdateNoMonitors();
        if (UpdateSteps()) StepText.Text = $"Step {_index + 1} of {_steps.Count}";
    }

    /// <summary>
    /// The screens-off step only appears when a monitor fits the pattern, which is known once its capabilities
    /// read has failed (a few seconds after start). Decided for good when leaving the names step.
    /// </summary>
    private bool UpdateSteps()
    {
        if (_suggestionsDecided) return false;
        bool wanted = ViewModel.Monitors.Any(m => m.IsConnected && m.SuggestedScreensOffValue is not null);
        bool has = _steps.Contains(ScreensOff);
        if (wanted == has) return false;
        if (wanted) _steps.Insert(_steps.IndexOf(Names) + 1, ScreensOff);
        else _steps.Remove(ScreensOff);
        return true;
    }

    private void UpdateNoMonitors() =>
        NoMonitorsText.Visibility = ViewModel.Monitors.Any(m => m.IsConnected) ? Visibility.Collapsed : Visibility.Visible;

    private void ShowStep()
    {
        UpdateSteps();
        string step = _steps[_index];
        StepText.Text = $"Step {_index + 1} of {_steps.Count}";
        (StepTitle.Text, StepIntro.Text) = step switch
        {
            Monitors => ("Your monitors",
                "InputFlip switches inputs over DDC/CI, the control channel in the display cable. A monitor that isn't answering usually has DDC/CI turned off in its on-screen menu; some only answer over HDMI."),
            Names => ("Name your inputs",
                "Say what's plugged into each input, such as \"PS5\" or \"Work laptop\", and pick an icon. Untick inputs you don't use, such as ports the monitor lists but doesn't have."),
            ScreensOff => ("Monitors that won't switch away",
                "Some monitors ignore switch commands while they show this PC. InputFlip can turn all screens off for a few seconds while it switches, which makes them listen. Going back to the PC works normally."),
            _ => ("Shortcuts",
                "Switch from anywhere, even while a game has the screen. Click a shortcut, then press the keys you want. You can change these later in Settings."),
        };

        MonitorsStep.Visibility = step == Monitors ? Visibility.Visible : Visibility.Collapsed;
        NamesStep.Visibility = step == Names ? Visibility.Visible : Visibility.Collapsed;
        ScreensOffStep.Visibility = step == ScreensOff ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsStep.Visibility = step == Shortcuts ? Visibility.Visible : Visibility.Collapsed;
        UpdateNoMonitors();

        BackButton.Visibility = _index == 0 ? Visibility.Collapsed : Visibility.Visible;
        bool last = _index == _steps.Count - 1;
        NextButton.Content = last ? "Done" : "Next";
        SkipButton.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
        StepTitle.Focus(FocusState.Programmatic);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_index == _steps.Count - 1)
        {
            Close();
            return;
        }

        if (_steps[_index] == Names && !_suggestionsDecided)
        {
            UpdateSteps();
            _suggestionsDecided = true;
            // Suggested monitors get the screens-off switch turned on; the next step shows it and lets you turn it off.
            Suggestions.Clear();
            foreach (var m in ViewModel.Monitors.Where(m => m.IsConnected && m.SuggestedScreensOffValue is not null))
                Suggestions.Add(new ScreensOffSuggestionViewModel(m, m.SuggestedScreensOffValue!.Value) { IsOn = true });
        }

        _index++;
        ShowStep();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_index == 0) return;
        _index--;
        ShowStep();
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Close();

    private void CheckAgain_Click(object sender, RoutedEventArgs e) => _ = _app.RefreshAsync(enumerate: true);
}
