using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Switchboard.Core;
using Switchboard.Core.Ddc;
using Switchboard.Core.Hotkeys;
using Switchboard.Core.Monitors;
using Switchboard.Core.Settings;

namespace Switchboard.ViewModels;

/// <summary>
/// Everything the settings and setup windows show. Every edit is applied and saved at once (no OK button, as in
/// Windows Settings). <see cref="Sync"/> refreshes values in place, so a text box you're typing in isn't disturbed.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppController _app;
    private bool _startWithWindows;
    private int _themeIndex;
    private bool _alwaysOnTop;
    private bool _moveMouse;
    private string? _generalMessage;
    private int _cycleMonitorIndex;
    private List<string?> _cycleMonitorIds = [];

    public SettingsViewModel(AppController app)
    {
        _app = app;
        ShowShortcut = new ShortcutItemViewModel(app, ShortcutSlots.Show, "Bring Switchboard to the front", "Shows the window, even when it's minimized or behind other windows.", "\uE737");
        CycleShortcut = new ShortcutItemViewModel(app, ShortcutSlots.Cycle, "Switch to the next input", "Goes through the shown inputs of one monitor, one press at a time. With two inputs shown, it flips between them.", "\uE8AB");
        EditSettingsFileCommand = new RelayCommand(app.EditSettingsFile);
        OpenLogCommand = new RelayCommand(() => LogMessage = app.OpenLog() ? null : "There's nothing in the log yet.");
        OpenDataFolderCommand = new RelayCommand(app.OpenDataFolder);
        RunSetupCommand = new RelayCommand(app.OpenSetup);
        CopyAllDiagnosticsCommand = new RelayCommand(() => CopyText(app.Diagnostics(null)));
        CheckUpdatesCommand = new AsyncRelayCommand(() => app.CheckForUpdatesAsync(quiet: true));
        InstallUpdateCommand = new AsyncRelayCommand(app.InstallUpdateAsync);
        Sync();
    }

    public AppController App => _app;

    public ObservableCollection<MonitorEditViewModel> Monitors { get; } = new();

    // ---- General ----

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!SetProperty(ref _startWithWindows, value) || Syncing) return;
            GeneralMessage = _app.SetStartWithWindows(value);
            _startWithWindows = _app.StartsWithWindows;
            OnPropertyChanged();
        }
    }

    /// <summary>0: follow Windows, 1: light, 2: dark.</summary>
    public int ThemeIndex
    {
        get => _themeIndex;
        set
        {
            if (value < 0 || !SetProperty(ref _themeIndex, value) || Syncing) return;
            _app.Change(s => s.Theme = value switch { 1 => SettingsEditor.ThemeLight, 2 => SettingsEditor.ThemeDark, _ => null });
        }
    }

    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set
        {
            if (SetProperty(ref _alwaysOnTop, value) && !Syncing) _app.SetAlwaysOnTop(value);
        }
    }

    /// <summary>Move the mouse off a monitor that switches to another device, and back afterwards.</summary>
    public bool MoveMouse
    {
        get => _moveMouse;
        set
        {
            if (SetProperty(ref _moveMouse, value) && !Syncing) _app.Change(s => s.LeaveMouseAlone = !value);
        }
    }

    public string? GeneralMessage
    {
        get => _generalMessage;
        set => SetProperty(ref _generalMessage, value);
    }

    public IRelayCommand EditSettingsFileCommand { get; }

    public string SettingsFilePath => AppPaths.SettingsFile;

    // ---- Shortcuts ----

    public ShortcutItemViewModel ShowShortcut { get; }

    public ShortcutItemViewModel CycleShortcut { get; }

    /// <summary>"Main display" first, then each connected monitor.</summary>
    public ObservableCollection<string> CycleMonitorOptions { get; } = new();

    public int CycleMonitorIndex
    {
        get => _cycleMonitorIndex;
        set
        {
            if (value < 0 || value >= _cycleMonitorIds.Count || !SetProperty(ref _cycleMonitorIndex, value) || Syncing) return;
            string? id = _cycleMonitorIds[value];
            _app.Change(s => s.CycleMonitor = id);
        }
    }

    public ObservableCollection<ShortcutGroupViewModel> InputShortcutGroups { get; } = new();

    // ---- About ----

    public string Version => $"Version {AppInfo.Version}";

    public string DataFolder => AppPaths.DataDirectory;

    private string? _logMessage;

    public string? LogMessage { get => _logMessage; set => SetProperty(ref _logMessage, value); }

    public IRelayCommand OpenLogCommand { get; }

    public IRelayCommand OpenDataFolderCommand { get; }

    public IRelayCommand RunSetupCommand { get; }

    public IRelayCommand CopyAllDiagnosticsCommand { get; }

    public ObservableCollection<DiagnosticsViewModel> Diagnostics { get; } = new();

    public bool NoSwitch => _app.NoSwitch;

    private string _updateMessage = "";
    private bool _canInstallUpdate;
    private bool _canCheckUpdates = true;

    /// <summary>The result of the last update check, in words.</summary>
    public string UpdateMessage => _updateMessage;

    public bool CanInstallUpdate => _canInstallUpdate;

    public bool CanCheckUpdates => _canCheckUpdates;

    public IAsyncRelayCommand CheckUpdatesCommand { get; }

    public IAsyncRelayCommand InstallUpdateCommand { get; }

    private void SyncUpdates()
    {
        var check = _app.UpdateStatus;
        bool installing = _app.ViewModel.IsUpdating;
        string message = installing ? $"Installing version {check.NewVersion}. Switchboard restarts when it's done."
            : _app.IsCheckingForUpdates ? "Checking…"
            : check.State == UpdateState.Unknown ? "Not checked yet."
            : check.Message;
        Set(ref _updateMessage, message, nameof(UpdateMessage));
        Set(ref _canInstallUpdate, check.State == UpdateState.Available && !installing, nameof(CanInstallUpdate));
        Set(ref _canCheckUpdates, !_app.IsCheckingForUpdates && !installing, nameof(CanCheckUpdates));
    }

    // ---- Keeping in step ----

    internal bool Syncing { get; private set; }

    public void Sync()
    {
        Syncing = true;
        try
        {
            var settings = _app.Settings;
            var connected = _app.Monitors;

            Set(ref _startWithWindows, _app.StartsWithWindows, nameof(StartWithWindows));
            Set(ref _themeIndex, SettingsEditor.NormalizeTheme(settings.Theme) switch { SettingsEditor.ThemeLight => 1, SettingsEditor.ThemeDark => 2, _ => 0 }, nameof(ThemeIndex));
            Set(ref _alwaysOnTop, settings.AlwaysOnTop, nameof(AlwaysOnTop));
            Set(ref _moveMouse, !settings.LeaveMouseAlone, nameof(MoveMouse));
            SyncUpdates();

            // Monitors in settings order (the order the app shows them), including ones that aren't connected now.
            var ids = settings.Monitors.Keys.ToList();
            ids.AddRange(connected.Select(m => m.Id).Where(id => !ids.Contains(id, StringComparer.OrdinalIgnoreCase)));
            var allIds = settings.Monitors.Keys.ToList();
            SyncList(Monitors, ids, (vm, id) => vm.Id == id, id => new MonitorEditViewModel(this, id), (vm, id) =>
            {
                settings.Monitors.TryGetValue(id, out var ms);
                int at = allIds.FindIndex(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase));
                vm.Sync(connected.FirstOrDefault(m => m.Id == id), ms, canMoveUp: at > 0, canMoveDown: at >= 0 && at < allIds.Count - 1);
            });

            // Shortcuts.
            ShowShortcut.Sync();
            CycleShortcut.Sync();
            var options = new List<string> { "Main display (automatic)" };
            var optionIds = new List<string?> { null };
            foreach (var m in connected)
            {
                options.Add(_app.MonitorName(m.Id));
                optionIds.Add(m.Id);
            }
            if (!string.IsNullOrWhiteSpace(settings.CycleMonitor) && !optionIds.Contains(settings.CycleMonitor, StringComparer.OrdinalIgnoreCase))
            {
                options.Add($"{_app.MonitorName(settings.CycleMonitor)} (not connected)");
                optionIds.Add(settings.CycleMonitor);
            }
            if (!options.SequenceEqual(CycleMonitorOptions))
            {
                CycleMonitorOptions.Clear();
                foreach (var o in options) CycleMonitorOptions.Add(o);
                _cycleMonitorIndex = -1; // make the ComboBox pick it up again after its items changed
            }
            _cycleMonitorIds = optionIds;
            int cycleIndex = optionIds.FindIndex(id => string.Equals(id, string.IsNullOrWhiteSpace(settings.CycleMonitor) ? null : settings.CycleMonitor, StringComparison.OrdinalIgnoreCase));
            Set(ref _cycleMonitorIndex, Math.Max(0, cycleIndex), nameof(CycleMonitorIndex));

            SyncList(InputShortcutGroups, Monitors.Where(m => m.IsConnected).ToList(), (g, m) => g.MonitorId == m.Id,
                m => new ShortcutGroupViewModel(_app, m.Id), (g, m) => g.Sync(m));

            // Diagnostics.
            SyncList(Diagnostics, connected.ToList(), (d, m) => d.Id == m.Id, m => new DiagnosticsViewModel(this, m.Id), (d, m) => d.Sync(m));
        }
        finally
        {
            Syncing = false;
        }
    }

    internal void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    /// <summary>Updates a collection in place: keeps matching items (and the focus inside them), adds, moves, removes.</summary>
    internal static void SyncList<TVm, TSource>(ObservableCollection<TVm> list, IReadOnlyList<TSource> sources,
        Func<TVm, TSource, bool> matches, Func<TSource, TVm> create, Action<TVm, TSource> update)
    {
        for (int i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            int at = -1;
            for (int j = 0; j < list.Count; j++)
                if (matches(list[j], source)) { at = j; break; }
            TVm vm;
            if (at < 0)
            {
                vm = create(source);
                list.Insert(i, vm);
            }
            else
            {
                vm = list[at];
                if (at != i) list.Move(at, i);
            }
            update(vm, source);
        }
        while (list.Count > sources.Count) list.RemoveAt(list.Count - 1);
    }

    internal static void CopyText(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }
}

