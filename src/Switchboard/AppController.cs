using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Switchboard.Core;
using Switchboard.Core.Ddc;
using Switchboard.Core.Hotkeys;
using Switchboard.Core.Monitors;
using Switchboard.Core.Settings;
using Switchboard.Native;
using Switchboard.ViewModels;

namespace Switchboard;

/// <summary>
/// Owns the services and keeps the view models in step with them. Everything here runs on the UI thread;
/// work from background threads is posted back through the dispatcher queue.
/// </summary>
public sealed class AppController : ISwitchboardActions, IAsyncDisposable
{
    private const int FirstHotkeyId = 1;
    private const int ProbeHotkeyId = 0x7FFF;

    private readonly DispatcherQueue _dispatcher;
    private readonly StartupOptions _options;
    private readonly MonitorService _monitors = new();
    private readonly SettingsStore _store = new(AppPaths.SettingsFile);
    private readonly StartupRegistration _startup = new();
    private readonly Dictionary<int, Func<Task>> _hotkeyActions = new();
    private readonly Dictionary<string, string> _shortcutProblems = new();
    private readonly DispatcherQueueTimer _displayChangeTimer;
    private readonly DispatcherQueueTimer _settingsReloadTimer;
    private readonly DispatcherQueueTimer _statusClearTimer;
    private HostWindow? _host;
    private MainWindow? _window;
    private SettingsWindow? _settingsWindow;
    private SetupWindow? _setupWindow;
    private FileSystemWatcher? _watcher;
    private AppSettings _settings = new();
    private string? _settingsProblem;
    private DateTime _lastInputRefresh = DateTime.MinValue;
    private bool _screensOffSwitchRunning;
    private int _hotkeySuspensions;

    public AppController(DispatcherQueue dispatcher, StartupOptions options)
    {
        _dispatcher = dispatcher;
        _options = options;
        _monitors.ReadOnly = options.NoSwitch;
        _displayChangeTimer = MakeTimer(TimeSpan.FromSeconds(1.5), () =>
        {
            // Re-finding monitors releases their handles; not while a screens-off switch is still sending.
            if (_screensOffSwitchRunning) RestartDisplayTimer();
            else _ = RefreshAsync(enumerate: true);
        });
        _settingsReloadTimer = MakeTimer(TimeSpan.FromMilliseconds(400), ReloadSettingsFromDisk);
        _statusClearTimer = MakeTimer(TimeSpan.FromSeconds(8), () =>
        {
            ViewModel.Status = StandingProblem() ?? "";
            _window?.FitToContent();
        });
        _monitors.Changed += () => _dispatcher.TryEnqueue(OnMonitorsChanged);
    }

    public MainViewModel ViewModel { get; } = new();

    /// <summary>The settings as they are now. Change them only through <see cref="Change"/>.</summary>
    public AppSettings Settings => _settings;

    /// <summary>Connected monitors, in the order settings list them.</summary>
    public IReadOnlyList<MonitorSnapshot> Monitors => SettingsEditor.OrderMonitors(_monitors.Monitors, _settings);

    /// <summary>Raised after settings or monitors change, so the settings and setup windows can update.</summary>
    public event Action? StateChanged;

    public bool NoSwitch => _options.NoSwitch;

