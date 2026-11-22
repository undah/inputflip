using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InputFlip.Core;
using InputFlip.Core.Actions;
using InputFlip.Core.Ddc;
using InputFlip.Core.Hotkeys;
using InputFlip.Core.Interop;
using InputFlip.Core.Monitors;
using InputFlip.Core.Settings;
using InputFlip.Core.Usb;

namespace InputFlip.ViewModels;

/// <summary>Settings for the stage 4 features: tray and toast (General), scenes, rules, the picker and scene shortcuts.</summary>
public sealed partial class SettingsViewModel
{
    private bool _showTrayIcon, _closeToTray, _showToasts;
    private bool _hasScenes, _hasUsbRules;
    private int _onLockIndex, _onUnlockIndex, _onSleepIndex, _onWakeIndex;
    private List<string?> _actionValues = [];
    private List<UsbDeviceInfo> _usbDevicesNow = [];

    public ShortcutItemViewModel PickerShortcut { get; private set; } = null!;

    public ObservableCollection<ShortcutItemViewModel> SceneShortcuts { get; } = new();

    public ObservableCollection<SceneEditViewModel> Scenes { get; } = new();

    public ObservableCollection<UsbRuleEditViewModel> UsbRules { get; } = new();

    /// <summary>What a rule can do, for every rule's drop-downs: nothing, go back, a scene, or one input.</summary>
    public ObservableCollection<string> ActionLabels { get; } = new();

    public IRelayCommand AddSceneCommand { get; private set; } = null!;

    public IRelayCommand SaveCurrentAsSceneCommand { get; private set; } = null!;

    public IRelayCommand AddUsbRuleCommand { get; private set; } = null!;

    public IRelayCommand RefreshUsbDevicesCommand { get; private set; } = null!;

    internal IReadOnlyList<UsbDeviceInfo> UsbDevicesNow => _usbDevicesNow;

    private void InitFeatures()
    {
        PickerShortcut = new ShortcutItemViewModel(_app, ShortcutSlots.Picker, "Open the pop-up picker",
            "A small list of scenes and inputs next to the mouse. Arrow keys and Enter, or 1 to 9.", "");
        AddSceneCommand = new RelayCommand(() => _app.Change(s => s.Scenes.Add(new SceneSettings { Id = ScenePlanner.MakeId("New scene", s.Scenes), Name = "New scene" })));
        SaveCurrentAsSceneCommand = new RelayCommand(() => _app.AddSceneFromCurrent("What's showing now"));
        AddUsbRuleCommand = new RelayCommand(AddUsbRule);
        RefreshUsbDevicesCommand = new RelayCommand(() =>
        {
            LoadUsbDevices();
            Sync();
        });
        LoadUsbDevices();
    }

    private void LoadUsbDevices()
    {
        try
        {
            _usbDevicesNow = UsbDevices.ListPresent().ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Listing USB devices failed: {ex.Message}");
            _usbDevicesNow = [];
        }
    }

    private void AddUsbRule()
    {
        // Start with a device that's plugged in and not in a rule yet, if there is one.
        var used = _app.Settings.UsbRules.Select(r => UsbDeviceId.TryParse(r.Device)?.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var device = _usbDevicesNow.FirstOrDefault(d => !used.Contains(d.Id.ToString())) ?? _usbDevicesNow.FirstOrDefault();
        _app.Change(s => s.UsbRules.Add(new UsbRule { Device = device?.Id.ToString() ?? "", Name = device?.Name }));
    }

    // ---- General ----

    public bool ShowTrayIcon
    {
        get => _showTrayIcon;
        set
        {
            if (SetProperty(ref _showTrayIcon, value) && !Syncing) _app.Change(s => s.HideTrayIcon = !value);
        }
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (SetProperty(ref _closeToTray, value) && !Syncing) _app.Change(s => s.CloseToTray = value);
        }
    }

    public bool ShowToasts
    {
        get => _showToasts;
        set
        {
            if (SetProperty(ref _showToasts, value) && !Syncing) _app.Change(s => s.HideToasts = !value);
        }
    }

    // ---- Scenes and rules ----

