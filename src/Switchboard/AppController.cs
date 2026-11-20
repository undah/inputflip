using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Switchboard.Core;
using Switchboard.Core.Hotkeys;
using Switchboard.Core.Monitors;
using Switchboard.Core.Settings;
using Switchboard.Native;
using Switchboard.ViewModels;

namespace Switchboard;

/// <summary>
/// Owns the services and keeps the view model in step with them. Everything here runs on the UI thread;
/// work from background threads is posted back through the dispatcher queue.
/// </summary>
public sealed class AppController : ISwitchboardActions, IAsyncDisposable
{
    private const int ShowHotkeyId = 1;
    private const int CycleHotkeyId = 2;
    private const int FirstInputHotkeyId = 100;

    private const int MenuOpen = 1;
    private const int MenuRefresh = 2;
    private const int MenuKeepOpen = 3;
    private const int MenuEditSettings = 4;
    private const int MenuOpenLog = 5;
    private const int MenuQuit = 9;

    private readonly DispatcherQueue _dispatcher;
    private readonly MonitorService _monitors = new();
    private readonly SettingsStore _store = new(AppPaths.SettingsFile);
    private readonly Dictionary<int, Func<Task>> _hotkeyActions = new();
    private readonly DispatcherQueueTimer _displayChangeTimer;
    private readonly DispatcherQueueTimer _settingsReloadTimer;
    private readonly DispatcherQueueTimer _statusClearTimer;
    private HostWindow? _host;
    private TrayIcon? _tray;
    private FlyoutWindow? _flyout;
    private FileSystemWatcher? _watcher;
    private AppSettings _settings = new();
    private string? _settingsProblem;
    private string? _hotkeyProblem;
    private DateTime _lastInputRefresh = DateTime.MinValue;

    public AppController(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _displayChangeTimer = MakeTimer(TimeSpan.FromSeconds(1.5), () => _ = RefreshAsync(enumerate: true));
        _settingsReloadTimer = MakeTimer(TimeSpan.FromMilliseconds(400), ReloadSettingsFromDisk);
        _statusClearTimer = MakeTimer(TimeSpan.FromSeconds(8), () =>
        {
            ViewModel.Status = StandingProblem() ?? "";
            _flyout?.FitToContent();
        });
        _monitors.Changed += () => _dispatcher.TryEnqueue(OnMonitorsChanged);
    }

    public MainViewModel ViewModel { get; } = new();

    public void Start()
    {
        Log.Info("Switchboard starting");
        var loaded = _store.Load();
        _settings = loaded.Settings;
        _settingsProblem = loaded.Problem;
        ViewModel.KeepOpen = _settings.KeepOpen;

        _host = new HostWindow();
        _host.TrayEvent += OnTrayEvent;
        _host.HotkeyPressed += id => { if (_hotkeyActions.TryGetValue(id, out var action)) _ = action(); };
        _host.DisplaysChanged += RestartDisplayTimer;
        _host.Resumed += RestartDisplayTimer;

        _tray = new TrayIcon(_host);
        _tray.Show();

        _flyout = new FlyoutWindow(this);
        RegisterHotkeys();
        WatchSettingsFile();
        ViewModel.Status = StandingProblem() ?? "Looking for monitors…";

        _ = RefreshAsync(enumerate: true);

        // First run: show the window once so it's clear where Switchboard went. After that it lives in the tray.
        if (_settings.KeepOpen || !loaded.FileExisted) ShowFlyout();
    }

    public void ShowFlyout()
    {
        _flyout?.ShowFlyout(_settings.KeepOpen, _settings.KeepOpenPosition);
        if (DateTime.UtcNow - _lastInputRefresh > TimeSpan.FromSeconds(2)) _ = RefreshAsync(enumerate: false);
    }

    public void ToggleFlyout()
    {
        if (_flyout is null) return;
        if (_flyout.IsShown && !_settings.KeepOpen) _flyout.HideFlyout();
        else if (!_flyout.RecentlyHidden) ShowFlyout();
    }

    public async Task RefreshAsync(bool enumerate)
    {
        ViewModel.IsRefreshing = true;
        try
        {
            _lastInputRefresh = DateTime.UtcNow;
            await _monitors.RefreshAsync(enumerate);
        }
        catch (Exception ex)
        {
            Log.Error("Refreshing monitors failed", ex);
            SetStatus("Something went wrong while looking for monitors. Try Refresh.");
        }
        finally
        {
            ViewModel.IsRefreshing = false;
        }
    }

    public void SetKeepOpen(bool keepOpen)
    {
        _settings.KeepOpen = keepOpen;
        ViewModel.KeepOpen = keepOpen;
        SaveSettings();
        if (keepOpen) ShowFlyout();
        _flyout?.ApplyKeepOpen(keepOpen);
    }

