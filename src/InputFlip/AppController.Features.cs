using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using InputFlip.Core;
using InputFlip.Core.Actions;
using InputFlip.Core.Ddc;
using InputFlip.Core.Monitors;
using InputFlip.Core.Settings;
using InputFlip.Core.Usb;
using InputFlip.Native;
using InputFlip.ViewModels;

namespace InputFlip;

/// <summary>
/// Stage 4 features: scenes, the command line and links, the tray icon, the pop-up picker and the toast, USB and
/// lock/sleep rules, and brightness, contrast and volume.
/// </summary>
public sealed partial class AppController
{
    private readonly UsbRuleEngine _usbRules = new();
    private readonly Dictionary<string, RestorePoint> _sceneRestore = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Monitor, byte Code), LatestValueSender> _levelSenders = new();
    private readonly Dictionary<(string Monitor, byte Code), uint> _levelMaximum = new();
    private RestorePoint? _ruleRestore;
    private CommandPipeHost? _commandHost;
    private ToastWindow? _toast;
    private PickerWindow? _picker;

    /// <summary>A command from the command line that started this copy (nothing was running yet).</summary>
    public static RemoteCommand? StartupCommand { get; set; }

    private void StartFeatures()
    {
        if (_host is null) return;
        _host.TrayInvoked += ShowWindow;
        _host.TrayMenuRequested += ShowTrayMenu;
        _host.SessionLocked += () => RunPowerRule(_settings.PowerRules?.OnLock, "the PC locked");
        _host.SessionUnlocked += () => RunPowerRule(_settings.PowerRules?.OnUnlock, "the PC unlocked");
        _host.Suspending += OnSuspending;
        _host.Resumed += OnResumedForRules;
        _host.UsbDeviceChanged += OnUsbDeviceChanged;
        _host.WatchSessionAndDevices();
        SyncTrayIcon();

        _commandHost = new CommandPipeHost(App.CommandPipeName, _dispatcher, RunCommandLineAsync);
        _commandHost.Start();
        if (!AppPaths.IsCustomDataDirectory && Environment.ProcessPath is { } exe)
        {
            try
            {
                var links = new LinkRegistration();
                if (!links.IsRegistered(exe)) links.Register(exe);
                if (_startup.MigrateLegacy(exe)) Log.Info($"Start with Windows: moved the entry from before the rename to {exe}");
            }
            catch (Exception ex)
            {
                Log.Warn($"Registering inputflip:// links failed: {ex.Message}");
            }
        }
    }

    private void StopFeatures()
    {
        _commandHost?.Dispose();
        _toast?.CloseForGood();
        _picker?.CloseForGood();
    }

    private void ApplyThemeToPopups(ElementTheme theme)
    {
        if (_toast is not null) WindowChrome.ApplyTheme(_toast, theme);
        if (_picker is not null) WindowChrome.ApplyTheme(_picker, theme);
    }

    /// <summary>The first look at the monitors; then a command the app was started with, if any.</summary>
    private async Task StartupRefreshAsync()
    {
        await RefreshAsync(enumerate: true);
        if (StartupCommand is { } command)
        {
            StartupCommand = null;
            var reply = await RunCommandAsync(command);
            Log.Info($"Start-up command {string.Join(' ', command.ToArgs())}: {reply.Message}");
            if (!reply.Ok) SetStatus(reply.Message);
        }
    }

    // ---- Closing ----

    /// <summary>The window's close button: quits, or with "close to the tray" just hides the window.</summary>
    public void OnWindowClosing()
    {
        if (_settings.CloseToTray && _host?.HasTrayIcon == true)
        {
            _window?.HideWindow();
            return;
        }
        Quit();
    }

    // ---- Scenes ----

    /// <summary>A click on a scene in the window.</summary>
    public Task RunSceneAsync(string sceneId) => RunSceneAsync(sceneId, "click", Stopwatch.GetTimestamp());

    /// <summary>
    /// Runs a scene. If it's showing already, goes back to what showed before it (when that's known), so the same key
    /// or pill switches there and back.
    /// </summary>
    private async Task<CommandPipe.Reply> RunSceneAsync(string sceneId, string trigger, long requestedAt)
    {
        if (ScenePlanner.Find(_settings, sceneId) is not { } scene)
        {
            SetStatus("That scene doesn't exist any more. See Settings › Scenes.");
            return new CommandPipe.Reply(false, $"There's no scene called \"{sceneId}\".");
        }
        string name = ScenePlanner.DisplayName(scene);
        var monitors = EffectiveMonitors();

        if (ScenePlanner.IsActive(scene, monitors) && _sceneRestore.TryGetValue(scene.Id, out var before))
        {
            var back = ScenePlanner.PlanBack(before, monitors);
            if (back.Count > 0)
            {
                _sceneRestore.Remove(scene.Id);
                Log.Info($"Scene {name} ({trigger}): showing already, going back");
                ShowToast($"Back from {name}", DescribeSteps(back), Ui.SceneGlyph, back);
                await RunStepsAsync(back, $"scene {name}, back", requestedAt);
                return new CommandPipe.Reply(true, $"Went back from {name}.");
            }
        }

        var plan = ScenePlanner.Plan(scene, monitors);
        var point = ScenePlanner.Remember(scene.Id, plan.Steps, monitors);
        if (point.Inputs.Count > 0) _sceneRestore[scene.Id] = point;
        Log.Info($"Scene {name} ({trigger}): {plan.Steps.Count} step(s){(plan.MissingMonitors.Count > 0 ? $", not connected: {string.Join(", ", plan.MissingMonitors)}" : "")}");
        if (plan.Steps.Count == 0)
        {
            string why = plan.MissingMonitors.Count > 0 ? "none of its monitors is connected" : "it has nothing to do yet";
            SetStatus($"{name} didn't run: {why}. See Settings › Scenes.");
            return new CommandPipe.Reply(false, $"{name} didn't run: {why}.");
        }
        ShowToast(name, DescribeSteps(plan.Steps), IconFor(scene), plan.Steps);
        var results = await RunStepsAsync(plan.Steps, $"scene {name}", requestedAt);

        if (plan.MissingMonitors.Count > 0)
            SetStatus($"Ran {name}. Not connected: {string.Join(", ", plan.MissingMonitors.Select(MonitorName))}.");
        bool ok = results.All(r => r is null or SwitchStatus.Confirmed or SwitchStatus.Unconfirmed or SwitchStatus.Superseded);
        return new CommandPipe.Reply(ok, ok ? $"Ran {name}." : $"Ran {name}, but a monitor didn't switch. {ViewModel.Status}".Trim());
    }

    /// <summary>
    /// Switches and brightness for several monitors. Every switch command goes out first (each monitor has its own
    /// queue), then brightness levels, which can wait.
    /// </summary>
    private async Task<IReadOnlyList<SwitchStatus?>> RunStepsAsync(IReadOnlyList<PlannedStep> steps, string trigger, long requestedAt)
    {
        var switches = steps.Where(s => s.Input is not null).Select(s => SwitchAsync(s.MonitorId, s.Input!.Value, trigger, requestedAt)).ToList();
        foreach (var step in steps.Where(s => s.Brightness is not null))
            await SetLevelNowAsync(step.MonitorId, VcpCodes.Brightness, step.Brightness!.Value);
        return await Task.WhenAll(switches);
    }

    /// <summary>Monitors as the window shows them: a switch on its way counts as done.</summary>
    private IReadOnlyList<MonitorSnapshot> EffectiveMonitors() =>
        Monitors.Select(m => _pendingSwitch.TryGetValue(m.Id, out uint target) ? m with { CurrentInput = target } : m).ToList();

    private string DescribeSteps(IEnumerable<PlannedStep> steps) => string.Join(", ", steps.Select(s =>
        s.Input is uint input
            ? $"{MonitorName(s.MonitorId)} → {InputName(s.MonitorId, input)}"
            : $"{MonitorName(s.MonitorId)} at {s.Brightness}%"));

    private static string IconFor(SceneSettings scene) =>
        scene.Icon is not null && InputLayout.Icons.Contains(scene.Icon) ? Ui.IconGlyph(scene.Icon.ToLowerInvariant()) : Ui.SceneGlyph;

    private void SyncScenes()
    {
        var monitors = EffectiveMonitors();
        var scenes = _settings.Scenes.Where(s => !string.IsNullOrWhiteSpace(s.Id)).ToList();
        SettingsViewModel.SyncList(ViewModel.Scenes, scenes, (vm, s) => vm.Id == s.Id, s => new SceneViewModel(this, s.Id), (vm, s) =>
        {
            vm.Name = ScenePlanner.DisplayName(s);
            vm.Icon = s.Icon is not null && InputLayout.Icons.Contains(s.Icon) ? s.Icon.ToLowerInvariant() : "scene";
            vm.Hotkey = string.IsNullOrWhiteSpace(s.Hotkey) ? null : s.Hotkey;
            vm.IsActive = ScenePlanner.IsActive(s, monitors);
        });
        ViewModel.HasScenes = ViewModel.Scenes.Count > 0;
        SyncTrayIcon();
    }

    /// <summary>"Save what's showing now as a scene" (Settings › Scenes). Returns the new scene's id.</summary>
    public string AddSceneFromCurrent(string name)
    {
        var scene = ScenePlanner.FromCurrent(string.IsNullOrWhiteSpace(name) ? "New scene" : name, Monitors, _settings);
        Change(s => s.Scenes.Add(scene));
        return scene.Id;
    }

    // ---- Rules: USB, lock, sleep ----

    /// <summary>
    /// Runs a rule's action. Scenes and switches from rules remember what showed before, so a later "back" rule (the
    /// USB switch box moving back, unlocking the PC) can put it back.
    /// </summary>
    private async Task RunRuleActionAsync(AppAction action, string trigger)
    {
        long at = Stopwatch.GetTimestamp();
        var monitors = EffectiveMonitors();
        switch (action.Kind)
        {
            case AppActionKind.Scene when ScenePlanner.Find(_settings, action.SceneId) is { } scene:
                var plan = ScenePlanner.Plan(scene, monitors);
                _ruleRestore = ScenePlanner.Remember(null, plan.Steps, monitors);
                Log.Info($"Rule ({trigger}): scene {ScenePlanner.DisplayName(scene)}");
                ShowToast(ScenePlanner.DisplayName(scene), DescribeSteps(plan.Steps), IconFor(scene), plan.Steps);
                await RunStepsAsync(plan.Steps, $"scene {ScenePlanner.DisplayName(scene)}, rule: {trigger}", at);
                break;
            case AppActionKind.Switch:
                var step = new PlannedStep(action.MonitorId!, action.Input, null);
                _ruleRestore = ScenePlanner.Remember(null, [step], monitors);
                Log.Info($"Rule ({trigger}): {action}");
                await SwitchAsync(step.MonitorId, action.Input, $"rule: {trigger}", at);
                break;
            case AppActionKind.Back when _ruleRestore is { } point:
                var back = ScenePlanner.PlanBack(point, monitors);
                _ruleRestore = null;
                Log.Info($"Rule ({trigger}): back, {back.Count} monitor(s) to put back");
                if (back.Count == 0) break;
                ShowToast("Back", DescribeSteps(back), Ui.SceneGlyph, back);
                await RunStepsAsync(back, $"scene back, rule: {trigger}", at);
                break;
            case AppActionKind.Back:
                Log.Info($"Rule ({trigger}): back, but no rule has switched anything yet");
                break;
            default:
                Log.Warn($"Rule ({trigger}): {action} names a scene that doesn't exist");
                break;
        }
    }

    private void RunPowerRule(string? text, string trigger)
    {
        if (AppAction.Parse(text) is not { } action) return;
        _ = RunRuleActionAsync(action, trigger);
    }

    private void OnSuspending()
    {
        if (AppAction.Parse(_settings.PowerRules?.OnSleep) is not { } action) return;
        _ = RunRuleActionAsync(action, "the PC went to sleep");
        // The commands go out on background threads; give them a moment before Windows suspends. Not longer: Windows
        // only waits a couple of seconds for apps here.
        Thread.Sleep(400);
    }

    private void OnResumedForRules()
    {
        if (AppAction.Parse(_settings.PowerRules?.OnWake) is not { } action) return;
        _ = RunAfterWakeAsync(action);
    }

    private async Task RunAfterWakeAsync(AppAction action)
    {
        // Monitors come back a few seconds after the PC wakes, and their old handles are gone.
        await Task.Delay(TimeSpan.FromSeconds(3));
        await RefreshAsync(enumerate: true);
        await RunRuleActionAsync(action, "the PC woke up");
    }

    private void OnUsbDeviceChanged(string path, bool connected)
    {
        if (_settings.UsbRules.Count == 0) return;
        var actions = _usbRules.OnDeviceChange(_settings.UsbRules, path, connected, DateTime.UtcNow);
        foreach (var action in actions)
            _ = RunRuleActionAsync(action, $"USB device {(connected ? "plugged in" : "unplugged")}: {UsbDeviceId.TryParse(path)}");
    }

    // ---- Toast ----

    /// <summary>The short note after a switch that didn't come from a click in the window (which shows it anyway).</summary>
    private void ToastForSwitch(string monitorId, uint value, string trigger)
    {
        // Scenes show one note for all their monitors.
        if (trigger == "click" || trigger.StartsWith("scene", StringComparison.Ordinal)) return;
        var input = FindInput(monitorId, value);
        ShowToast(InputName(monitorId, value), MonitorName(monitorId), Ui.IconGlyph(input?.Icon ?? "display"), [new PlannedStep(monitorId, value, null)]);
    }

    private void ShowToast(string title, string detail, string glyph, IEnumerable<PlannedStep> steps)
    {
        if (_settings.HideToasts || _window is null) return;
        try
        {
            var snapshots = _monitors.Monitors;
            var switched = steps.Where(s => s.Input is not null)
                .Select(s => snapshots.FirstOrDefault(m => m.Id == s.MonitorId)?.Bounds).OfType<ScreenRect>().ToList();
            var screens = snapshots.Select(m => (m.Bounds, m.IsPrimary)).ToList();
            (int X, int Y)? mouse = Win32.GetCursorPos(out var p) ? (p.X, p.Y) : null;
            if (ToastPlacement.PickScreen(screens, switched, mouse, _window.ScreenBounds) is not { } screen) return;
            if (_toast is null)
            {
                _toast = new ToastWindow();
                WindowChrome.ApplyTheme(_toast, Theme);
            }
            _toast.Show(title, detail, glyph, screen);
        }
        catch (Exception ex)
        {
            Log.Warn($"Showing the toast failed: {ex.Message}");
        }
    }

    // ---- Picker ----

    /// <summary>The pop-up picker next to the mouse: scenes and every shown input, keyboard first.</summary>
    public void OpenPicker()
    {
        try
        {
            var items = PickerItems.Build(EffectiveMonitors(), _settings);
            if (items.Count == 0)
            {
                ShowWindow();
                return;
            }
            if (_picker is null)
            {
                _picker = new PickerWindow(item => _ = RunPickedAsync(item));
                WindowChrome.ApplyTheme(_picker, Theme);
            }
            (int X, int Y) at = Win32.GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);
            _picker.Open(items, PickerItems.InitialSelection(items), at);
        }
        catch (Exception ex)
        {
            Log.Error("Opening the picker failed", ex);
            ShowWindow();
        }
    }

    private Task RunPickedAsync(PickerItem item)
    {
        long at = Stopwatch.GetTimestamp();
        return item.Action.Kind switch
        {
            AppActionKind.Scene => RunSceneAsync(item.Action.SceneId!, "picker", at),
            AppActionKind.Switch => SwitchAsync(item.Action.MonitorId!, item.Action.Input, "picker", at),
            _ => Task.CompletedTask,
        };
    }

    // ---- Tray icon ----

    private void SyncTrayIcon()
    {
        if (_host is null) return;
        if (_settings.HideTrayIcon) _host.HideTrayIcon();
        else if (!_host.HasTrayIcon) _host.ShowTrayIcon(WindowChrome.IconPath, "InputFlip");
    }

    private void ShowTrayMenu(int x, int y)
    {
        const int MenuOpen = 1, MenuPicker = 2, MenuSettings = 3, MenuQuit = 4, First = 100;
        var actions = new Dictionary<int, Func<Task>>();
        var entries = new List<MenuEntry>();
        int next = First;
        long at = Stopwatch.GetTimestamp();

        var items = PickerItems.Build(EffectiveMonitors(), _settings);
        foreach (var scene in items.Where(i => i.Group == "Scenes"))
        {
            int id = next++;
            var action = scene.Action;
            actions[id] = () => RunSceneAsync(action.SceneId!, "tray", at);
            entries.Add(new MenuEntry(id, scene.Label, scene.IsCurrent));
        }
        if (entries.Count > 0) entries.Add(MenuEntry.Separator);
        foreach (var group in items.Where(i => i.Group != "Scenes").GroupBy(i => i.Group))
        {
            var children = new List<MenuEntry>();
            foreach (var input in group)
            {
                int id = next++;
                var action = input.Action;
                actions[id] = () => SwitchAsync(action.MonitorId!, action.Input, "tray", at);
                children.Add(new MenuEntry(id, input.Label, input.IsCurrent));
            }
            entries.Add(new MenuEntry(-1, group.Key, Children: children));
        }
        if (entries.Count > 0 && entries[^1] != MenuEntry.Separator) entries.Add(MenuEntry.Separator);
        entries.Add(new MenuEntry(MenuOpen, "Open InputFlip"));
        entries.Add(new MenuEntry(MenuPicker, "Pop-up picker"));
        entries.Add(new MenuEntry(MenuSettings, "Settings"));
        entries.Add(MenuEntry.Separator);
        entries.Add(new MenuEntry(MenuQuit, "Quit InputFlip"));

        int chosen = _host!.ShowMenu(entries, x, y);
        switch (chosen)
        {
            case MenuOpen: ShowWindow(); break;
            case MenuPicker: OpenPicker(); break;
            case MenuSettings: OpenSettings(null); break;
            case MenuQuit: Quit(); break;
            default:
                if (actions.TryGetValue(chosen, out var run)) _ = run();
                break;
        }
    }

    // ---- Brightness, contrast, volume ----

    private void SyncLevelFlags(MonitorViewModel vm, MonitorSnapshot snapshot)
    {
        vm.HasBrightness = MonitorFeatures.Offers(snapshot, VcpCodes.Brightness);
        vm.HasContrast = MonitorFeatures.Offers(snapshot, VcpCodes.Contrast);
        vm.HasVolume = MonitorFeatures.Offers(snapshot, VcpCodes.SpeakerVolume);
        vm.HasLevels = vm.HasBrightness || vm.HasContrast || vm.HasVolume;
    }

    public async void LoadLevels(string monitorId)
    {
        var vm = ViewModel.Monitors.FirstOrDefault(m => m.Id == monitorId);
        if (vm is null) return;
        vm.LevelsMessage = "Reading from the monitor…";
        bool any = false;
        try
        {
            foreach (var (code, has) in new[] { (VcpCodes.Brightness, vm.HasBrightness), (VcpCodes.Contrast, vm.HasContrast), (VcpCodes.SpeakerVolume, vm.HasVolume) })
            {
                if (!has) continue;
                var read = await _monitors.ReadVcpAsync(monitorId, code);
                if (read is not { } r) continue;
                any = true;
                _levelMaximum[(monitorId, code)] = r.Maximum;
                int percent = VcpScale.ToPercent(r.Current, r.Maximum);
                vm.SyncingLevels = true;
                try
                {
                    if (code == VcpCodes.Brightness) vm.Brightness = percent;
                    else if (code == VcpCodes.Contrast) vm.Contrast = percent;
                    else vm.Volume = percent;
                }
                finally { vm.SyncingLevels = false; }
            }
            if (vm.HasVolume && await _monitors.ReadVcpAsync(monitorId, VcpCodes.AudioMute) is { } mute)
            {
                vm.SyncingLevels = true;
                try { vm.IsMuted = mute.Current == VcpCodes.Muted; }
                finally { vm.SyncingLevels = false; }
            }
            vm.LevelsMessage = any ? null : "The monitor didn't answer. Check that DDC/CI is on in its menu, then close and open this again.";
        }
        catch (Exception ex)
        {
            Log.Error("Reading brightness and volume failed", ex);
            vm.LevelsMessage = "Something went wrong reading the monitor. Try again.";
        }
    }

    public void SetLevel(string monitorId, byte vcpCode, int percent)
    {
        if (_options.NoSwitch) return;
        if (!_levelSenders.TryGetValue((monitorId, vcpCode), out var sender))
        {
            sender = new LatestValueSender(raw => _monitors.SetVcpAsync(monitorId, vcpCode, raw));
            _levelSenders[(monitorId, vcpCode)] = sender;
        }
        uint max = _levelMaximum.GetValueOrDefault((monitorId, vcpCode), 100u);
        sender.Submit(VcpScale.FromPercent(percent, max));
    }

    public void SetMuted(string monitorId, bool muted) =>
        _ = _monitors.SetVcpAsync(monitorId, VcpCodes.AudioMute, muted ? VcpCodes.Muted : VcpCodes.NotMuted);

    /// <summary>Sets a level and waits for it (scenes, the command line). Reads the monitor's maximum first if it isn't known.</summary>
    private async Task<bool> SetLevelNowAsync(string monitorId, byte code, int percent)
    {
        if (!_levelMaximum.TryGetValue((monitorId, code), out uint max))
        {
            max = (await _monitors.ReadVcpAsync(monitorId, code))?.Maximum ?? 100;
            _levelMaximum[(monitorId, code)] = max;
        }
        bool ok = await _monitors.SetVcpAsync(monitorId, code, VcpScale.FromPercent(percent, max));
        Log.Info($"{MonitorName(monitorId)}: VCP 0x{code:X2} to {percent}% {(ok ? "sent" : "failed")}");
        return ok;
    }

    // ---- Command line and links ----

    private Task<CommandPipe.Reply> RunCommandLineAsync(IReadOnlyList<string> args)
    {
        var command = RemoteCommand.FromArgs(args, out string? problem);
        if (command is null) return Task.FromResult(new CommandPipe.Reply(false, problem ?? "That isn't an InputFlip command. Try --list."));
        return RunCommandAsync(command);
    }

    /// <summary>Runs a command from the command line or a link and says how it went, in one line for scripts.</summary>
    public async Task<CommandPipe.Reply> RunCommandAsync(RemoteCommand command)
    {
        long at = Stopwatch.GetTimestamp();
        var monitors = Monitors;
        string trigger = "command";
        switch (command.Verb)
        {
            case CommandVerb.Show:
                ShowWindow();
                return new CommandPipe.Reply(true, "InputFlip is in front.");
            case CommandVerb.Picker:
                OpenPicker();
                return new CommandPipe.Reply(true, "Picker open.");
            case CommandVerb.List:
                return new CommandPipe.Reply(true, ListForCommandLine());
            case CommandVerb.Scene:
                if (ScenePlanner.Find(_settings, command.Target) is not { } scene)
                    return new CommandPipe.Reply(false, $"There's no scene called \"{command.Target}\". Scenes: {SceneNames()}.");
                return await RunSceneAsync(scene.Id, trigger, at);
            case CommandVerb.Switch:
            {
                if (TargetResolver.ResolveMonitor(command.Monitor, monitors, _settings, out string? problem) is not { } monitor)
                    return new CommandPipe.Reply(false, problem!);
                _settings.Monitors.TryGetValue(monitor.Id, out var ms);
                if (TargetResolver.ResolveInput(command.Target, monitor, ms, out problem) is not uint input)
                    return new CommandPipe.Reply(false, problem!);
                var status = await SwitchAsync(monitor.Id, input, trigger, at);
                return SwitchReply(monitor.Id, input, status);
            }
            case CommandVerb.Toggle:
            {
                string? id = null;
                if (command.Monitor is not null)
                {
                    if (TargetResolver.ResolveMonitor(command.Monitor, monitors, _settings, out string? problem) is not { } monitor)
                        return new CommandPipe.Reply(false, problem!);
                    id = monitor.Id;
                }
                var status = await CycleAsync(trigger, at, id);
                return status is null or SwitchStatus.Confirmed or SwitchStatus.Unconfirmed or SwitchStatus.Superseded
                    ? new CommandPipe.Reply(status is not null, status is null ? "Nothing to toggle: no monitor has two inputs shown." : "Toggled.")
                    : new CommandPipe.Reply(false, ViewModel.Status.Length > 0 ? ViewModel.Status : "The monitor didn't switch.");
            }
            case CommandVerb.Brightness or CommandVerb.Contrast or CommandVerb.Volume:
                return await LevelCommandAsync(command, monitors);
            case CommandVerb.Mute or CommandVerb.Unmute:
            {
                if (TargetResolver.ResolveMonitor(command.Monitor, monitors, _settings, out string? problem) is not { } monitor)
                    return new CommandPipe.Reply(false, problem!);
                bool mute = command.Verb == CommandVerb.Mute;
                bool ok = !_options.NoSwitch && await _monitors.SetVcpAsync(monitor.Id, VcpCodes.AudioMute, mute ? VcpCodes.Muted : VcpCodes.NotMuted);
                return new CommandPipe.Reply(ok, ok ? $"{MonitorName(monitor.Id)} {(mute ? "muted" : "unmuted")}." : $"{MonitorName(monitor.Id)} didn't take it. Not every monitor has speakers.");
            }
            default:
                return new CommandPipe.Reply(false, "InputFlip doesn't know that command.");
        }
    }

    private CommandPipe.Reply SwitchReply(string monitorId, uint input, SwitchStatus? status)
    {
        string name = $"{MonitorName(monitorId)} → {InputName(monitorId, input)}";
        return status switch
        {
            SwitchStatus.Confirmed or null => new CommandPipe.Reply(true, $"{name}."),
            SwitchStatus.Unconfirmed => new CommandPipe.Reply(true, $"{name} sent; the monitor didn't confirm."),
            SwitchStatus.Superseded => new CommandPipe.Reply(true, $"{name} replaced by a newer switch."),
            _ => new CommandPipe.Reply(false, ViewModel.Status.Length > 0 ? ViewModel.Status : $"{name} didn't work."),
        };
    }

    private async Task<CommandPipe.Reply> LevelCommandAsync(RemoteCommand command, IReadOnlyList<MonitorSnapshot> monitors)
    {
        byte code = command.Verb switch { CommandVerb.Contrast => VcpCodes.Contrast, CommandVerb.Volume => VcpCodes.SpeakerVolume, _ => VcpCodes.Brightness };
        string what = command.Verb.ToString().ToLowerInvariant();
        List<MonitorSnapshot> targets;
        if (string.Equals(command.Monitor, "all", StringComparison.OrdinalIgnoreCase))
        {
            targets = monitors.Where(m => MonitorFeatures.Offers(m, code)
                && !(_settings.Monitors.TryGetValue(m.Id, out var ms) && ms.Hidden)).ToList();
            if (targets.Count == 0) return new CommandPipe.Reply(false, $"No monitor here offers {what}.");
        }
        else
        {
            if (TargetResolver.ResolveMonitor(command.Monitor, monitors, _settings, out string? problem) is not { } monitor)
                return new CommandPipe.Reply(false, problem!);
            targets = [monitor];
        }
        if (_options.NoSwitch) return new CommandPipe.Reply(false, "InputFlip was started with --no-switch, so it sends no commands.");

        var done = new List<string>();
        var failed = new List<string>();
        foreach (var m in targets)
        {
            var level = command.Level!.Value;
            int percent = level.Amount;
            if (level.Relative)
            {
                if (await _monitors.ReadVcpAsync(m.Id, code) is not { } now)
                {
                    failed.Add(MonitorName(m.Id));
                    continue;
                }
                _levelMaximum[(m.Id, code)] = now.Maximum;
                percent = level.Apply(VcpScale.ToPercent(now.Current, now.Maximum));
            }
            if (await SetLevelNowAsync(m.Id, code, percent)) done.Add($"{MonitorName(m.Id)} {percent}%");
            else failed.Add(MonitorName(m.Id));
        }
        string message = done.Count > 0 ? $"{char.ToUpperInvariant(what[0])}{what[1..]}: {string.Join(", ", done)}." : "";
        if (failed.Count > 0) message += $" Didn't answer: {string.Join(", ", failed)}.";
        return new CommandPipe.Reply(failed.Count == 0, message.Trim());
    }

    private string SceneNames() => _settings.Scenes.Count == 0 ? "none yet" : string.Join(", ", _settings.Scenes.Select(ScenePlanner.DisplayName));

    /// <summary>What --list prints: monitors with their numbers, inputs and scenes, so a script can be written from it.</summary>
    private string ListForCommandLine()
    {
        var sb = new StringBuilder();
        var monitors = Monitors;
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            _settings.Monitors.TryGetValue(m.Id, out var ms);
            sb.AppendLine($"{i + 1}. {MonitorName(m.Id)} [{m.Id}]{(m.IsPrimary ? ", main" : "")}{(ms?.Hidden == true ? ", hidden" : "")}");
            foreach (var input in InputLayout.Build(m, ms))
            {
                string extra = input.Name == input.PortName ? "" : $" ({input.PortName})";
                sb.AppendLine($"   {InputCatalog.FormatValue(input.Value)}  {input.Name}{extra}{(input.IsCurrent ? "  <- showing" : "")}{(input.Hidden ? "  hidden" : "")}");
            }
        }
        if (_settings.Scenes.Count > 0)
        {
            sb.AppendLine("Scenes:");
            foreach (var s in _settings.Scenes) sb.AppendLine($"   {s.Id}  {ScenePlanner.DisplayName(s)}");
        }
        return sb.ToString().TrimEnd();
    }
}
