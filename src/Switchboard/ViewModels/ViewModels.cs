using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Switchboard.Core.Settings;

namespace Switchboard.ViewModels;

/// <summary>What the view models ask the app to do. Implemented by <see cref="AppController"/>.</summary>
public interface ISwitchboardActions
{
    Task SwitchAsync(string monitorId, uint value);

    void RenameInput(string monitorId, uint value, string? name);

    void ToggleInputHidden(string monitorId, uint value);

    void SetInputIcon(string monitorId, uint value, string icon);

    void ToggleMonitorHidden(string monitorId);

    void ToggleBlankScreensWhenLeaving(string monitorId, uint value);

    void SetPcInput(string monitorId, uint value);

    void OpenSettings(string? page, string? monitorId = null);
}

public sealed class InputViewModel : ObservableObject
{
    private readonly ISwitchboardActions _actions;
    private string _name = "";
    private string _portName = "";
    private string _icon = "display";
    private string? _hotkey;
    private bool _isCurrent;
    private bool _isHidden;
    private bool _isBusy;
    private bool _isRenaming;

    public InputViewModel(ISwitchboardActions actions, string monitorId, uint value)
    {
        _actions = actions;
        MonitorId = monitorId;
        Value = value;
        SwitchCommand = new AsyncRelayCommand(() => _actions.SwitchAsync(MonitorId, Value));
        RenameCommand = new RelayCommand(() => IsRenaming = true);
        HideCommand = new RelayCommand(() => _actions.ToggleInputHidden(MonitorId, Value));
        SetIconCommand = new RelayCommand<string>(icon => { if (icon is not null) _actions.SetInputIcon(MonitorId, Value, icon); });
        BlankWhenLeavingCommand = new RelayCommand(() => _actions.ToggleBlankScreensWhenLeaving(MonitorId, Value));
        SetPcInputCommand = new RelayCommand(() => _actions.SetPcInput(MonitorId, Value));
    }

    public IRelayCommand SetPcInputCommand { get; }

    private bool _isThisPc;

    /// <summary>This PC is plugged into this input (found from the cable type, or set from the menu).</summary>
    public bool IsThisPc
    {
        get => _isThisPc;
        set
        {
            if (SetProperty(ref _isThisPc, value))
            {
                OnPropertyChanged(nameof(IsNotThisPc));
                OnSubtitleChanged();
            }
        }
    }

    public bool IsNotThisPc => !_isThisPc;

    public IRelayCommand BlankWhenLeavingCommand { get; }

    private bool _blankWhenLeaving;

    /// <summary>This is the PC's input on a monitor that only switches away from it with the screens off.</summary>
    public bool BlankWhenLeaving
    {
        get => _blankWhenLeaving;
        set
        {
            if (SetProperty(ref _blankWhenLeaving, value)) OnPropertyChanged(nameof(BlankWhenLeavingMenuText));
        }
    }

    public string BlankWhenLeavingMenuText => _blankWhenLeaving
        ? "Stop turning screens off when leaving this input"
        : "Turn screens off when leaving this input";

    public string MonitorId { get; }

    public uint Value { get; }

    public IAsyncRelayCommand SwitchCommand { get; }

    public IRelayCommand RenameCommand { get; }

    public IRelayCommand HideCommand { get; }

    public IRelayCommand<string> SetIconCommand { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value)) OnSubtitleChanged();
        }
    }

    /// <summary>"HDMI 1"; shown under the name when the name is something else.</summary>
    public string PortName
    {
        get => _portName;
        set
        {
            if (SetProperty(ref _portName, value)) OnSubtitleChanged();
        }
    }

    /// <summary>The port when the name is something else, and "This PC" on the PC's input.</summary>
    public string Subtitle
    {
        get
        {
            if (_isBusy) return "Switching…";
            string port = _name == _portName ? "" : _portName;
            if (!_isThisPc) return port;
            return port.Length == 0 ? "This PC" : $"{port} · This PC";
        }
    }

    public bool HasSubtitle => Subtitle.Length > 0;

    public string Icon { get => _icon; set => SetProperty(ref _icon, value); }

    public string? Hotkey
    {
        get => _hotkey;
        set
        {
            if (SetProperty(ref _hotkey, value)) OnPropertyChanged(nameof(HotkeyText));
        }
    }

    public string HotkeyText => _hotkey ?? "";

    public bool IsCurrent { get => _isCurrent; set => SetProperty(ref _isCurrent, value); }

    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (SetProperty(ref _isHidden, value)) OnPropertyChanged(nameof(HideMenuText));
        }
    }

    public string HideMenuText => _isHidden ? "Show this input" : "Hide this input";

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value)) OnSubtitleChanged();
        }
    }

    private void OnSubtitleChanged()
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(HasSubtitle));
    }

    public bool IsRenaming { get => _isRenaming; set => SetProperty(ref _isRenaming, value); }

    public string AccessibleName => IsCurrent ? $"{Name}, showing now" : $"Switch to {Name}";

    /// <summary>Narrator reads this after the name: the port, the shortcut and how to get to more options.</summary>
    public string AccessibleHelp
    {
        get
        {
            var parts = new List<string>();
            if (_name != _portName) parts.Add(_portName);
            if (_isThisPc) parts.Add("This PC is plugged in here");
            if (_hotkey is not null) parts.Add($"Shortcut {_hotkey}");
            if (_isHidden) parts.Add("Hidden");
            parts.Add("Press Shift+F10 for more options");
            return string.Join(". ", parts) + ".";
        }
    }

    public string Tooltip => _hotkey is null ? _portName : $"{_portName} ({_hotkey})";

    public string RenameAccessibleName => $"New name for {_portName}";

    public void CommitRename(string? text)
    {
        if (!IsRenaming) return;
        IsRenaming = false;
        string? trimmed = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        // Typing the port name back means "use the default".
        if (trimmed == PortName) trimmed = null;
        _actions.RenameInput(MonitorId, Value, trimmed);
    }

    public void CancelRename() => IsRenaming = false;

    internal void Apply(DisplayInput input)
    {
        Name = input.Name;
        PortName = input.PortName;
        Icon = input.Icon;
        Hotkey = input.Hotkey;
        IsCurrent = input.IsCurrent;
        IsHidden = input.Hidden;
        BlankWhenLeaving = input.BlankScreensWhenLeaving;
        IsThisPc = input.IsThisPc;
        OnPropertyChanged(nameof(AccessibleName));
        OnPropertyChanged(nameof(AccessibleHelp));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(RenameAccessibleName));
    }
}