public sealed class MonitorEditViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;
    private string _title = "";
    private string _nickname = "";
    private string _model = "";
    private string _status = "";
    private string? _connection;
    private bool _isConnected;
    private bool _isShown = true;
    private bool _canMoveUp;
    private bool _canMoveDown;
    private int _screensOffIndex;
    private List<uint?> _screensOffValues = [];
    private string _addValueText = "";
    private string? _addError;
    private string? _suggestion;
    private uint? _suggestedValue;
    private MonitorSnapshot? _snapshot;

    public MonitorEditViewModel(SettingsViewModel owner, string id)
    {
        _owner = owner;
        Id = id;
        MoveUpCommand = new RelayCommand(() => App.Change(s => SettingsEditor.MoveMonitor(s, Id, -1)));
        MoveDownCommand = new RelayCommand(() => App.Change(s => SettingsEditor.MoveMonitor(s, Id, +1)));
        AddCommand = new RelayCommand(Add);
        ForgetCommand = new RelayCommand(() => App.Change(s => s.Monitors.Remove(Id)));
        ApplySuggestionCommand = new RelayCommand(() => { if (_suggestedValue is uint v) Edit(ms => ms.BlankScreensWhenLeaving = v); });
    }

    private AppController App => _owner.App;

    public string Id { get; }

    public ObservableCollection<InputEditViewModel> Inputs { get; } = new();

    public string Title { get => _title; private set => SetProperty(ref _title, value); }

    /// <summary>The name shown in Switchboard. Clearing it goes back to the model name.</summary>
    public string Nickname
    {
        get => _nickname;
        set
        {
            if (!SetProperty(ref _nickname, value) || _owner.Syncing) return;
            string? name = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Edit(ms => ms.Nickname = name ?? _model);
        }
    }

    public string ModelName { get => _model; private set => SetProperty(ref _model, value); }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>"Main display · DisplayPort · MSI3CB0-000000D1".</summary>
    public string Details => string.Join(" · ", new[] { _snapshot?.IsPrimary == true ? "Main display" : null, _connection, Id }.Where(x => x is not null));

    public bool IsConnected { get => _isConnected; private set => SetProperty(ref _isConnected, value); }

    public bool IsNotConnected => !_isConnected;

    /// <summary>Answers DDC/CI (now, or at least once since Switchboard started).</summary>
    public bool IsAnswering => _snapshot is { } s && (s.Health == MonitorHealth.Responding || s.HasAnswered);

    public bool IsNotAnswering => !IsAnswering;

    public bool IsShown
    {
        get => _isShown;
        set
        {
            if (SetProperty(ref _isShown, value) && !_owner.Syncing) Edit(ms => ms.Hidden = !value);
        }
    }

    public bool CanMoveUp { get => _canMoveUp; private set => SetProperty(ref _canMoveUp, value); }

    public bool CanMoveDown { get => _canMoveDown; private set => SetProperty(ref _canMoveDown, value); }

    public IRelayCommand MoveUpCommand { get; }

    public IRelayCommand MoveDownCommand { get; }

    public IRelayCommand ForgetCommand { get; }

    public string MoveUpName => $"Move {_title} up";

    public string MoveDownName => $"Move {_title} down";

    // Screens-off switch: "Off" first, then each input.
    public ObservableCollection<string> ScreensOffOptions { get; } = new();

    public int ScreensOffIndex
    {
        get => _screensOffIndex;
        set
        {
            if (value < 0 || value >= _screensOffValues.Count || !SetProperty(ref _screensOffIndex, value) || _owner.Syncing) return;
            uint? v = _screensOffValues[value];
            Edit(ms => ms.BlankScreensWhenLeaving = v);
        }
    }

    /// <summary>Set when this monitor looks like the MSI: answers over DisplayPort but lists no inputs.</summary>
    public string? Suggestion { get => _suggestion; private set => SetProperty(ref _suggestion, value); }

    public IRelayCommand ApplySuggestionCommand { get; }

    public uint? SuggestedScreensOffValue => _suggestedValue;

    public string AddValueText { get => _addValueText; set => SetProperty(ref _addValueText, value); }

    public string? AddError { get => _addError; private set => SetProperty(ref _addError, value); }

    public IRelayCommand AddCommand { get; }

    internal void Edit(Action<MonitorSettings> change)
    {
        App.Change(s =>
        {
            if (!s.Monitors.TryGetValue(Id, out var ms))
            {
                ms = new MonitorSettings { Nickname = _model };
                s.Monitors[Id] = ms;
            }
            change(ms);
        });
    }

    private void Add()
    {
        string? error = null;
        uint added = 0;
        Edit(ms => error = SettingsEditor.AddInput(ms, Inputs.Select(i => i.Value), AddValueText, out added));
        AddError = error;
        if (error is null)
        {
            AddValueText = "";
            Log.Info($"{_title}: added input {InputCatalog.FormatValue(added)}");
        }
    }

    internal IReadOnlyList<uint> ShownOrder => Inputs.Select(i => i.Value).ToList();

    internal MonitorSnapshot? Snapshot => _snapshot;

    internal void Sync(MonitorSnapshot? snapshot, MonitorSettings? ms, bool canMoveUp, bool canMoveDown)
    {
        _snapshot = snapshot;
        ModelName = snapshot?.Name ?? ms?.Nickname ?? Id;
        string title = string.IsNullOrWhiteSpace(ms?.Nickname) ? ModelName : ms!.Nickname!.Trim();
        if (title != _title)
        {
            Title = title;
            OnPropertyChanged(nameof(MoveUpName));
            OnPropertyChanged(nameof(MoveDownName));
        }
        _owner.Set(ref _nickname, title, nameof(Nickname));
        IsConnected = snapshot is not null;
        OnPropertyChanged(nameof(IsNotConnected));
        OnPropertyChanged(nameof(IsAnswering));
        OnPropertyChanged(nameof(IsNotAnswering));
        Status = snapshot is null ? "Not connected now." : SetupAdvisor.Describe(snapshot);
        _connection = snapshot is null ? null : SetupAdvisor.DescribeConnection(snapshot.Connection);
        OnPropertyChanged(nameof(Details));
        _owner.Set(ref _isShown, ms?.Hidden != true, nameof(IsShown));
        CanMoveUp = canMoveUp;
        CanMoveDown = canMoveDown;

        var layout = InputLayout.Build(
            snapshot ?? new MonitorSnapshot(Id, ModelName, false, default, MonitorHealth.Unknown, null, null, false),
            ms);
        SettingsViewModel.SyncList(Inputs, layout, (vm, input) => vm.Value == input.Value,
            input => new InputEditViewModel(this, input.Value), (vm, input) => { });
        for (int i = 0; i < layout.Count; i++)
            Inputs[i].Sync(layout[i], i > 0, i < layout.Count - 1, SettingsEditor.CanRemoveInput(snapshot, layout[i].Value));

        var options = new List<string> { "Off: switch normally" };
        var values = new List<uint?> { null };
        foreach (var input in layout)
        {
            options.Add(input.Name == input.PortName ? input.PortName : $"{input.Name} ({input.PortName})");
            values.Add(input.Value);
        }
        if (!options.SequenceEqual(ScreensOffOptions))
        {
            ScreensOffOptions.Clear();
            foreach (var o in options) ScreensOffOptions.Add(o);
            _screensOffIndex = -1;
        }
        _screensOffValues = values;
        _owner.Set(ref _screensOffIndex, Math.Max(0, values.IndexOf(ms?.BlankScreensWhenLeaving)), nameof(ScreensOffIndex));

        _suggestedValue = snapshot is null ? null : SetupAdvisor.SuggestScreensOff(snapshot, ms);
        Suggestion = _suggestedValue is uint s
            ? $"{title} answers over DisplayPort but doesn't list its inputs. Monitors like that often ignore switch commands while they show this PC. Turning the screens off when leaving {InputCatalog.GetPortName(s)} makes it listen."
            : null;
    }
}