    public bool HasScenes { get => _hasScenes; private set => SetProperty(ref _hasScenes, value); }

    public bool HasUsbRules { get => _hasUsbRules; private set => SetProperty(ref _hasUsbRules, value); }

    public int OnLockIndex { get => _onLockIndex; set => SetPowerRule(ref _onLockIndex, value, (r, a) => r.OnLock = a); }

    public int OnUnlockIndex { get => _onUnlockIndex; set => SetPowerRule(ref _onUnlockIndex, value, (r, a) => r.OnUnlock = a); }

    public int OnSleepIndex { get => _onSleepIndex; set => SetPowerRule(ref _onSleepIndex, value, (r, a) => r.OnSleep = a); }

    public int OnWakeIndex { get => _onWakeIndex; set => SetPowerRule(ref _onWakeIndex, value, (r, a) => r.OnWake = a); }

    private void SetPowerRule(ref int field, int value, Action<PowerRules, string?> set, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (value < 0 || value >= _actionValues.Count || field == value) return;
        field = value;
        OnPropertyChanged(name);
        if (Syncing) return;
        string? action = _actionValues[value];
        _app.Change(s =>
        {
            s.PowerRules ??= new PowerRules();
            set(s.PowerRules, action);
            if (s.PowerRules is { OnLock: null, OnUnlock: null, OnSleep: null, OnWake: null }) s.PowerRules = null;
        });
    }

    /// <summary>The action stored at a position in <see cref="ActionLabels"/>.</summary>
    internal string? ActionValue(int index) => index >= 0 && index < _actionValues.Count ? _actionValues[index] : null;

    internal int ActionIndex(string? value)
    {
        string? normalized = AppAction.Parse(value)?.ToString();
        int at = _actionValues.FindIndex(v => string.Equals(v, normalized, StringComparison.OrdinalIgnoreCase));
        return at < 0 ? 0 : at;
    }

    private void SyncFeatures()
    {
        var settings = _app.Settings;
        Set(ref _showTrayIcon, !settings.HideTrayIcon, nameof(ShowTrayIcon));
        Set(ref _closeToTray, settings.CloseToTray, nameof(CloseToTray));
        Set(ref _showToasts, !settings.HideToasts, nameof(ShowToasts));
        PickerShortcut.Sync();

        // What rules can do. Values already in settings that no longer match a scene or input stay selectable.
        var labels = new List<string> { "Do nothing", "Go back to what was showing before" };
        var values = new List<string?> { null, AppAction.Back.ToString() };
        foreach (var scene in settings.Scenes.Where(s => !string.IsNullOrWhiteSpace(s.Id)))
        {
            labels.Add($"Run scene {ScenePlanner.DisplayName(scene)}");
            values.Add(AppAction.ForScene(scene.Id).ToString());
        }
        foreach (var m in _app.Monitors)
        {
            settings.Monitors.TryGetValue(m.Id, out var ms);
            foreach (var input in InputLayout.Build(m, ms).Where(i => !i.Hidden))
            {
                labels.Add($"{_app.MonitorName(m.Id)} to {input.Name}");
                values.Add(AppAction.ForSwitch(m.Id, input.Value).ToString());
            }
        }
        var inUse = settings.UsbRules.SelectMany(r => new[] { r.WhenConnected, r.WhenDisconnected })
            .Concat(new[] { settings.PowerRules?.OnLock, settings.PowerRules?.OnUnlock, settings.PowerRules?.OnSleep, settings.PowerRules?.OnWake })
            .Select(v => AppAction.Parse(v)?.ToString()).Where(v => v is not null).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string? v in inUse)
        {
            if (values.Contains(v, StringComparer.OrdinalIgnoreCase)) continue;
            labels.Add(AppAction.Parse(v)!.Describe(settings, _app.MonitorName) + " (not connected)");
            values.Add(v);
        }
        bool actionsChanged = !labels.SequenceEqual(ActionLabels);
        if (actionsChanged)
        {
            ActionLabels.Clear();
            foreach (string l in labels) ActionLabels.Add(l);
            // Make every drop-down pick its item again after the list changed under it.
            _onLockIndex = _onUnlockIndex = _onSleepIndex = _onWakeIndex = -1;
        }
        _actionValues = values;
        Set(ref _onLockIndex, ActionIndex(settings.PowerRules?.OnLock), nameof(OnLockIndex));
        Set(ref _onUnlockIndex, ActionIndex(settings.PowerRules?.OnUnlock), nameof(OnUnlockIndex));
        Set(ref _onSleepIndex, ActionIndex(settings.PowerRules?.OnSleep), nameof(OnSleepIndex));
        Set(ref _onWakeIndex, ActionIndex(settings.PowerRules?.OnWake), nameof(OnWakeIndex));