public sealed class MonitorViewModel : ObservableObject
{
    private readonly ISwitchboardActions _actions;
    private string _name = "";
    private string? _message;
    private bool _isHidden;

    public MonitorViewModel(ISwitchboardActions actions, string id)
    {
        _actions = actions;
        Id = id;
        HideCommand = new RelayCommand(() => _actions.ToggleMonitorHidden(Id));
        SettingsCommand = new RelayCommand(() => _actions.OpenSettings("monitors", Id));
        Inputs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasInputs));
    }

    public IRelayCommand SettingsCommand { get; }

    public bool HasInputs => Inputs.Count > 0;

    public string Id { get; }

    public ObservableCollection<InputViewModel> Inputs { get; } = new();

    public IRelayCommand HideCommand { get; }

    public string Name { get => _name; set => SetProperty(ref _name, value); }

    /// <summary>Shown instead of, or above, the inputs when something's wrong or still loading.</summary>
    public string? Message
    {
        get => _message;
        set
        {
            if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(_message);

    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (SetProperty(ref _isHidden, value)) OnPropertyChanged(nameof(HideMenuText));
        }
    }

    public string HideMenuText => _isHidden ? "Show this monitor" : "Hide this monitor";

    internal void SyncInputs(IReadOnlyList<DisplayInput> inputs)
    {
        for (int i = 0; i < inputs.Count; i++)
        {
            var existing = Inputs.FirstOrDefault(vm => vm.Value == inputs[i].Value);
            if (existing is null)
            {
                existing = new InputViewModel(_actions, Id, inputs[i].Value);
                Inputs.Insert(i, existing);
            }
            else
            {
                int at = Inputs.IndexOf(existing);
                if (at != i) Inputs.Move(at, i);
            }
            existing.Apply(inputs[i]);
        }
        while (Inputs.Count > inputs.Count) Inputs.RemoveAt(Inputs.Count - 1);
    }
}

public sealed class MainViewModel : ObservableObject
{
    private string _status = "";
    private bool _showHidden;
    private int _hiddenCount;
    private bool _alwaysOnTop;
    private bool _isRefreshing;

    public ObservableCollection<MonitorViewModel> Monitors { get; } = new();

    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
                OnPropertyChanged(nameof(HasFooter));
            }
        }
    }

    public bool HasStatus => !string.IsNullOrEmpty(_status);

    public bool HasFooter => HasStatus || HasHiddenToggle;

    public bool ShowHidden
    {
        get => _showHidden;
        set
        {
            if (SetProperty(ref _showHidden, value))
            {
                OnPropertyChanged(nameof(HiddenToggleText));
                OnPropertyChanged(nameof(HasHiddenToggle));
                OnPropertyChanged(nameof(HasFooter));
            }
        }
    }

    public int HiddenCount
    {
        get => _hiddenCount;
        set
        {
            if (SetProperty(ref _hiddenCount, value))
            {
                OnPropertyChanged(nameof(HiddenToggleText));
                OnPropertyChanged(nameof(HasHiddenToggle));
                OnPropertyChanged(nameof(HasFooter));
            }
        }
    }

    public bool HasHiddenToggle => _hiddenCount > 0 || _showHidden;

    public string HiddenToggleText => _showHidden ? "Done" : $"Show hidden ({_hiddenCount})";

    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set
        {
            if (SetProperty(ref _alwaysOnTop, value)) OnPropertyChanged(nameof(AlwaysOnTopTooltip));
        }
    }

    public string AlwaysOnTopTooltip => _alwaysOnTop ? "Stop keeping on top" : "Keep on top of other windows";

    public bool IsRefreshing { get => _isRefreshing; set => SetProperty(ref _isRefreshing, value); }

    public bool IsEmpty => Monitors.Count == 0;

    internal void NotifyEmptyChanged() => OnPropertyChanged(nameof(IsEmpty));
}