public sealed class InputEditViewModel : ObservableObject
{
    private readonly MonitorEditViewModel _monitor;
    private string _name = "";
    private string _portName = "";
    private int _iconIndex;
    private bool _isShown = true;
    private bool _isCurrent;
    private bool _canMoveUp;
    private bool _canMoveDown;
    private bool _canRemove;
    private bool _syncing;

    public InputEditViewModel(MonitorEditViewModel monitor, uint value)
    {
        _monitor = monitor;
        Value = value;
        MoveUpCommand = new RelayCommand(() => Move(-1));
        MoveDownCommand = new RelayCommand(() => Move(+1));
        RemoveCommand = new RelayCommand(() => _monitor.Edit(ms => SettingsEditor.RemoveInput(ms, Value)));
        SetIconCommand = new RelayCommand<string>(key =>
        {
            int index = Ui.IconChoices.ToList().FindIndex(c => c.Key == key);
            if (index >= 0) IconIndex = index;
        });
    }

    public IRelayCommand<string> SetIconCommand { get; }

    public string IconKey => Ui.IconChoices[Math.Max(0, _iconIndex)].Key;

    public string IconGlyph => Ui.IconChoices[Math.Max(0, _iconIndex)].Glyph;

    public uint Value { get; }

    public string ValueText => InputCatalog.FormatValue(Value);

