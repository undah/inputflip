using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using InputFlip.Core.Hotkeys;
using InputFlip.ViewModels;
using Windows.System;
using Windows.UI.Core;

namespace InputFlip.Controls;

/// <summary>
/// Shows a shortcut as key caps. Click (or Enter) and press the new combination; Esc cancels, Backspace removes it.
/// While it listens, InputFlip's global shortcuts are switched off, so pressing one records it instead of
/// switching a monitor. Conflicts are refused with a message saying who has the shortcut.
/// </summary>
public sealed partial class ShortcutRecorder : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(ShortcutItemViewModel), typeof(ShortcutRecorder),
        new PropertyMetadata(null, (d, e) => ((ShortcutRecorder)d).OnItemChanged(e.OldValue as ShortcutItemViewModel, e.NewValue as ShortcutItemViewModel)));

    private bool _recording;
    private string? _message;
    private bool _messageIsError;

    public ShortcutRecorder()
    {
        InitializeComponent();
        Unloaded += (_, _) => StopRecording();
        Update();
    }

    public ShortcutItemViewModel? Item
    {
        get => (ShortcutItemViewModel?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private void OnItemChanged(ShortcutItemViewModel? oldItem, ShortcutItemViewModel? newItem)
    {
        if (oldItem is not null) oldItem.PropertyChanged -= OnItemPropertyChanged;
        if (newItem is not null) newItem.PropertyChanged += OnItemPropertyChanged;
        _message = null;
        Update();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recording) StopRecording();
        else StartRecording();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        Item?.Clear();
        _message = null;
        Update();
    }

    private void RecordButton_LostFocus(object sender, RoutedEventArgs e) => StopRecording();

    private void StartRecording()
    {
        if (Item is null || _recording) return;
        _recording = true;
        Item.BeginRecording();
        _message = "Press the new shortcut. Esc cancels, Backspace removes it.";
        _messageIsError = false;
        Update();
    }

    private void StopRecording()
    {
        if (!_recording) return;
        _recording = false;
        Item?.EndRecording();
        if (!_messageIsError) _message = null;
        Update();
    }

    private void RecordButton_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording || Item is null) return;
        var modifiers = CurrentModifiers();
        uint vk = (uint)e.Key;

        if (modifiers == HotkeyModifiers.None)
        {
            switch (e.Key)
            {
                case VirtualKey.Escape:
                    e.Handled = true;
                    _messageIsError = false;
                    StopRecording();
                    return;
                case VirtualKey.Back:
                    e.Handled = true;
                    Item.Clear();
                    _messageIsError = false;
                    StopRecording();
                    return;
                case VirtualKey.Tab:
                    // Let Tab move on, so keyboard users aren't trapped.
                    _messageIsError = false;
                    StopRecording();
                    return;
            }
        }

        e.Handled = true;
        if (Hotkey.IsModifierKey(vk)) return; // wait for the key that goes with them

        if (!Hotkey.TryCreate(modifiers, vk, out var hotkey, out string? error))
        {
            ShowMessage(error, isError: true);
            return;
        }

        string? problem = Item.TryAssign(hotkey);
        if (problem is not null)
        {
            ShowMessage(problem, isError: true);
            return;
        }
        _messageIsError = false;
        StopRecording();
        ShowMessage($"Saved. {hotkey} now works anywhere.", isError: false);
    }

    private void ShowMessage(string text, bool isError)
    {
        _message = text;
        _messageIsError = isError;
        Update();
        DispatcherQueue.TryEnqueue(() =>
            FrameworkElementAutomationPeer.FromElement(isError ? ErrorText : MessageText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged));
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
        var m = HotkeyModifiers.None;
        if (Down(VirtualKey.Control)) m |= HotkeyModifiers.Control;
        if (Down(VirtualKey.Menu)) m |= HotkeyModifiers.Alt;
        if (Down(VirtualKey.Shift)) m |= HotkeyModifiers.Shift;
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) m |= HotkeyModifiers.Win;
        return m;
    }

    private void Update()
    {
        string? text = Item?.Text;
        string title = Item?.Title ?? "Shortcut";
        bool hasShortcut = !string.IsNullOrWhiteSpace(text);

        if (_recording)
        {
            Keys.ItemsSource = null;
            Placeholder.Text = "Press keys…";
            Placeholder.Visibility = Visibility.Visible;
            AutomationProperties.SetName(RecordButton, $"Recording a shortcut for {title}. Press the keys now, or Escape to cancel.");
        }
        else
        {
            Keys.ItemsSource = hasShortcut ? text!.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : null;
            Placeholder.Text = "Not set";
            Placeholder.Visibility = hasShortcut ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(RecordButton, hasShortcut ? $"{title} shortcut: {text}. Press to change." : $"{title}: no shortcut. Press to set one.");
        }
        ToolTipService.SetToolTip(RecordButton, hasShortcut ? "Click, then press a new shortcut" : "Click, then press a shortcut");

        ClearButton.Visibility = hasShortcut && !_recording ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(ClearButton, $"Remove the {title} shortcut");
        ToolTipService.SetToolTip(ClearButton, "Remove shortcut");

        // A saved shortcut that doesn't work (taken by another app, duplicate) shows its problem until it's changed.
        string? message = _message ?? Item?.Problem;
        bool isError = _message is not null ? _messageIsError : Item?.Problem is not null;
        MessageText.Text = isError ? "" : message ?? "";
        MessageText.Visibility = message is not null && !isError ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = isError ? message ?? "" : "";
        ErrorText.Visibility = message is not null && isError ? Visibility.Visible : Visibility.Collapsed;
    }
}