    public void SaveKeepOpenPosition(int x, int y)
    {
        _settings.KeepOpenPosition = new WindowPlacement { X = x, Y = y };
        SaveSettings();
    }

    public void ToggleShowHidden()
    {
        ViewModel.ShowHidden = !ViewModel.ShowHidden;
        RebuildViewModel();
    }

    public async Task SwitchAsync(string monitorId, uint value)
    {
        var vm = FindInput(monitorId, value);
        string monitorName = MonitorName(monitorId);
        string inputName = vm?.Name ?? Core.Ddc.InputCatalog.GetPortName(value);
        if (vm is { IsCurrent: true })
        {
            SetStatus($"{monitorName} is already showing {inputName}.");
            return;
        }
        if (vm is { IsBusy: true }) return;

        if (vm is not null) vm.IsBusy = true;
        try
        {
            var outcome = await _monitors.SwitchAsync(monitorId, value);
            SetStatus(outcome.Status switch
            {
                SwitchStatus.Confirmed => $"{monitorName} switched to {inputName}.",
                SwitchStatus.Unconfirmed => $"Switch sent to {monitorName}. It didn't confirm, so check the screen.",
                SwitchStatus.StillOnOtherInput => $"{monitorName} didn't switch. It still reports {DescribeInput(monitorId, outcome.ReportedInput)}.",
                SwitchStatus.MonitorGone => $"{monitorName} isn't connected any more. Try Refresh.",
                _ => $"{monitorName} didn't respond. Check that DDC/CI is on in its menu, then try again.",
            });
        }
        catch (Exception ex)
        {
            Log.Error("Switching failed", ex);
            SetStatus($"Something went wrong switching {monitorName}. Try again.");
        }
        finally
        {
            if (FindInput(monitorId, value) is { } current) current.IsBusy = false;
            if (vm is not null) vm.IsBusy = false;
            RebuildViewModel();
        }
    }

    public void RenameInput(string monitorId, uint value, string? name)
    {
        if (!_settings.Monitors.TryGetValue(monitorId, out var ms)) return;
        InputLayout.GetOrAdd(ms, value).Name = name;
        SaveSettings();
        RebuildViewModel();
    }

    public void ToggleInputHidden(string monitorId, uint value)
    {
        if (!_settings.Monitors.TryGetValue(monitorId, out var ms)) return;
        var input = InputLayout.GetOrAdd(ms, value);
        input.Hidden = !input.Hidden;
        SaveSettings();
        RebuildViewModel();
    }

    public void SetInputIcon(string monitorId, uint value, string icon)
    {
        if (!_settings.Monitors.TryGetValue(monitorId, out var ms)) return;
        InputLayout.GetOrAdd(ms, value).Icon = icon;
        SaveSettings();
        RebuildViewModel();
    }

    public void ToggleMonitorHidden(string monitorId)
    {
        if (!_settings.Monitors.TryGetValue(monitorId, out var ms)) return;
        ms.Hidden = !ms.Hidden;
        SaveSettings();
        RebuildViewModel();
    }