    public string PortName
    {
        get => _portName;
        private set
        {
            if (SetProperty(ref _portName, value)) NotifyLabels();
        }
    }

    public string PortLine => $"{_portName} · {ValueText}";

    /// <summary>Your name for what's plugged in. Clearing it, or typing the port name, shows the port name.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (!SetProperty(ref _name, value)) return;
            // Narrator names of this row's buttons ("Move PS5 up") follow the name.
            NotifyLabels();
            if (_syncing) return;
            string? name = string.IsNullOrWhiteSpace(value) || value.Trim() == _portName ? null : value.Trim();
            _monitor.Edit(ms => InputLayout.GetOrAdd(ms, Value).Name = name);
        }
    }

    public int IconIndex
    {
        get => _iconIndex;
        set
        {
            if (value < 0 || !SetProperty(ref _iconIndex, value)) return;
            OnPropertyChanged(nameof(IconKey));
            OnPropertyChanged(nameof(IconGlyph));
            OnPropertyChanged(nameof(IconBoxName));
            if (_syncing) return;
            string icon = Ui.IconChoices[value].Key;
            _monitor.Edit(ms => InputLayout.GetOrAdd(ms, Value).Icon = icon);
        }
    }

    public bool IsShown
    {
        get => _isShown;
        set
        {
            if (SetProperty(ref _isShown, value) && !_syncing) _monitor.Edit(ms => InputLayout.GetOrAdd(ms, Value).Hidden = !value);
        }
    }

    public bool IsCurrent { get => _isCurrent; private set => SetProperty(ref _isCurrent, value); }

    public bool CanMoveUp { get => _canMoveUp; private set => SetProperty(ref _canMoveUp, value); }

    public bool CanMoveDown { get => _canMoveDown; private set => SetProperty(ref _canMoveDown, value); }

    public bool CanRemove { get => _canRemove; private set => SetProperty(ref _canRemove, value); }

    public IRelayCommand MoveUpCommand { get; }

    public IRelayCommand MoveDownCommand { get; }

    public IRelayCommand RemoveCommand { get; }

    public string NameBoxName => $"Name for {_portName}";

    public string IconBoxName => $"Icon for {_name}: {Ui.IconChoices[Math.Max(0, _iconIndex)].Label}";

    public string ShowBoxName => $"Show {_name} in Switchboard";

    public string MoveUpName => $"Move {_name} up";

    public string MoveDownName => $"Move {_name} down";

    public string RemoveName => $"Remove {_name}";

    private void Move(int delta)
    {
        var order = _monitor.ShownOrder;
        _monitor.Edit(ms => SettingsEditor.MoveInput(ms, order, Value, delta));
    }

    private void NotifyLabels()
    {
        foreach (var n in new[] { nameof(PortLine), nameof(NameBoxName), nameof(IconBoxName), nameof(ShowBoxName), nameof(MoveUpName), nameof(MoveDownName), nameof(RemoveName) })
            OnPropertyChanged(n);
    }

    internal void Sync(DisplayInput input, bool canMoveUp, bool canMoveDown, bool canRemove)
    {
        _syncing = true;
        try
        {
            PortName = input.PortName;
            Name = input.Name;
            IconIndex = Math.Max(0, Ui.IconChoices.ToList().FindIndex(c => c.Key == input.Icon));
            IsShown = !input.Hidden;
            IsCurrent = input.IsCurrent;
            CanMoveUp = canMoveUp;
            CanMoveDown = canMoveDown;
            CanRemove = canRemove;
        }
        finally
        {
            _syncing = false;
        }
    }
}

