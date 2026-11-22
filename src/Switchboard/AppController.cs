using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Switchboard.Core;
using Switchboard.Core.Actions;
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
public sealed partial class AppController : ISwitchboardActions, IAsyncDisposable
{
    private const int FirstHotkeyId = 1;
    private const int ProbeHotkeyId = 0x7FFF;

    private readonly DispatcherQueue _dispatcher;
    private readonly StartupOptions _options;
    private readonly MonitorService _monitors = new() { CapabilitiesFile = Path.Combine(AppPaths.DataDirectory, "capabilities.json") };
    private readonly SettingsStore _store = new(AppPaths.SettingsFile);
    private readonly StartupRegistration _startup = new();
    private readonly Updater _updater = new();
    private readonly Dictionary<int, Func<long, Task>> _hotkeyActions = new();
    private readonly Dictionary<string, string> _shortcutProblems = new();
    private readonly DispatcherQueueTimer _displayChangeTimer;
    private readonly DispatcherQueueTimer _settingsReloadTimer;
    private readonly DispatcherQueueTimer _statusClearTimer;
    private readonly DispatcherQueueTimer _activationRefreshTimer;
    private HostWindow? _host;
    private MainWindow? _window;
    private SettingsWindow? _settingsWindow;
    private SetupWindow? _setupWindow;
    private FileSystemWatcher? _watcher;
    private AppSettings _settings = new();
    private string? _settingsProblem;
    private DateTime _lastInputRefresh = DateTime.MinValue;
    private bool _screensOffSwitchRunning;

    /// <summary>
    /// How long after a screens-off switch the app keeps re-sending a switch on that monitor. On 2026-11-21 the MSI
    /// ignored switches back to the PC sent 1.0, 1.4 and 1.7 s after the screens came back, and took the first one
    /// sent at 4.4 s (display change at 7.0 s). The window is generous because the limit isn't known exactly.
    /// </summary>
    private static readonly TimeSpan ScreensOffSettle = TimeSpan.FromSeconds(25);

    /// <summary>
    /// How long after the screens come back another screens-off switch on the same monitor waits before starting.
    /// Its own sends come <see cref="ScreensOffWait"/> later still, so they land after the 4.4 s at which the MSI
    /// took a plain command.
    /// </summary>
    private static readonly TimeSpan ScreensOffRestart = TimeSpan.FromSeconds(2);

    // Screens-off switch timings. Measured on 2026-11-21 with scripts/quiet-link-test.ps1 from Windows PowerShell
    // (docs/plan.md, stage 1): first send at 2.75 s, 4 sends 250 ms apart, screens woken 0.5 s after the last
    // switched the MSI to the PS5 5 times in 5, with the screens dark for 4.3 s. Before: 3 s, 6 sends 500 ms apart,
    // 1.5 s, dark for 7.4 s. Don't go shorter without measuring: one send at 2.25 s with the screens back at 3.8 s
    // switched 1 time in 3.
    private static readonly TimeSpan ScreensOffWait = TimeSpan.FromSeconds(2.75);
    private const int ScreensOffSends = 4;
    private static readonly TimeSpan ScreensOffSendInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ScreensOffWakeDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Per monitor: when it's ready for commands again after a screens-off switch (UTC).</summary>
    private readonly Dictionary<string, DateTime> _settleUntil = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per monitor: the input to switch to once it has settled. A newer click replaces it.</summary>
    private readonly Dictionary<string, uint> _waitingTarget = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When Windows last reported a display change (UTC).</summary>
    private DateTime _lastDisplayChange = DateTime.MinValue;

    /// <summary>
    /// Per monitor: the input a switch is on its way to. The window shows it as current straight away, and keeps doing
    /// so until the monitor confirms it or the switch fails.
    /// </summary>
    private readonly Dictionary<string, uint> _pendingSwitch = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per monitor: where the mouse was before Switchboard moved it off that monitor (physical pixels).</summary>
    private readonly Dictionary<string, (int X, int Y)> _parkedMouse = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _lastSwitchStarted = DateTime.MinValue;

    /// <summary>
    /// How long the toggle trusts a remembered input. An older one is checked first (one read, about 60 ms), because
    /// the monitor may have changed input by itself (its own buttons, or moving on when a device goes quiet).
    /// </summary>
    private static readonly TimeSpan TrustReadingFor = TimeSpan.FromSeconds(30);