    public void Quit()
    {
        Log.Info("Switchboard quitting");
        _flyout?.CloseForGood();
        _tray?.Dispose();
        _host?.Dispose();
        _watcher?.Dispose();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    public async ValueTask DisposeAsync()
    {
        _tray?.Dispose();
        _host?.Dispose();
        _watcher?.Dispose();
        await _monitors.DisposeAsync();
    }

    private void OnMonitorsChanged()
    {
        if (SettingsStore.MergeDetected(_settings, _monitors.Monitors)) SaveSettings();
        if (ViewModel.Status == "Looking for monitors…") ViewModel.Status = StandingProblem() ?? "";
        RebuildViewModel();
    }

    private void RebuildViewModel()
    {
        var snapshots = _monitors.Monitors;
        int hiddenCount = 0;
        var shown = new List<(MonitorSnapshot Snapshot, MonitorSettings? Settings, IReadOnlyList<DisplayInput> Inputs)>();

        foreach (var m in snapshots)
        {
            _settings.Monitors.TryGetValue(m.Id, out var ms);
            var inputs = InputLayout.Build(m, ms);
            if (ms?.Hidden == true)
            {
                hiddenCount++;
                if (!ViewModel.ShowHidden) continue;
            }
            hiddenCount += inputs.Count(i => i.Hidden);
            shown.Add((m, ms, ViewModel.ShowHidden ? inputs : inputs.Where(i => !i.Hidden).ToList()));
        }

        var monitors = ViewModel.Monitors;
        for (int i = 0; i < shown.Count; i++)
        {
            var (snapshot, ms, inputs) = shown[i];
            var vm = monitors.FirstOrDefault(x => x.Id == snapshot.Id);
            if (vm is null)
            {
                vm = new MonitorViewModel(this, snapshot.Id);
                monitors.Insert(i, vm);
            }
            else if (monitors.IndexOf(vm) is int at && at != i)
            {
                monitors.Move(at, i);
            }

            vm.Name = string.IsNullOrWhiteSpace(ms?.Nickname) ? snapshot.Name : ms!.Nickname!;
            vm.IsHidden = ms?.Hidden == true;
            vm.Message = MonitorMessage(snapshot, inputs.Count);
            vm.SyncInputs(snapshot.Health is MonitorHealth.Unavailable ? [] : inputs);
        }
        while (monitors.Count > shown.Count) monitors.RemoveAt(monitors.Count - 1);

        ViewModel.HiddenCount = hiddenCount;
        ViewModel.NotifyEmptyChanged();
        _tray!.Tooltip = BuildTooltip(snapshots);
        _tray.Refresh();
        _flyout?.FitToContent();
    }

    private static string? MonitorMessage(MonitorSnapshot m, int inputCount) => m.Health switch
    {
        MonitorHealth.Unavailable => "Windows can't control this monitor.",
        MonitorHealth.NotResponding => "Not answering. Check that DDC/CI is on in the monitor's menu. Some monitors only answer over HDMI.",
        _ when inputCount == 0 && m.AdvertisedInputs is null && !m.CapabilitiesFailed => "Reading inputs…",
        _ when inputCount == 0 => "This monitor didn't list any inputs.",
        _ => null,
    };

    private string BuildTooltip(IReadOnlyList<MonitorSnapshot> snapshots)
    {
        var lines = new List<string> { "Switchboard" };
        foreach (var m in snapshots)
        {
            _settings.Monitors.TryGetValue(m.Id, out var ms);
            if (ms?.Hidden == true || m.CurrentInput is null) continue;
            var current = InputLayout.Build(m, ms).FirstOrDefault(i => i.IsCurrent);
            if (current is not null) lines.Add($"{(string.IsNullOrWhiteSpace(ms?.Nickname) ? m.Name : ms!.Nickname)}: {current.Name}");
        }
        return string.Join("\n", lines);
    }

    private void RegisterHotkeys()
    {
        if (_host is null) return;
        _host.UnregisterAllHotkeys();
        _hotkeyActions.Clear();
        var problems = new List<string>();

        void Add(int id, string? text, Func<Task> action)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!Hotkey.TryParse(text, out var hotkey, out var error))
            {
                problems.Add($"\"{text}\": {error}");
                return;
            }
            if (!_host.RegisterHotkey(id, (uint)hotkey.Modifiers, hotkey.VirtualKey))
            {
                problems.Add($"{hotkey} is already used by another app.");
                return;
            }
            _hotkeyActions[id] = action;
        }

        Add(ShowHotkeyId, _settings.ShowHotkey, () => { ShowFlyout(); return Task.CompletedTask; });
        Add(CycleHotkeyId, _settings.CycleHotkey, CycleAsync);

        int next = FirstInputHotkeyId;
        foreach (var (monitorId, ms) in _settings.Monitors)
        {
            foreach (var input in ms.Inputs)
            {
                uint value = input.Value;
                Add(next++, input.Hotkey, () => SwitchAsync(monitorId, value));
            }
        }

