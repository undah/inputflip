using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Switchboard.ViewModels;

namespace Switchboard.Controls;

/// <summary>A monitor's inputs with icon, name, show, and reorder controls. Used in Settings and in the first-run setup.</summary>
public sealed partial class InputListEditor : UserControl
{
    public static readonly DependencyProperty MonitorProperty = DependencyProperty.Register(
        nameof(Monitor), typeof(MonitorEditViewModel), typeof(InputListEditor),
        new PropertyMetadata(null, (d, _) => ((InputListEditor)d).Bindings.Update()));

    public InputListEditor() => InitializeComponent();

    public MonitorEditViewModel? Monitor
    {
        get => (MonitorEditViewModel?)GetValue(MonitorProperty);
        set => SetValue(MonitorProperty, value);
    }
}