        var scenes = settings.Scenes.Where(s => !string.IsNullOrWhiteSpace(s.Id)).ToList();
        SyncList(Scenes, scenes, (vm, s) => vm.Id == s.Id, s => new SceneEditViewModel(this, s.Id),
            (vm, s) => vm.Sync(s, canMoveUp: scenes.IndexOf(s) > 0, canMoveDown: scenes.IndexOf(s) < scenes.Count - 1));
        HasScenes = Scenes.Count > 0;
        SyncList(SceneShortcuts, scenes, (item, s) => item.SlotKey == ShortcutSlots.SceneKey(s.Id),
            s => new ShortcutItemViewModel(_app, ShortcutSlots.SceneKey(s.Id), "", null, Ui.SceneGlyph),
            (item, s) =>
            {
                item.Title = $"Run scene {ScenePlanner.DisplayName(s)}";
                item.Description = "Press it again to go back to what was showing before.";
                item.Sync();
            });

        var indexes = Enumerable.Range(0, settings.UsbRules.Count).ToList();
        SyncList(UsbRules, indexes, (vm, i) => vm.Index == i, i => new UsbRuleEditViewModel(this, i), (vm, i) => vm.Sync(settings.UsbRules[i], actionsChanged));
        HasUsbRules = UsbRules.Count > 0;
    }
}