    public ElementTheme Theme => SettingsEditor.NormalizeTheme(_settings.Theme) switch
    {
        SettingsEditor.ThemeLight => ElementTheme.Light,
        SettingsEditor.ThemeDark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public void Start()
    {
        Log.Info($"Switchboard {AppInfo.Version} starting{(AppPaths.IsCustomDataDirectory ? $" with data in {AppPaths.DataDirectory}" : "")}{(_options.NoSwitch ? ", switching off (--no-switch)" : "")}");
        var loaded = _store.Load();
        _settings = loaded.Settings;
        _settingsProblem = loaded.Problem;
        ViewModel.AlwaysOnTop = _settings.AlwaysOnTop;

        // Hidden helper window for global hotkeys, display changes and resume from sleep.
        _host = new HostWindow();
        _host.HotkeyPressed += id => { if (_hotkeyActions.TryGetValue(id, out var action)) _ = action(); };
        _host.DisplaysChanged += RestartDisplayTimer;
        _host.Resumed += RestartDisplayTimer;

        _window = new MainWindow(this);
        WindowChrome.ApplyTheme(_window, Theme);
        RegisterHotkeys();
        WatchSettingsFile();
        ViewModel.Status = StandingProblem() ?? "Looking for monitors…";

        _ = RefreshAsync(enumerate: true);
        ShowWindow();

        // First run: no settings file yet. Not when the file exists but has a mistake.
        string? open = _options.Open;
        if (!loaded.FileExisted && open is null) open = "setup";
        if (open == "setup") OpenSetup();
        else if (open?.StartsWith("settings", StringComparison.Ordinal) == true) OpenSettings(open.Contains(':') ? open[(open.IndexOf(':') + 1)..] : null);
    }

    /// <summary>Shows the window and brings it to the front (second launch, show shortcut).</summary>
    public void ShowWindow() => _window?.ShowWindow(_settings.AlwaysOnTop, _settings.WindowPosition);

    /// <summary>Re-reads current inputs when you come back to the window, in case they changed from the monitor's own buttons.</summary>
    public void OnWindowActivated()
    {
        if (DateTime.UtcNow - _lastInputRefresh > TimeSpan.FromSeconds(3)) _ = RefreshAsync(enumerate: false);
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

    // ---- Settings changes ----

    /// <summary>Applies a change from the settings or setup window: saves it, re-registers shortcuts, updates every window.</summary>
    public void Change(Action<AppSettings> edit)
    {
        var oldTheme = Theme;
        bool oldOnTop = _settings.AlwaysOnTop;
        edit(_settings);
        SaveSettings();
        if (Theme != oldTheme) ApplyThemeEverywhere();
        if (_settings.AlwaysOnTop != oldOnTop)
        {
            ViewModel.AlwaysOnTop = _settings.AlwaysOnTop;
            _window?.SetAlwaysOnTop(_settings.AlwaysOnTop);
        }
        RebuildViewModel();
    }

    public void SetAlwaysOnTop(bool alwaysOnTop) => Change(s => s.AlwaysOnTop = alwaysOnTop);

    public void SaveWindowPosition(int x, int y)
    {
        if (_settings.WindowPosition is { } p && p.X == x && p.Y == y) return;
        _settings.WindowPosition = new WindowPlacement { X = x, Y = y };
        SaveSettings();
    }

    public void ToggleShowHidden()
    {
        ViewModel.ShowHidden = !ViewModel.ShowHidden;
        RebuildViewModel();
    }

    public bool StartsWithWindows
    {
        get
        {
            try { return Environment.ProcessPath is { } exe && _startup.IsEnabled(exe); }
            catch (Exception ex) { Log.Warn($"Reading the startup entry failed: {ex.Message}"); return false; }
        }
    }

    /// <summary>Adds or removes the HKCU Run entry. Returns a message to show if it didn't work.</summary>
    public string? SetStartWithWindows(bool enabled)
    {
        try
        {
            if (enabled && Environment.ProcessPath is { } exe) _startup.Enable(exe);
            else _startup.Disable();
            Log.Info(enabled ? $"Start with Windows on: {Environment.ProcessPath}" : "Start with Windows off");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Changing start with Windows failed", ex);
            return "Windows didn't let Switchboard change its startup setting. Try again, or use Task Manager's Startup apps page.";
        }
    }

    public async Task SwitchAsync(string monitorId, uint value)
    {
        var vm = FindInput(monitorId, value);
        string monitorName = MonitorName(monitorId);
        string inputName = vm?.Name ?? InputCatalog.GetPortName(value);
        if (_options.NoSwitch)
        {
            SetStatus($"Didn't switch {monitorName}: Switchboard was started with --no-switch.");
            return;
        }
        _settings.Monitors.TryGetValue(monitorId, out var ms);
        bool screensOff = InputLayout.NeedsScreensOff(ms, value);
        // A monitor that needs the screens-off trick also reads back unreliably, so don't trust "already showing" there.
        if (vm is { IsCurrent: true } && ms?.BlankScreensWhenLeaving is null)
        {
            SetStatus($"{monitorName} is already showing {inputName}.");
            return;
        }
        if (vm is { IsBusy: true } || _screensOffSwitchRunning) return;

        if (vm is not null) vm.IsBusy = true;
        try
        {
            if (screensOff)
            {
                await SwitchWithScreensOffAsync(monitorId, value, monitorName, inputName);
                return;
            }

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

    /// <summary>
    /// For monitors that ignore switch commands while showing this PC (the MSI G27CQ4 over DisplayPort):
    /// turn every screen off so no picture goes down the cable, send the command a few times, wake the screens.
    /// Tested by hand on 2026-11-20 (scripts/quiet-link-test.ps1): the MSI switched to the PS5.
    /// </summary>
    private async Task SwitchWithScreensOffAsync(string monitorId, uint value, string monitorName, string inputName)
    {
        if (_host is null) return;
        _screensOffSwitchRunning = true;
        try
        {
            Log.Info($"{monitorName}: switching to {InputCatalog.FormatValue(value)} with the screens off");
            _host.TurnScreensOff();
            await Task.Delay(TimeSpan.FromSeconds(3));
            int accepted = await _monitors.SendRepeatedlyAsync(monitorId, value, 6, TimeSpan.FromMilliseconds(500));
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            HostWindow.WakeScreens();

            if (accepted == 0)
            {
                SetStatus($"{monitorName} didn't take the switch. Try again.");
                return;
            }
            // The monitor can't confirm over this link, so assume it worked; the screen shows whether it did.
            _monitors.AssumeInput(monitorId, value);
            SetStatus($"{monitorName} should now show {inputName}. The screens went dark for a moment to make it listen.");
        }
        finally
        {
            _screensOffSwitchRunning = false;
            // Turning screens off and on can look like a display change; take one fresh look once it's over.
            RestartDisplayTimer();
        }
    }

    public void ToggleBlankScreensWhenLeaving(string monitorId, uint value)
    {
        if (!_settings.Monitors.TryGetValue(monitorId, out var ms)) return;
        Change(_ => ms.BlankScreensWhenLeaving = ms.BlankScreensWhenLeaving == value ? null : value);
        SetStatus(ms.BlankScreensWhenLeaving is null
            ? $"{MonitorName(monitorId)} switches normally again."
            : $"Switching {MonitorName(monitorId)} away from {FindInput(monitorId, value)?.Name ?? InputCatalog.GetPortName(value)} now turns the screens off for a few seconds.");
    }

    /// <summary>Moves the "This PC" tag, for when the guess from the cable type was wrong.</summary>
    public void SetPcInput(string monitorId, uint value)
    {
        if (!_settings.Monitors.TryGetValue(monitorId, out var ms)) return;
        Change(_ => ms.PcInput = value);
        string inputName = FindInput(monitorId, value)?.Name ?? InputCatalog.GetPortName(value);
        string status = $"{MonitorName(monitorId)}: this PC is now shown on {inputName}.";
        // The screens-off input is meant to be the PC's; if it's elsewhere, say so rather than change it quietly.
        if (ms.BlankScreensWhenLeaving is uint blank && blank != value)
            status += $" Screens still turn off when leaving {FindInput(monitorId, blank)?.Name ?? InputCatalog.GetPortName(blank)}; change that from its menu if it's wrong too.";
        SetStatus(status);
    }

    public void RenameInput(string monitorId, uint value, string? name)
    {
        if (!_settings.Monitors.TryGetValue(monitorId, out var ms)) return;
        Change(_ => InputLayout.GetOrAdd(ms, value).Name = name);
    }

    public void ToggleInputHidden(string monitorId, uint value)
    {
        if (!_settings.Monitors.TryGetValue(monitorId, out var ms)) return;
        Change(_ =>
        {
            var input = InputLayout.GetOrAdd(ms, value);
            input.Hidden = !input.Hidden;
        });
    }

    public void SetInputIcon(string monitorId, uint value, string icon)
    {
        if (!_settings.Monitors.TryGetValue(monitorId, out var ms)) return;
        Change(_ => InputLayout.GetOrAdd(ms, value).Icon = icon);
    }

    public void ToggleMonitorHidden(string monitorId)
    {
        if (!_settings.Monitors.TryGetValue(monitorId, out var ms)) return;
        Change(_ => ms.Hidden = !ms.Hidden);
    }

    // ---- Other windows ----

    public void OpenSettings(string? page, string? monitorId = null)
    {
        if (_window is null) return;
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            WindowChrome.ApplyTheme(_settingsWindow, Theme);
            _settingsWindow.Place(_window.Display, WindowChrome.Scale(_window));
        }
        _settingsWindow.Navigate(page, monitorId);
        _settingsWindow.Activate();
    }

    public void OpenSetup()
    {
        if (_window is null) return;
        if (_setupWindow is null)
        {
            _setupWindow = new SetupWindow(this);
            _setupWindow.Closed += (_, _) =>
            {
                _setupWindow = null;
                // Skipping or finishing setup both leave a settings file, so it doesn't open again.
                if (!File.Exists(_store.FilePath)) SaveSettings();
            };
            WindowChrome.ApplyTheme(_setupWindow, Theme);
            _setupWindow.Place(_window.Display, WindowChrome.Scale(_window));
        }
        _setupWindow.Activate();
    }

    private void ApplyThemeEverywhere()
    {
        var theme = Theme;
        if (_window is not null) WindowChrome.ApplyTheme(_window, theme);
        if (_settingsWindow is not null) WindowChrome.ApplyTheme(_settingsWindow, theme);
        if (_setupWindow is not null) WindowChrome.ApplyTheme(_setupWindow, theme);
    }

    public void Quit()
    {
        Log.Info("Switchboard quitting");
        _setupWindow?.Close();
        _settingsWindow?.Close();
        _window?.CloseForGood();
        _host?.Dispose();
        _watcher?.Dispose();
        Application.Current.Exit();
    }

    public async ValueTask DisposeAsync()
    {
        _host?.Dispose();
        _watcher?.Dispose();
        await _monitors.DisposeAsync();
    }

    // ---- Diagnostics ----

    public void RereadCapabilities(string monitorId) => _monitors.RereadCapabilities(monitorId);

    /// <summary>Plain-text report of what Switchboard knows about one monitor (or all), for bug reports.</summary>
    public string Diagnostics(string? monitorId)
    {
        var sb = new StringBuilder();
        if (monitorId is null)
        {
            sb.AppendLine($"Switchboard {AppInfo.Version}, Windows {Environment.OSVersion.Version}");
            sb.AppendLine();
        }
        foreach (var m in Monitors.Where(m => monitorId is null || m.Id == monitorId))
        {
            sb.AppendLine($"{m.Name} [{m.Id}]{(m.IsPrimary ? ", main display" : "")}");
            sb.AppendLine($"  Connection: {SetupAdvisor.DescribeConnection(m.Connection) ?? "not reported"}");
            sb.AppendLine($"  Status: {SetupAdvisor.Describe(m)}");
            sb.AppendLine($"  Current input: {(m.CurrentInput is uint c ? InputCatalog.FormatValue(c) : "unknown")}");
            if (_settings.Monitors.TryGetValue(m.Id, out var ms) && ms.BlankScreensWhenLeaving is uint b)
                sb.AppendLine($"  Screens off when leaving: {InputCatalog.FormatValue(b)}");
            sb.AppendLine($"  Capabilities: {m.CapabilitiesText ?? (m.CapabilitiesFailed ? "(didn't read)" : "(not read yet)")}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    public void EditSettingsFile()
    {
        if (!File.Exists(_store.FilePath)) SaveSettings();
        OpenFile(_store.FilePath);
    }

    /// <summary>Opens the log. False if there's no log yet.</summary>
    public bool OpenLog()
    {
        if (!File.Exists(Log.FilePath)) return false;
        OpenFile(Log.FilePath);
        return true;
    }

    public void OpenDataFolder()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDirectory}\"") { UseShellExecute = true });
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

    // ---- Shortcuts ----

    /// <summary>What's wrong with a shortcut as it is in settings (taken by another app, duplicate, typo), or null.</summary>
    public string? ShortcutProblem(string slotKey) => _shortcutProblems.GetValueOrDefault(slotKey);

    /// <summary>"Switch to PS5 (MSI G27CQ4)" and the like, for conflict messages.</summary>
    public string DescribeSlot(string slotKey)
    {
        if (slotKey == ShortcutSlots.Show) return "Bring Switchboard to the front";
        if (slotKey == ShortcutSlots.Cycle) return "Switch to the next input";
        if (ShortcutSlots.TryParseInputKey(slotKey, out string id, out uint value))
            return $"Switch to {InputName(id, value)} ({MonitorName(id)})";
        return slotKey;
    }

    /// <summary>
    /// Stops global shortcuts while the shortcut recorder listens, so pressing one records it instead of switching
    /// a monitor. Every call needs a matching <see cref="ResumeHotkeys"/>.
    /// </summary>
    public void SuspendHotkeys()
    {
        if (_hotkeySuspensions++ == 0) _host?.UnregisterAllHotkeys();
    }

    public void ResumeHotkeys()
    {
        if (_hotkeySuspensions == 0) return;
        if (--_hotkeySuspensions == 0) RegisterHotkeys();
    }

    /// <summary>Sets a shortcut from the recorder. Returns why it can't be used, or null when it's saved.</summary>
    public string? AssignShortcut(string slotKey, Hotkey hotkey)
    {
        if (ShortcutSlots.FindOwner(_settings, hotkey, slotKey) is { } owner)
            return $"{hotkey} is already set for \"{DescribeSlot(owner)}\".";

        // Our own shortcuts are suspended while recording, so a failed test registration means another app has it.
        if (_host is not null && !ShortcutSlots.Get(_settings, slotKey).IsSameAs(hotkey))
        {
            if (!_host.RegisterHotkey(ProbeHotkeyId, (uint)hotkey.Modifiers, hotkey.VirtualKey))
                return $"Another app already uses {hotkey}. Try another shortcut.";
            _host.UnregisterHotkey(ProbeHotkeyId);
        }

        Change(s => ShortcutSlots.Set(s, slotKey, hotkey.ToString()));
        return null;
    }

    public void ClearShortcut(string slotKey) => Change(s => ShortcutSlots.Set(s, slotKey, null));

    private void RegisterHotkeys()
    {
        if (_host is null) return;
        _host.UnregisterAllHotkeys();
        _hotkeyActions.Clear();
        _shortcutProblems.Clear();
        var duplicates = ShortcutSlots.Duplicates(_settings);
        int id = FirstHotkeyId;

        foreach (var (key, text) in ShortcutSlots.All(_settings))
        {
            if (!Hotkey.TryParse(text, out var hotkey, out var error))
            {
                _shortcutProblems[key] = $"\"{text}\" isn't a shortcut Switchboard can use. {error}";
                continue;
            }
            if (duplicates.TryGetValue(key, out string? first))
            {
                _shortcutProblems[key] = $"Also set for \"{DescribeSlot(first)}\", so it only does that.";
                continue;
            }
            if (_hotkeySuspensions > 0) continue;
            if (!_host.RegisterHotkey(id, (uint)hotkey.Modifiers, hotkey.VirtualKey))
            {
                _shortcutProblems[key] = "Another app already uses this shortcut, so it doesn't work. Pick another.";
                continue;
            }
            _hotkeyActions[id++] = ActionFor(key);
        }

        if (_shortcutProblems.Count > 0)
            Log.Warn("Shortcut problems: " + string.Join(" ", _shortcutProblems.Select(p => $"[{p.Key}] {p.Value}")));
    }

    private Func<Task> ActionFor(string key)
    {
        if (key == ShortcutSlots.Show) return () => { ShowWindow(); return Task.CompletedTask; };
        if (key == ShortcutSlots.Cycle) return CycleAsync;
        ShortcutSlots.TryParseInputKey(key, out string monitorId, out uint value);
        return () => SwitchAsync(monitorId, value);
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

    // ---- Keeping everything in step ----

    private void OnMonitorsChanged()
    {
        if (MergeDetected()) SaveSettings();
        if (ViewModel.Status == "Looking for monitors…") ViewModel.Status = StandingProblem() ?? "";
        RebuildViewModel();
    }

    /// <summary>Adds newly seen monitors and inputs to settings and remembers each monitor's PC input. True if anything changed.</summary>
    private bool MergeDetected()
    {
        var snapshots = _monitors.Monitors;
        bool merged = SettingsStore.MergeDetected(_settings, snapshots);
        bool learned = PcInputDetector.Learn(_settings, snapshots);
        return merged || learned;
    }

    private void RebuildViewModel()
    {
        int hiddenCount = 0;
        var shown = new List<(MonitorSnapshot Snapshot, MonitorSettings? Settings, IReadOnlyList<DisplayInput> Inputs)>();

        foreach (var m in Monitors)
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
        _window?.FitToContent();
        StateChanged?.Invoke();
    }

    private static string? MonitorMessage(MonitorSnapshot m, int inputCount) => m.Health switch
    {
        MonitorHealth.Unavailable => "Windows can't control this monitor.",
        MonitorHealth.NotResponding => "Not answering. Check that DDC/CI is on in the monitor's menu. Some monitors only answer over HDMI.",
        _ when inputCount == 0 && m.AdvertisedInputs is null && !m.CapabilitiesFailed => "Reading inputs…",
        _ when inputCount == 0 => "This monitor didn't list any inputs. Add one in Settings.",
        _ => null,
    };

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
            var oldTheme = Theme;
            _settings = loaded.Settings;
            ViewModel.AlwaysOnTop = _settings.AlwaysOnTop;
            _window?.SetAlwaysOnTop(_settings.AlwaysOnTop);
            if (Theme != oldTheme) ApplyThemeEverywhere();
            if (MergeDetected()) SaveSettings();
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

    private string? StandingProblem() =>
        _settingsProblem ?? (_shortcutProblems.Count == 0 ? null : "A shortcut doesn't work. See Settings › Shortcuts.");

    private void SetStatus(string text)
    {
        ViewModel.Status = text;
        _statusClearTimer.Stop();
        _statusClearTimer.Start();
        _window?.FitToContent();
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

    public string MonitorName(string monitorId)
    {
        if (_settings.Monitors.TryGetValue(monitorId, out var ms) && !string.IsNullOrWhiteSpace(ms.Nickname)) return ms.Nickname!;
        return _monitors.Monitors.FirstOrDefault(m => m.Id == monitorId)?.Name ?? "The monitor";
    }

    private string InputName(string monitorId, uint value)
    {
        if (_settings.Monitors.TryGetValue(monitorId, out var ms)
            && ms.Inputs.FirstOrDefault(i => i.Value == value)?.Name is { } name
            && !string.IsNullOrWhiteSpace(name))
            return name.Trim();
        return InputCatalog.GetPortName(value);
    }

    private string DescribeInput(string monitorId, uint? value)
    {
        if (value is null) return "another input";
        var vm = ViewModel.Monitors.FirstOrDefault(m => m.Id == monitorId)?.Inputs
            .FirstOrDefault(i => InputCatalog.Matches(value.Value, i.Value));
        return vm?.Name ?? InputCatalog.GetPortName(value.Value);
    }
}

internal static class HotkeyExtensions
{
    public static bool IsSameAs(this string? text, Hotkey hotkey) => Hotkey.TryParse(text, out var parsed, out _) && parsed == hotkey;
}

public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