        _hotkeyProblem = problems.Count == 0 ? null : "Shortcut problem: " + string.Join(" ", problems);
        if (_hotkeyProblem is not null) Log.Warn(_hotkeyProblem);
    }

    private async Task CycleAsync()
    {
        var snapshots = _monitors.Monitors;
        MonitorSnapshot? target = null;
        if (!string.IsNullOrWhiteSpace(_settings.CycleMonitor))
            target = snapshots.FirstOrDefault(m => string.Equals(m.Id, _settings.CycleMonitor, StringComparison.OrdinalIgnoreCase));
        target ??= snapshots.FirstOrDefault(m => m.IsPrimary && m.Health == MonitorHealth.Responding)
            ?? snapshots.FirstOrDefault(m => m.Health == MonitorHealth.Responding);
        if (target is null)
        {
            SetStatus("No monitor is answering, so the cycle shortcut has nothing to switch.");
            return;
        }

        _settings.Monitors.TryGetValue(target.Id, out var ms);
        var next = InputLayout.Next(InputLayout.Build(target, ms).Where(i => !i.Hidden).ToList());
        if (next is not null) await SwitchAsync(target.Id, next.Value);
    }

    private void OnTrayEvent(int evt, int x, int y)
    {
        switch (evt)
        {
            case Win32.NIN_SELECT:
            case Win32.NIN_KEYSELECT:
                ToggleFlyout();
                break;
            case Win32.WM_CONTEXTMENU:
                ShowTrayMenu();
                break;
        }
    }

    private void ShowTrayMenu()
    {
        if (_tray is null) return;
        int chosen = _tray.ShowMenu(
        [
            (MenuOpen, "Open Switchboard", true),
            (MenuRefresh, "Refresh monitors", true),
            (MenuKeepOpen, _settings.KeepOpen ? "Stop keeping the window open" : "Keep the window open", true),
            (0, null, true),
            (MenuEditSettings, "Edit settings file", true),
            (MenuOpenLog, "Open log", File.Exists(Log.FilePath)),
            (0, null, true),
            (MenuQuit, "Quit", true),
        ]);

        switch (chosen)
        {
            case MenuOpen: ShowFlyout(); break;
            case MenuRefresh: _ = RefreshAsync(enumerate: true); break;
            case MenuKeepOpen: SetKeepOpen(!_settings.KeepOpen); break;
            case MenuEditSettings: EditSettingsFile(); break;
            case MenuOpenLog: OpenFile(Log.FilePath); break;
            case MenuQuit: Quit(); break;
        }
    }

    private void EditSettingsFile()
    {
        if (!File.Exists(_store.FilePath)) SaveSettings();
        OpenFile(_store.FilePath);
    }

    private static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No app is associated with .json or .log files.
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
    }

    private void SaveSettings()
    {
        if (!_store.Save(_settings) && _store.IsBroken)
            SetStatus(_settingsProblem ?? "Your settings file has a mistake. Changes aren't saved until it's fixed.");
        RegisterHotkeys();
    }

    private void WatchSettingsFile()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_store.FilePath)!);
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(_store.FilePath)!, Path.GetFileName(_store.FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler changed = (_, _) => _dispatcher.TryEnqueue(() => { _settingsReloadTimer.Stop(); _settingsReloadTimer.Start(); });
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Renamed += (_, _) => changed(null!, null!);
        }
        catch (Exception ex)
        {
            Log.Warn($"Can't watch the settings file: {ex.Message}");
        }
    }

    private void ReloadSettingsFromDisk()
    {
        // Ignore the change notification from our own save.
        if (DateTime.UtcNow - _store.LastSavedUtc < TimeSpan.FromSeconds(1.5)) return;

        var loaded = _store.Load();
        _settingsProblem = loaded.Problem;
        if (loaded.Problem is null)
        {
            _settings = loaded.Settings;
            ViewModel.KeepOpen = _settings.KeepOpen;
            _flyout?.ApplyKeepOpen(_settings.KeepOpen);
            if (SettingsStore.MergeDetected(_settings, _monitors.Monitors)) SaveSettings();
            else RegisterHotkeys();
            RebuildViewModel();
            SetStatus(StandingProblem() ?? "Settings reloaded.");
            Log.Info("Settings reloaded from disk");
        }
        else
        {
            SetStatus(loaded.Problem);
        }
    }

    private string? StandingProblem() => _settingsProblem ?? _hotkeyProblem;

    private void SetStatus(string text)
    {
        ViewModel.Status = text;
        _statusClearTimer.Stop();
        _statusClearTimer.Start();
        _flyout?.FitToContent();
    }

    private void RestartDisplayTimer()
    {
        _displayChangeTimer.Stop();
        _displayChangeTimer.Start();
    }

    private DispatcherQueueTimer MakeTimer(TimeSpan interval, Action action)
    {
        var timer = _dispatcher.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => action();
        return timer;
    }

    private InputViewModel? FindInput(string monitorId, uint value) =>
        ViewModel.Monitors.FirstOrDefault(m => m.Id == monitorId)?.Inputs.FirstOrDefault(i => i.Value == value);

    private string MonitorName(string monitorId)
    {
        if (_settings.Monitors.TryGetValue(monitorId, out var ms) && !string.IsNullOrWhiteSpace(ms.Nickname)) return ms.Nickname!;
        return _monitors.Monitors.FirstOrDefault(m => m.Id == monitorId)?.Name ?? "The monitor";
    }

    private string DescribeInput(string monitorId, uint? value)
    {
        if (value is null) return "another input";
        var vm = ViewModel.Monitors.FirstOrDefault(m => m.Id == monitorId)?.Inputs
            .FirstOrDefault(i => Core.Ddc.InputCatalog.Matches(value.Value, i.Value));
        return vm?.Name ?? Core.Ddc.InputCatalog.GetPortName(value.Value);
    }
}