/// <summary>One scene on the Scenes page.</summary>
public sealed class SceneEditViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;
    private string _name = "";
    private int _iconIndex;
    private string _summary = "";
    private bool _canMoveUp, _canMoveDown;

    public SceneEditViewModel(SettingsViewModel owner, string id)
    {
        _owner = owner;
        Id = id;
        MoveUpCommand = new RelayCommand(() => Move(-1));
        MoveDownCommand = new RelayCommand(() => Move(+1));
        DeleteCommand = new RelayCommand(() => _owner.App.Change(s =>
        {
            s.Scenes.RemoveAll(x => x.Id == Id);
            // Rules that ran it now do nothing.
            foreach (var rule in s.UsbRules)
            {
                if (IsThisScene(rule.WhenConnected)) rule.WhenConnected = null;
                if (IsThisScene(rule.WhenDisconnected)) rule.WhenDisconnected = null;
            }
            if (s.PowerRules is { } p)
            {
                if (IsThisScene(p.OnLock)) p.OnLock = null;
                if (IsThisScene(p.OnUnlock)) p.OnUnlock = null;
                if (IsThisScene(p.OnSleep)) p.OnSleep = null;
                if (IsThisScene(p.OnWake)) p.OnWake = null;
            }
        }));
        RunCommand = new AsyncRelayCommand(() => _owner.App.RunSceneAsync(Id));
    }

    private bool IsThisScene(string? action) => AppAction.Parse(action) is { Kind: AppActionKind.Scene } a && string.Equals(a.SceneId, Id, StringComparison.OrdinalIgnoreCase);

    public string Id { get; }

    public ObservableCollection<SceneStepEditViewModel> Steps { get; } = new();

    public IRelayCommand MoveUpCommand { get; }

    public IRelayCommand MoveDownCommand { get; }

    public IRelayCommand DeleteCommand { get; }

    public IAsyncRelayCommand RunCommand { get; }

    /// <summary>"Scene" first, then the input icons.</summary>
    public static IReadOnlyList<string> IconLabels { get; } = ["Scene", .. Ui.IconChoices.Select(c => c.Label)];

    public string Name
    {
        get => _name;
        set
        {
            if (!SetProperty(ref _name, value)) return;
            OnPropertyChanged(nameof(Title));
            if (_owner.Syncing) return;
            string? name = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Edit(s => s.Name = name);
        }
    }

    public string Title => string.IsNullOrWhiteSpace(_name) ? "Scene" : _name.Trim();

    public int IconIndex
    {
        get => _iconIndex;
        set
        {
            if (value < 0 || !SetProperty(ref _iconIndex, value)) return;
            OnPropertyChanged(nameof(Glyph));
            if (_owner.Syncing) return;
            string? icon = value == 0 ? null : Ui.IconChoices[value - 1].Key;
            Edit(s => s.Icon = icon);
        }
    }

    public string Glyph => _iconIndex == 0 ? Ui.SceneGlyph : Ui.IconChoices[_iconIndex - 1].Glyph;

    /// <summary>"MSI G27CQ4 to PS5, Acer VG240Y at 40%".</summary>
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public bool CanMoveUp { get => _canMoveUp; private set => SetProperty(ref _canMoveUp, value); }

    public bool CanMoveDown { get => _canMoveDown; private set => SetProperty(ref _canMoveDown, value); }

    public string MoveUpName => $"Move {Title} up";

    public string MoveDownName => $"Move {Title} down";

    public string DeleteName => $"Delete {Title}";

    internal SettingsViewModel Owner => _owner;

    internal void Edit(Action<SceneSettings> edit) => _owner.App.Change(s =>
    {
        if (s.Scenes.FirstOrDefault(x => x.Id == Id) is { } scene) edit(scene);
    });

    private void Move(int delta) => _owner.App.Change(s =>
    {
        int from = s.Scenes.FindIndex(x => x.Id == Id), to = from + delta;
        if (from < 0 || to < 0 || to >= s.Scenes.Count) return;
        (s.Scenes[from], s.Scenes[to]) = (s.Scenes[to], s.Scenes[from]);
    });

    internal void Sync(SceneSettings scene, bool canMoveUp, bool canMoveDown)
    {
        Name = scene.Name ?? "";
        int icon = scene.Icon is null ? 0 : Ui.IconChoices.ToList().FindIndex(c => c.Key.Equals(scene.Icon, StringComparison.OrdinalIgnoreCase)) + 1;
        IconIndex = Math.Max(0, icon);
        CanMoveUp = canMoveUp;
        CanMoveDown = canMoveDown;
        OnPropertyChanged(nameof(MoveUpName));
        OnPropertyChanged(nameof(MoveDownName));
        OnPropertyChanged(nameof(DeleteName));

        // A row for every monitor InputFlip knows, connected or not, in the window's order.
        var settings = _owner.App.Settings;
        var ids = settings.Monitors.Keys.ToList();
        foreach (var step in scene.Steps)
            if (!ids.Contains(step.Monitor, StringComparer.OrdinalIgnoreCase)) ids.Add(step.Monitor);
        SettingsViewModel.SyncList(Steps, ids, (vm, id) => string.Equals(vm.MonitorId, id, StringComparison.OrdinalIgnoreCase),
            id => new SceneStepEditViewModel(this, id), (vm, id) => vm.Sync(scene.Steps.FirstOrDefault(s => string.Equals(s.Monitor, id, StringComparison.OrdinalIgnoreCase))));

        var plan = scene.Steps.Where(s => s.Input is not null || s.Brightness is not null).Select(s =>
        {
            string monitor = _owner.App.MonitorName(s.Monitor);
            string input = s.Input is uint v ? Steps.FirstOrDefault(x => x.MonitorId == s.Monitor)?.InputName(v) ?? InputCatalog.GetPortName(v) : "";
            return (s.Input, s.Brightness) switch
            {
                (not null, not null) => $"{monitor} to {input} at {s.Brightness}%",
                (not null, null) => $"{monitor} to {input}",
                _ => $"{monitor} at {s.Brightness}%",
            };
        }).ToList();
        Summary = plan.Count == 0 ? "Does nothing yet. Pick an input for a monitor below." : string.Join(", ", plan);
    }
}

