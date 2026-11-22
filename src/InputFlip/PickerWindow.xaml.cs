using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using InputFlip.Core.Actions;
using InputFlip.Native;
using Windows.Graphics;
using Windows.System;

namespace InputFlip;

/// <summary>
/// The pop-up picker: a small window next to the mouse listing scenes and every shown input. Keyboard first: it opens
/// with the likely next choice selected, arrows move, Enter or 1 to 9 picks, Esc closes. Clicking elsewhere closes it.
/// </summary>
public sealed partial class PickerWindow : Window
{
    private const double WidthDip = 300, RowDip = 48, HeaderDip = 30, ChromeDip = 52, MaxHeightDip = 560;

    private readonly Action<PickerItem> _pick;
    private readonly OverlappedPresenter _presenter;
    private readonly List<(Button Button, PickerItem Item)> _rows = new();
    private bool _closingForGood;

    public PickerWindow(Action<PickerItem> pick)
    {
        _pick = pick;
        InitializeComponent();
        _presenter = OverlappedPresenter.Create();
        _presenter.IsResizable = false;
        _presenter.IsMaximizable = false;
        _presenter.IsMinimizable = false;
        _presenter.IsAlwaysOnTop = true;
        _presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(_presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Title = "InputFlip picker";
        WindowChrome.RoundCorners(this);

        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) AppWindow.Hide();
        };
        AppWindow.Closing += (_, e) =>
        {
            if (_closingForGood) return;
            e.Cancel = true;
            AppWindow.Hide();
        };
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);
    }

    /// <param name="at">The mouse, in physical screen pixels.</param>
    public void Open(IReadOnlyList<PickerItem> items, int selected, (int X, int Y) at)
    {
        Build(items);

        var display = DisplayArea.GetFromPoint(new PointInt32(at.X, at.Y), DisplayAreaFallback.Nearest);
        var work = display.WorkArea;
        // Size for that display's scaling: move there first, then read the scale.
        AppWindow.Move(new PointInt32(work.X + work.Width / 2, work.Y + work.Height / 2));
        double scale = WindowChrome.Scale(this);
        int groups = items.Select(i => i.Group).Distinct().Count();
        double heightDip = Math.Min(MaxHeightDip, ChromeDip + groups * HeaderDip + items.Count * RowDip);
        int w = (int)Math.Round(WidthDip * scale), h = Math.Min((int)Math.Round(heightDip * scale), work.Height);
        // Centered just below the mouse, kept on the screen.
        int x = Math.Clamp(at.X - w / 2, work.X, work.X + work.Width - w);
        int y = Math.Clamp(at.Y + (int)(12 * scale), work.Y, work.Y + work.Height - h);
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));

        AppWindow.Show(true);
        Activate();
        Win32.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (_rows.Count > 0) _rows[Math.Clamp(selected, 0, _rows.Count - 1)].Button.Focus(FocusState.Keyboard);
    }

    private void Build(IReadOnlyList<PickerItem> items)
    {
        Items.Children.Clear();
        _rows.Clear();
        string? group = null;
        foreach (var item in items)
        {
            if (item.Group != group)
            {
                group = item.Group;
                Items.Children.Add(new TextBlock
                {
                    Text = group,
                    Style = (Style)Application.Current.Resources["MonitorHeaderStyle"],
                    Margin = new Thickness(4, _rows.Count == 0 ? 2 : 8, 0, 2),
                });
            }
            int number = _rows.Count + 1;

            var icon = new FontIcon
            {
                Glyph = Ui.IconGlyph(item.Icon),
                FontSize = 16,
                Style = Ui.TileIconStyle(item.IsCurrent),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = item.Label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(item.Detail))
                label.Children.Add(new TextBlock { Text = item.Detail, FontSize = 12, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
            var key = new TextBlock
            {
                Text = number <= 9 ? number.ToString() : "",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(label, 1);
            Grid.SetColumn(key, 2);
            grid.Children.Add(icon);
            grid.Children.Add(label);
            grid.Children.Add(key);

            var button = new Button
            {
                Style = Ui.TileStyle(item.IsCurrent),
                MinHeight = 44,
                Padding = new Thickness(12, 6, 12, 6),
                CornerRadius = new CornerRadius(12),
                Content = grid,
            };
            AutomationProperties.SetName(button, item.IsCurrent ? $"{item.Label}, {item.Group}, showing now" : $"{item.Label}, {item.Group}");
            if (number <= 9) AutomationProperties.SetAcceleratorKey(button, number.ToString());
            var picked = item;
            button.Click += (_, _) => Choose(picked);
            _rows.Add((button, item));
            Items.Children.Add(button);
        }
    }

    private void Choose(PickerItem item)
    {
        AppWindow.Hide();
        _pick(item);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            AppWindow.Hide();
            e.Handled = true;
            return;
        }
        int digit = e.Key is >= VirtualKey.Number1 and <= VirtualKey.Number9 ? e.Key - VirtualKey.Number1
            : e.Key is >= VirtualKey.NumberPad1 and <= VirtualKey.NumberPad9 ? e.Key - VirtualKey.NumberPad1 : -1;
        if (digit >= 0 && digit < _rows.Count)
        {
            e.Handled = true;
            Choose(_rows[digit].Item);
        }
    }

    public void CloseForGood()
    {
        _closingForGood = true;
        Close();
    }
}