/// <summary>One shortcut in the Shortcuts page; the recorder control talks to the app through it.</summary>
public sealed class ShortcutItemViewModel : ObservableObject
{
    private readonly AppController _app;
    private string _title;
    private string? _description;
    private string _glyph;
    private string? _text;
    private string? _problem;

    public ShortcutItemViewModel(AppController app, string slotKey, string title, string? description, string glyph)
    {
        _app = app;
        SlotKey = slotKey;
        _title = title;
        _description = description;
        _glyph = glyph;
    }

    public string SlotKey { get; }

    public string Title { get => _title; set => SetProperty(ref _title, value); }

    public string? Description { get => _description; set => SetProperty(ref _description, value); }

    public string Glyph { get => _glyph; set => SetProperty(ref _glyph, value); }

    /// <summary>The shortcut as saved ("Ctrl+Alt+Shift+I"), or null.</summary>
    public string? Text { get => _text; private set => SetProperty(ref _text, value); }

    /// <summary>Why the saved shortcut doesn't work, or null.</summary>
    public string? Problem { get => _problem; private set => SetProperty(ref _problem, value); }

    public void Sync()
    {
        Text = ShortcutSlots.Get(_app.Settings, SlotKey);
        Problem = _app.ShortcutProblem(SlotKey);
    }