/// <summary>One monitor in a scene: which input (or leave it), and optionally a brightness level.</summary>
public sealed class SceneStepEditViewModel : ObservableObject
{
    private readonly SceneEditViewModel _scene;
    private string _monitorName = "";
    private int _inputIndex;
    private List<uint?> _inputValues = [null];
    private bool _setsBrightness;
    private double _brightness = 50;
    private bool _isConnected;

    public SceneStepEditViewModel(SceneEditViewModel scene, string monitorId)
    {
        _scene = scene;
        MonitorId = monitorId;
    }

    public string MonitorId { get; }

    public string MonitorName { get => _monitorName; private set => SetProperty(ref _monitorName, value); }

    public bool IsConnected { get => _isConnected; private set => SetProperty(ref _isConnected, value); }

    /// <summary>"Leave it as it is", then the monitor's inputs.</summary>
    public ObservableCollection<string> InputLabels { get; } = new();

    public int InputIndex
    {
        get => _inputIndex;
        set
        {
            if (value < 0 || value >= _inputValues.Count || !SetProperty(ref _inputIndex, value) || _scene.Owner.Syncing) return;
            uint? input = _inputValues[value];
            EditStep(step => step.Input = input);
        }
    }

    public bool SetsBrightness
    {
        get => _setsBrightness;
        set
        {
            if (!SetProperty(ref _setsBrightness, value) || _scene.Owner.Syncing) return;
            int level = (int)Math.Round(_brightness);
            EditStep(step => step.Brightness = value ? level : null);
        }
    }

    public double Brightness
    {
        get => _brightness;
        set
        {
            if (!SetProperty(ref _brightness, value) || _scene.Owner.Syncing || !_setsBrightness) return;
            int level = (int)Math.Round(value);
            EditStep(step => step.Brightness = level);
        }
    }

    public string InputBoxName => $"Input for {_monitorName}";

    public string BrightnessBoxName => $"Set the brightness of {_monitorName}";

    internal string? InputName(uint value)
    {
        int at = _inputValues.IndexOf(value);
        return at > 0 && at < InputLabels.Count ? InputLabels[at] : null;
    }

    private void EditStep(Action<SceneStep> edit) => _scene.Edit(scene =>
    {
        var step = scene.Steps.FirstOrDefault(s => string.Equals(s.Monitor, MonitorId, StringComparison.OrdinalIgnoreCase));
        if (step is null)
        {
            step = new SceneStep { Monitor = MonitorId };
            scene.Steps.Add(step);
        }
        edit(step);
        if (step.Input is null && step.Brightness is null) scene.Steps.Remove(step);
    });

    internal void Sync(SceneStep? step)
    {
        var app = _scene.Owner.App;
        MonitorName = app.MonitorName(MonitorId);
        OnPropertyChanged(nameof(InputBoxName));
        OnPropertyChanged(nameof(BrightnessBoxName));
        var snapshot = app.Monitors.FirstOrDefault(m => m.Id == MonitorId);
        IsConnected = snapshot is not null;
        app.Settings.Monitors.TryGetValue(MonitorId, out var ms);

        var inputs = snapshot is not null
            ? InputLayout.Build(snapshot, ms).Where(i => !i.Hidden || i.Value == step?.Input).Select(i => (i.Value, i.Name)).ToList()
            : (ms?.Inputs ?? []).Where(i => !i.Hidden || i.Value == step?.Input)
                .Select(i => (i.Value, Name: string.IsNullOrWhiteSpace(i.Name) ? InputCatalog.GetPortName(i.Value) : i.Name!.Trim())).ToList();
        if (step?.Input is uint wanted && !inputs.Any(i => i.Value == wanted)) inputs.Add((wanted, InputCatalog.GetPortName(wanted)));

        var labels = new List<string> { "Leave it as it is" };
        labels.AddRange(inputs.Select(i => i.Name));
        var values = new List<uint?> { null };
        values.AddRange(inputs.Select(i => (uint?)i.Value));
        if (!labels.SequenceEqual(InputLabels))
        {
            InputLabels.Clear();
            foreach (string l in labels) InputLabels.Add(l);
            _inputIndex = -1;
        }
        _inputValues = values;
        _scene.Owner.Set(ref _inputIndex, Math.Max(0, values.IndexOf(step?.Input)), nameof(InputIndex));
        _scene.Owner.Set(ref _setsBrightness, step?.Brightness is not null, nameof(SetsBrightness));
        if (step?.Brightness is int b) _scene.Owner.Set(ref _brightness, b, nameof(Brightness));
    }
}