    /// <summary>A switch that takes longer than this shows a spinner on its row. Most are confirmed well before.</summary>
    private static readonly TimeSpan ShowBusyAfter = TimeSpan.FromMilliseconds(400);
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
        // Coming back to the window re-reads the inputs, but not while that would hold up a switch: a click on an
        // input usually comes right after the click that activated the window.
        _activationRefreshTimer = MakeTimer(TimeSpan.FromMilliseconds(300), () =>
        {
            if (_pendingSwitch.Count == 0 && DateTime.UtcNow - _lastSwitchStarted > TimeSpan.FromSeconds(1)) _ = RefreshAsync(enumerate: false);
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
        _host.HotkeyPressed += id =>
        {
            long pressedAt = Stopwatch.GetTimestamp();
            if (_hotkeyActions.TryGetValue(id, out var action)) _ = action(pressedAt);
        };
        _host.DisplaysChanged += () =>
        {
            _lastDisplayChange = DateTime.UtcNow;
            PutMouseBackAfterReplug();
            RestartDisplayTimer();
        };
        _host.Resumed += RestartDisplayTimer;

        _window = new MainWindow(this);
        WindowChrome.ApplyTheme(_window, Theme);
        RegisterHotkeys();
        WatchSettingsFile();
        StartFeatures();
        ViewModel.Status = StandingProblem() ?? "Looking for monitors…";

        _ = StartupRefreshAsync();
        ShowWindow();
        _ = CheckForUpdatesAsync(quiet: true);

        // First run: no settings file yet. Not when the file exists but has a mistake.
        string? open = _options.Open;
        if (!loaded.FileExisted && open is null) open = "setup";
        if (open == "setup") OpenSetup();
        else if (open?.StartsWith("settings", StringComparison.Ordinal) == true) OpenSettings(open.Contains(':') ? open[(open.IndexOf(':') + 1)..] : null);
    }

    // ---- Updates ----

    /// <summary>The result of the last update check.</summary>
    public UpdateCheck UpdateStatus => _updater.Last;

    public bool IsCheckingForUpdates { get; private set; }

    /// <summary>
    /// Looks for a new version. Quiet (at start and on Refresh): no status line, only the update button if one is found.
    /// </summary>
    public async Task CheckForUpdatesAsync(bool quiet)
    {
        if (IsCheckingForUpdates || ViewModel.IsUpdating) return;
        IsCheckingForUpdates = true;
        StateChanged?.Invoke();
        try
        {
            var result = await _updater.CheckAsync(_settings.UpdateSource);
            ViewModel.UpdateVersion = result.State == UpdateState.Available ? result.NewVersion : null;
            if (!quiet) SetStatus(result.Message);
        }
        finally
        {
            IsCheckingForUpdates = false;
            StateChanged?.Invoke();
            _window?.FitToContent();
        }
    }

    /// <summary>Downloads the update, then closes Switchboard, installs it and starts the new version.</summary>
    public async Task InstallUpdateAsync()
    {
        if (ViewModel.IsUpdating || ViewModel.UpdateVersion is not { } version) return;
        ViewModel.IsUpdating = true;
        StateChanged?.Invoke();
        SetStatus($"Getting version {version}…");
        string? problem = await _updater.InstallAsync(percent => _dispatcher.TryEnqueue(() => SetStatus($"Getting version {version}… {percent}%")));
        // When it works, Switchboard closes here and the new version starts.
        ViewModel.IsUpdating = false;
        StateChanged?.Invoke();
        if (problem is not null) SetStatus(problem);
    }

    /// <summary>Shows the window and brings it to the front (second launch, show shortcut).</summary>
    public void ShowWindow() => _window?.ShowWindow(_settings.AlwaysOnTop, _settings.WindowPosition);

    /// <summary>Re-reads current inputs when you come back to the window, in case they changed from the monitor's own buttons.</summary>
    public void OnWindowActivated()
    {
        if (DateTime.UtcNow - _lastInputRefresh <= TimeSpan.FromSeconds(3)) return;
        _activationRefreshTimer.Stop();
        _activationRefreshTimer.Start();
    }

    public async Task RefreshAsync(bool enumerate)
    {
        ViewModel.IsRefreshing = true;
        try
        {
            _lastInputRefresh = DateTime.UtcNow;
            // Taken here: the refresh runs on another thread, and settings belong to this one.
            var hidden = _settings.Monitors.Where(m => m.Value.Hidden).Select(m => m.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await _monitors.RefreshAsync(enumerate, hidden.Contains);
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

    /// <summary>A click on an input in the window.</summary>
    public Task SwitchAsync(string monitorId, uint value) => SwitchAsync(monitorId, value, "click", Stopwatch.GetTimestamp());

    /// <param name="trigger">"click" or the shortcut, for the log.</param>
    /// <param name="requestedAt"><see cref="Stopwatch.GetTimestamp"/> at the click or key press.</param>
    private async Task<SwitchStatus?> SwitchAsync(string monitorId, uint value, string trigger, long requestedAt)
    {
        _settings.Monitors.TryGetValue(monitorId, out var settings);
        if (_options.NoSwitch || !_settleUntil.TryGetValue(monitorId, out var until) || DateTime.UtcNow >= until
            || (InputLayout.NeedsScreensOff(settings, value) && DateTime.UtcNow >= until - ScreensOffSettle + ScreensOffRestart))
        {
            return await SwitchNowAsync(monitorId, value, trigger, requestedAt);
        }

        // Shortly after a screens-off switch the monitor ignores commands for a few seconds (the MSI: ignored at 1.0,
        // 1.4 and 1.7 s, took one at 4.4 s). A plain command it ignores does no harm, so keep sending one every couple
        // of seconds: it switches as soon as it's ready. Every switch that worked was followed by a display change
        // (Windows sees the link come back), so that's the sign to stop. Targets that need the screens-off sequence
        // wait a moment and then run it. Another click in the meantime changes the target instead of starting a second loop.
        bool alreadyWaiting = _waitingTarget.ContainsKey(monitorId);
        _waitingTarget[monitorId] = value;
        if (alreadyWaiting) return null;

        string monitorName = MonitorName(monitorId);
        DateTime woke = until - ScreensOffSettle;
        Log.Info($"{monitorName}: {(DateTime.UtcNow - woke).TotalSeconds:0.0} s after the screens came back, {(InputLayout.NeedsScreensOff(settings, value) ? "holding" : "sending")} {InputCatalog.FormatValue(value)}");
        uint shown = value;
        uint? sent = null;
        DateTime firstSend = DateTime.MaxValue;
        bool tookIt = false;
        try
        {
            while (DateTime.UtcNow < until)
            {
                uint target = _waitingTarget[monitorId];
                if (target != shown)
                {
                    if (FindInput(monitorId, shown) is { } old) old.IsBusy = false;
                    sent = null;
                    firstSend = DateTime.MaxValue;
                }
                shown = target;
                if (FindInput(monitorId, target) is { } row) row.IsBusy = true;
                string name = FindInput(monitorId, target)?.Name ?? InputCatalog.GetPortName(target);
                _settings.Monitors.TryGetValue(monitorId, out var ms);

                if (InputLayout.NeedsScreensOff(ms, target))
                {
                    TimeSpan left = woke + ScreensOffRestart - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) break;
                    SetStatus($"{monitorName} needs a moment after the last switch. Switching to {name} in {(int)Math.Ceiling(left.TotalSeconds)} s.");
                    await Task.Delay(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1));
                    continue;
                }

                // A display change from the screens waking up comes in the first few seconds; only count later ones.
                if (sent == target && _lastDisplayChange > firstSend.AddSeconds(0.5) && _lastDisplayChange > woke.AddSeconds(5))
                {
                    tookIt = true;
                    // It's listening again, so the next switch needn't wait.
                    _settleUntil.Remove(monitorId);
                    Log.Info($"{monitorName}: display change {(_lastDisplayChange - woke).TotalSeconds:0.0} s after the screens came back; taking that as switched to {InputCatalog.FormatValue(target)}");
                    break;
                }

                SetStatus($"Switching {monitorName} to {name}. Right after a switch it takes a few seconds to listen.");
                await _monitors.SendRepeatedlyAsync(monitorId, target, 1, TimeSpan.Zero);
                if (sent != target) firstSend = DateTime.UtcNow;
                sent = target;
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
        finally
        {
            value = _waitingTarget[monitorId];
            _waitingTarget.Remove(monitorId);
            if (FindInput(monitorId, shown) is { } row) row.IsBusy = false;
        }

        if (sent == value)
        {
            // Sent right through the window (or it took it): reads over this link can't confirm, so assume it worked.
            if (!tookIt) Log.Info($"{monitorName}: no display change while sending {InputCatalog.FormatValue(value)}; it may not have switched");
            _monitors.AssumeInput(monitorId, value);
            string name = FindInput(monitorId, value)?.Name ?? InputCatalog.GetPortName(value);
            SetStatus(tookIt ? $"{monitorName} switched to {name}." : $"{monitorName} should now show {name}. If it doesn't, click it again.");
            RebuildViewModel();
            return tookIt ? SwitchStatus.Confirmed : SwitchStatus.Unconfirmed;
        }
        return await SwitchNowAsync(monitorId, value, trigger, requestedAt);
    }

    private async Task<SwitchStatus?> SwitchNowAsync(string monitorId, uint value, string trigger, long requestedAt)
    {
        var vm = FindInput(monitorId, value);
        string monitorName = MonitorName(monitorId);
        string inputName = vm?.Name ?? InputCatalog.GetPortName(value);
        if (_options.NoSwitch)
        {
            SetStatus($"Didn't switch {monitorName}: Switchboard was started with --no-switch.");
            return SwitchStatus.Failed;
        }
        _settings.Monitors.TryGetValue(monitorId, out var ms);
        if (_screensOffSwitchRunning) return null;
        if (InputLayout.NeedsScreensOff(ms, value))
        {
            if (vm is { IsBusy: true }) return null;
            MoveMouseForSwitch(monitorId, value, ms);
            if (vm is not null) vm.IsBusy = true;
            try
            {
                await SwitchWithScreensOffAsync(monitorId, value, monitorName, inputName);
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
            return SwitchStatus.Unconfirmed;
        }

        bool pending = _pendingSwitch.TryGetValue(monitorId, out uint pendingTarget);
        if (pending && pendingTarget == value) return null;

        // "Already showing" from memory can be out of date (the monitor's own buttons, or it moving on by itself when
        // a device goes quiet), so ask the monitor before saying so. Only on this path: a real switch doesn't wait for a
        // read. Not for a monitor that needs the screens-off trick: it reads back unreliably.
        uint? remembered = _monitors.Monitors.FirstOrDefault(m => m.Id == monitorId)?.CurrentInput;
        if (!pending && remembered is uint r && InputCatalog.Matches(r, value) && ms?.BlankScreensWhenLeaving is null)
        {
            uint? now = await _monitors.ReadCurrentInputAsync(monitorId);
            if (now is null || InputCatalog.Matches(now.Value, value))
            {
                // No answer: trust memory rather than send a command that may make the screen blink.
                SetStatus($"{monitorName} is already showing {inputName}.");
                return SwitchStatus.Confirmed;
            }
            Log.Info($"{monitorName}: shows {InputCatalog.FormatValue(now.Value)}, not {InputCatalog.FormatValue(value)} as remembered; switching");
            if (_pendingSwitch.ContainsKey(monitorId)) return null; // another switch started during the read
        }

        _pendingSwitch[monitorId] = value;
        _lastSwitchStarted = DateTime.UtcNow;
        // The command goes out first: SwitchAsync has handed it to the monitor's queue by the time it returns its task.
        // The window, the mouse and the log all come after.
        var switching = _monitors.SwitchAsync(monitorId, value, trigger, requestedAt);
        var mouse = MoveMouseForSwitch(monitorId, value, ms);
        ToastForSwitch(monitorId, value, trigger);
        foreach (var row in InputsOf(monitorId)) row.IsBusy = false;
        if (ViewModel.Status.Length > 0) ViewModel.Status = StandingProblem() ?? "";
        RebuildViewModel();
        _ = ShowBusyIfSlowAsync(monitorId, value);

        SwitchOutcome outcome;
        try
        {
            outcome = await switching;
        }
        catch (Exception ex)
        {
            Log.Error("Switching failed", ex);
            outcome = new SwitchOutcome(SwitchStatus.Failed, null);
        }

        // Right after a display change the monitor's handle may be dead (Windows destroys it when the monitor unplugs
        // itself, as the MSI does for a moment after switching back to the PC), and the monitors are only found again
        // 1.5 s after the last change. A switch in that gap finds them now and tries once more.
        if (outcome.Status is SwitchStatus.Failed or SwitchStatus.MonitorGone
            && DateTime.UtcNow - _lastDisplayChange < TimeSpan.FromSeconds(10)
            && _pendingSwitch.TryGetValue(monitorId, out uint retrying) && retrying == value)
        {
            Log.Info($"{monitorName}: switch failed {(DateTime.UtcNow - _lastDisplayChange).TotalSeconds:0.0} s after a display change; finding monitors again and retrying");
            try
            {
                await RefreshAsync(enumerate: true);
                if (_pendingSwitch.TryGetValue(monitorId, out retrying) && retrying == value)
                    outcome = await _monitors.SwitchAsync(monitorId, value, trigger + ", retry", requestedAt);
            }
            catch (Exception ex)
            {
                Log.Error("Switching failed on the retry", ex);
            }
        }

        // A newer switch on this monitor took over; it updates the window.
        if (outcome.Status == SwitchStatus.Superseded || !_pendingSwitch.TryGetValue(monitorId, out uint stillPending) || stillPending != value) return SwitchStatus.Superseded;
        _pendingSwitch.Remove(monitorId);
        if (FindInput(monitorId, value) is { } done) done.IsBusy = false;
        if (outcome.Status is SwitchStatus.Failed or SwitchStatus.MonitorGone) PutMouseBack(mouse);
        RebuildViewModel();

        if (outcome.Status == SwitchStatus.Confirmed)
        {
            // The highlight has already moved there: no status line for news the window shows. Narrator still hears it.
            _window?.Announce($"{monitorName} switched to {inputName}.");
            return SwitchStatus.Confirmed;
        }
        SetStatus(outcome.Status switch
        {
            SwitchStatus.Unconfirmed => $"Switch sent to {monitorName}. It didn't confirm, so check the screen.",
            SwitchStatus.StillOnOtherInput => $"{monitorName} didn't switch. It still reports {DescribeInput(monitorId, outcome.ReportedInput)}.",
            SwitchStatus.MonitorGone => $"{monitorName} isn't connected any more. Try Refresh.",
            _ => $"{monitorName} didn't respond. Check that DDC/CI is on in its menu, then try again.",
        });
        return outcome.Status;
    }

    private async Task ShowBusyIfSlowAsync(string monitorId, uint value)
    {
        await Task.Delay(ShowBusyAfter);
        if (_pendingSwitch.TryGetValue(monitorId, out uint target) && target == value && FindInput(monitorId, value) is { } row)
            row.IsBusy = true;
    }

    // ---- The mouse ----

    private sealed record MouseMove(string MonitorId, int FromX, int FromY, bool Parked);

    /// <summary>
    /// Leaving this PC's input with the mouse on that monitor: moves the mouse to Switchboard's window (or another
    /// screen), since Windows still counts the monitor as there and the pointer would be lost on it. Coming back to this
    /// PC's input: puts it back where it was. Does nothing for monitors whose PC input isn't known.
    /// </summary>
    private MouseMove? MoveMouseForSwitch(string monitorId, uint target, MonitorSettings? ms)
    {
        try
        {
            if (_settings.LeaveMouseAlone || ms?.PcInput is not uint pcInput) return null;
            var snapshots = _monitors.Monitors;
            var monitor = snapshots.FirstOrDefault(m => m.Id == monitorId);
            if (monitor is null || !Win32.GetCursorPos(out var at)) return null;
            bool onMonitor = MouseParking.Contains(monitor.Bounds, (at.X, at.Y));

            if (InputCatalog.Matches(target, pcInput))
            {
                if (!_parkedMouse.Remove(monitorId, out var saved) || onMonitor || !MouseParking.Contains(monitor.Bounds, saved)) return null;
                Win32.SetCursorPos(saved.X, saved.Y);
                _mouseAfterReplug = (monitor.Name, monitor.Bounds, saved.X, saved.Y, DateTime.UtcNow + MouseAfterReplugFor);
                Log.Info($"{monitor.Name}: mouse back at {saved.X},{saved.Y}");
                return new MouseMove(monitorId, at.X, at.Y, Parked: false);
            }

            if (!onMonitor) return null;
            var spot = MouseParking.Spot(monitor.Bounds, snapshots.Select(m => (m.Bounds, m.IsPrimary)).ToList(), _window?.ScreenBounds);
            if (spot is not { } to) return null;
            _parkedMouse[monitorId] = (at.X, at.Y);
            _mouseAfterReplug = null;
            Win32.SetCursorPos(to.X, to.Y);
            Log.Info($"{monitor.Name}: mouse moved off it, from {at.X},{at.Y} to {to.X},{to.Y}");
            return new MouseMove(monitorId, at.X, at.Y, Parked: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Moving the mouse failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>After a switch that didn't happen: the mouse goes back on the monitor it was moved off.</summary>
    private void PutMouseBack(MouseMove? move)
    {
        if (move is not { Parked: true }) return;
        _parkedMouse.Remove(move.MonitorId);
        Win32.SetCursorPos(move.FromX, move.FromY);
    }

    /// <summary>
    /// After a switch back to this PC: where the mouse belongs, and until when. The MSI G27CQ4 over HDMI unplugs itself
    /// from the PC for about 0.8 s, half a second after it switches to the PC's input (measured 2026-11-21), and Windows
    /// moves the mouse onto another screen meanwhile. When the monitor is back, the mouse goes back again.
    /// </summary>
    private (string Name, ScreenRect Monitor, int X, int Y, DateTime Until)? _mouseAfterReplug;

    private static readonly TimeSpan MouseAfterReplugFor = TimeSpan.FromSeconds(10);

    /// <summary>On a display change: puts the mouse back if Windows moved it off the monitor that just came back.</summary>
    private void PutMouseBackAfterReplug()
    {
        if (_mouseAfterReplug is not { } r) return;
        if (DateTime.UtcNow > r.Until)
        {
            _mouseAfterReplug = null;
            return;
        }
        // Not while the monitor is gone (the spot is on no screen), and not if the mouse is still on it.
        if (!Win32.IsOnAScreen(r.X, r.Y) || !Win32.GetCursorPos(out var at) || MouseParking.Contains(r.Monitor, (at.X, at.Y))) return;
        Win32.SetCursorPos(r.X, r.Y);
        _mouseAfterReplug = null;
        Log.Info($"{r.Name}: came back after a display change; mouse back at {r.X},{r.Y} (Windows had moved it to {at.X},{at.Y})");
    }

    /// <summary>
    /// For monitors that ignore switch commands while showing this PC (the MSI G27CQ4 over DisplayPort):
    /// turn every screen off so no picture goes down the cable, send the command a few times, wake the screens.
    /// Tested by hand on 2026-11-20 (scripts/quiet-link-test.ps1): the MSI switched to the PS5. Timings: see
    /// <see cref="ScreensOffWait"/>.
    /// </summary>
    private async Task SwitchWithScreensOffAsync(string monitorId, uint value, string monitorName, string inputName)
    {
        if (_host is null) return;
        _screensOffSwitchRunning = true;
        try
        {
            Log.Info($"{monitorName}: switching to {InputCatalog.FormatValue(value)} with the screens off");
            _host.TurnScreensOff();
            await Task.Delay(ScreensOffWait);
            int accepted = await _monitors.SendRepeatedlyAsync(monitorId, value, ScreensOffSends, ScreensOffSendInterval);
            await Task.Delay(ScreensOffWakeDelay);
            HostWindow.WakeScreens();
            _settleUntil[monitorId] = DateTime.UtcNow + ScreensOffSettle;

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
            // No forced re-find of the monitors afterwards: in the 2026-11-21 test rounds the switch caused no display
            // change and the handles kept working. A re-find would read the MSI right after the screens come back,
            // often get a garbled answer and clear the input just assumed. A display change during the switch still
            // leads to one: the display timer keeps re-arming while this runs and fires once it's over.
            _screensOffSwitchRunning = false;
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
        ApplyThemeToPopups(theme);
    }

    public void Quit()
    {
        Log.Info("InputFlip quitting");
        _setupWindow?.Close();
        _settingsWindow?.Close();
        StopFeatures();
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
        if (slotKey == ShortcutSlots.Show) return "Bring InputFlip to the front";
        if (slotKey == ShortcutSlots.Cycle) return "Switch to the next input";
        if (slotKey == ShortcutSlots.Picker) return "Open the pop-up picker";
        if (ShortcutSlots.TryParseSceneKey(slotKey, out string sceneId))
            return $"Run scene {(ScenePlanner.Find(_settings, sceneId) is { } scene ? ScenePlanner.DisplayName(scene) : sceneId)}";
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
                _shortcutProblems[key] = $"\"{text}\" isn't a shortcut InputFlip can use. {error}";
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
            _hotkeyActions[id++] = ActionFor(key, hotkey.ToString());
        }

        if (_shortcutProblems.Count > 0)
            Log.Warn("Shortcut problems: " + string.Join(" ", _shortcutProblems.Select(p => $"[{p.Key}] {p.Value}")));
    }

    private Func<long, Task> ActionFor(string key, string shortcut)
    {
        if (key == ShortcutSlots.Show) return _ => { ShowWindow(); return Task.CompletedTask; };
        if (key == ShortcutSlots.Cycle) return pressedAt => CycleAsync(shortcut, pressedAt);
        if (key == ShortcutSlots.Picker) return _ => { OpenPicker(); return Task.CompletedTask; };
        if (ShortcutSlots.TryParseSceneKey(key, out string sceneId)) return pressedAt => RunSceneAsync(sceneId, shortcut, pressedAt);
        ShortcutSlots.TryParseInputKey(key, out string monitorId, out uint value);
        return pressedAt => SwitchAsync(monitorId, value, shortcut, pressedAt);
    }

    /// <summary>The next-input shortcut. With two inputs shown (the PC and the PS5) it flips between them.</summary>
    /// <param name="monitorId">The monitor to toggle (the command line's --toggle MSI); null: the one set for the shortcut.</param>
    private async Task<SwitchStatus?> CycleAsync(string shortcut, long pressedAt, string? monitorId = null)
    {
        var monitors = Monitors;
        MonitorSnapshot? target;
        if (monitorId is not null)
        {
            target = monitors.FirstOrDefault(m => m.Id == monitorId);
            if (target is null) return SwitchStatus.MonitorGone;
        }
        else if (!string.IsNullOrWhiteSpace(_settings.CycleMonitor))
        {
            target = monitors.FirstOrDefault(m => string.Equals(m.Id, _settings.CycleMonitor, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                SetStatus($"{MonitorName(_settings.CycleMonitor)} isn't connected, so the next-input shortcut has nothing to switch.");
                return SwitchStatus.MonitorGone;
            }
        }
        else
        {
            target = InputLayout.PickCycleMonitor(monitors, _settings);
            if (target is null)
            {
                SetStatus("No monitor shown here has two inputs to switch between, so the next-input shortcut has nothing to do.");
                return null;
            }
        }

        _settings.Monitors.TryGetValue(target.Id, out var ms);
        uint? current = _pendingSwitch.TryGetValue(target.Id, out uint pending) ? pending : target.CurrentInput;
        // A reading that's a while old is checked first (about 60 ms), so the toggle doesn't send the input that's
        // already showing when the monitor changed input by itself.
        if (!_pendingSwitch.ContainsKey(target.Id) && ms?.BlankScreensWhenLeaving is null && DateTime.UtcNow - target.InputReadUtc > TrustReadingFor)
        {
            uint? read = await _monitors.ReadCurrentInputAsync(target.Id);
            if (read is uint now && now != current)
                Log.Info($"{target.Name}: shows {InputCatalog.FormatValue(now)}, not {(current is uint c ? InputCatalog.FormatValue(c) : "an unknown input")} as remembered");
            current = read ?? current;
        }
        var visible = InputLayout.Build(target with { CurrentInput = current }, ms).Where(i => !i.Hidden).ToList();
        var next = InputLayout.Next(visible);
        return next is null ? null : await SwitchAsync(target.Id, next.Value, shortcut, pressedAt);
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

        foreach (var monitor in Monitors)
        {
            // A switch on its way shows as done; the monitor confirms it a moment later.
            var m = _pendingSwitch.TryGetValue(monitor.Id, out uint target) ? monitor with { CurrentInput = target } : monitor;
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
            SyncLevelFlags(vm, snapshot);
        }
        while (monitors.Count > shown.Count) monitors.RemoveAt(monitors.Count - 1);
        SyncScenes();

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

    private IEnumerable<InputViewModel> InputsOf(string monitorId) =>
        ViewModel.Monitors.FirstOrDefault(m => m.Id == monitorId)?.Inputs ?? Enumerable.Empty<InputViewModel>();

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