    public string? TryAssign(Hotkey hotkey)
    {
        string? error = _app.AssignShortcut(SlotKey, hotkey);
        Sync();
        return error;
    }

    public void Clear()
    {
        _app.ClearShortcut(SlotKey);
        Sync();
    }

    public void BeginRecording() => _app.SuspendHotkeys();

    public void EndRecording()
    {
        _app.ResumeHotkeys();
        Sync();
    }
}

/// <summary>A monitor's inputs on the Shortcuts page.</summary>
public sealed class ShortcutGroupViewModel : ObservableObject
{
    private readonly AppController _app;
    private string _title = "";

    public ShortcutGroupViewModel(AppController app, string monitorId)
    {
        _app = app;
        MonitorId = monitorId;
    }

    public string MonitorId { get; }

    public string Title { get => _title; private set => SetProperty(ref _title, value); }

    public ObservableCollection<ShortcutItemViewModel> Items { get; } = new();

    internal void Sync(MonitorEditViewModel monitor)
    {
        Title = monitor.Title;
        var shown = monitor.Inputs.Where(i => i.IsShown).ToList();
        SettingsViewModel.SyncList(Items, shown, (item, input) => item.SlotKey == ShortcutSlots.InputKey(MonitorId, input.Value),
            input => new ShortcutItemViewModel(_app, ShortcutSlots.InputKey(MonitorId, input.Value), "", null, ""),
            (item, input) =>
            {
                item.Title = $"Switch to {input.Name}";
                item.Description = input.Name == input.PortName ? null : input.PortName;
                item.Glyph = Ui.IconChoices[Math.Max(0, input.IconIndex)].Glyph;
                item.Sync();
            });
    }
}