/// <summary>One USB rule on the Rules page.</summary>
public sealed class UsbRuleEditViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;
    private int _deviceIndex = -1, _connectedIndex, _disconnectedIndex;
    private bool _isOn = true;
    private List<UsbDeviceInfo?> _devices = [];
    private string _title = "";

    public UsbRuleEditViewModel(SettingsViewModel owner, int index)
    {
        _owner = owner;
        Index = index;
        DeleteCommand = new RelayCommand(() => _owner.App.Change(s => { if (Index < s.UsbRules.Count) s.UsbRules.RemoveAt(Index); }));
    }

    public int Index { get; }

    public IRelayCommand DeleteCommand { get; }

    public string Title { get => _title; private set => SetProperty(ref _title, value); }

    public string DeleteName => $"Delete the rule for {_title}";

    /// <summary>USB devices plugged in now, plus the rule's own device if it isn't.</summary>
    public ObservableCollection<string> DeviceLabels { get; } = new();

    public int DeviceIndex
    {
        get => _deviceIndex;
        set
        {
            if (value < 0 || value >= _devices.Count || !SetProperty(ref _deviceIndex, value) || _owner.Syncing) return;
            if (_devices[value] is not { } device) return;
            Edit(r =>
            {
                r.Device = device.Id.ToString();
                r.Name = device.Name;
            });
        }
    }

    public int ConnectedIndex
    {
        get => _connectedIndex;
        set
        {
            if (value < 0 || !SetProperty(ref _connectedIndex, value) || _owner.Syncing) return;
            string? action = _owner.ActionValue(value);
            Edit(r => r.WhenConnected = action);
        }
    }

    public int DisconnectedIndex
    {
        get => _disconnectedIndex;
        set
        {
            if (value < 0 || !SetProperty(ref _disconnectedIndex, value) || _owner.Syncing) return;
            string? action = _owner.ActionValue(value);
            Edit(r => r.WhenDisconnected = action);
        }
    }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (SetProperty(ref _isOn, value) && !_owner.Syncing) Edit(r => r.Disabled = !value);
        }
    }

    public ObservableCollection<string> ActionLabels => _owner.ActionLabels;

    private void Edit(Action<UsbRule> edit) => _owner.App.Change(s => { if (Index < s.UsbRules.Count) edit(s.UsbRules[Index]); });

    internal void Sync(UsbRule rule, bool actionsChanged)
    {
        var id = UsbDeviceId.TryParse(rule.Device);
        var devices = _owner.UsbDevicesNow.Select(d => (UsbDeviceInfo?)d).ToList();
        int at = id is null ? -1 : devices.FindIndex(d => d!.Id.Matches(id) || id.Matches(d.Id));
        if (id is not null && at < 0)
        {
            devices.Insert(0, new UsbDeviceInfo(id, $"{rule.Name ?? id.ToString()} (not plugged in)"));
            at = 0;
        }
        var labels = devices.Select(d => d!.Name).ToList();
        if (!labels.SequenceEqual(DeviceLabels))
        {
            DeviceLabels.Clear();
            foreach (string l in labels) DeviceLabels.Add(l);
            _deviceIndex = -2;
        }
        _devices = devices;
        _owner.Set(ref _deviceIndex, at, nameof(DeviceIndex));
        Title = rule.Name ?? id?.ToString() ?? "a USB device";
        OnPropertyChanged(nameof(DeleteName));

        if (actionsChanged) _connectedIndex = _disconnectedIndex = -1;
        _owner.Set(ref _connectedIndex, _owner.ActionIndex(rule.WhenConnected), nameof(ConnectedIndex));
        _owner.Set(ref _disconnectedIndex, _owner.ActionIndex(rule.WhenDisconnected), nameof(DisconnectedIndex));
        _owner.Set(ref _isOn, !rule.Disabled, nameof(IsOn));
    }
}