/// <summary>What Switchboard knows about a monitor, for the About page.</summary>
public sealed class DiagnosticsViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;
    private string _title = "";
    private string _summary = "";
    private string _capabilities = "";

    public DiagnosticsViewModel(SettingsViewModel owner, string id)
    {
        _owner = owner;
        Id = id;
        CopyCommand = new RelayCommand(() => SettingsViewModel.CopyText(_owner.App.Diagnostics(Id)));
        RereadCommand = new RelayCommand(() => _owner.App.RereadCapabilities(Id));
    }

    public string Id { get; }

    public string Title { get => _title; private set => SetProperty(ref _title, value); }

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public string Capabilities { get => _capabilities; private set => SetProperty(ref _capabilities, value); }

    public IRelayCommand CopyCommand { get; }

    public IRelayCommand RereadCommand { get; }

    public string CopyName => $"Copy diagnostics for {_title}";

    public string RereadName => $"Read the capabilities string of {_title} again";

    internal void Sync(MonitorSnapshot m)
    {
        if (Title != _owner.App.MonitorName(m.Id))
        {
            Title = _owner.App.MonitorName(m.Id);
            OnPropertyChanged(nameof(CopyName));
            OnPropertyChanged(nameof(RereadName));
        }
        var parts = new List<string> { m.Id };
        if (SetupAdvisor.DescribeConnection(m.Connection) is { } c) parts.Add(c);
        parts.Add(m.CurrentInput is uint v ? $"Now on {InputCatalog.FormatValue(v)}" : "Current input unknown");
        Summary = string.Join(" · ", parts);
        Capabilities = m.CapabilitiesText
            ?? (m.CapabilitiesFailed ? "The monitor didn't send its capabilities string." : m.Health == MonitorHealth.Unavailable ? "Windows can't talk to this monitor." : "Reading…");
    }
}

/// <summary>A screens-off suggestion in the setup window. Kept for the whole step, even once it's turned on.</summary>
public sealed class ScreensOffSuggestionViewModel : ObservableObject
{
    private readonly MonitorEditViewModel _monitor;
    private readonly uint _value;
    private bool _isOn;

    public ScreensOffSuggestionViewModel(MonitorEditViewModel monitor, uint value)
    {
        _monitor = monitor;
        _value = value;
        Title = monitor.Title;
        Explanation = monitor.Suggestion ?? "";
        ToggleName = $"Turn the screens off when {monitor.Title} switches away from {InputCatalog.GetPortName(value)}";
    }

    public string Title { get; }

    public string Explanation { get; }

    public string ToggleName { get; }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!SetProperty(ref _isOn, value)) return;
            uint? v = value ? _value : null;
            _monitor.Edit(ms => ms.BlankScreensWhenLeaving = v);
        }
    }
}
