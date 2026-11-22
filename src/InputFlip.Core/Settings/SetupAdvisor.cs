using InputFlip.Core.Interop;
using InputFlip.Core.Monitors;

namespace InputFlip.Core.Settings;

/// <summary>Plain-language status and suggestions for the first-run setup and the settings window.</summary>
public static class SetupAdvisor
{
    /// <summary>
    /// The input to turn the screens-off switch on for, or null. Suggested for monitors that answer reads over
    /// DisplayPort but never send a capabilities string: the MSI G27CQ4's pattern, where switch commands are
    /// ignored while it shows this PC (docs/ddc-ci-notes.md).
    /// </summary>
    public static uint? SuggestScreensOff(MonitorSnapshot monitor, MonitorSettings? settings)
    {
        if (settings?.BlankScreensWhenLeaving is not null) return null;
        if (monitor.Connection != ConnectionKind.DisplayPort) return null;
        if (!monitor.HasAnswered || !monitor.CapabilitiesFailed || monitor.AdvertisedInputs is not null) return null;
        // The PC's input: what the monitor reports, if that's a DisplayPort value (its reads can be wrong), otherwise DisplayPort 1.
        return monitor.CurrentInput is uint current && (current & 0xFF) is 0x0F or 0x10 ? current & 0xFF : 0x0F;
    }

    /// <summary>One line on whether the monitor answers DDC/CI.</summary>
    public static string Describe(MonitorSnapshot m) => m.Health switch
    {
        MonitorHealth.Unavailable => "Windows can't control this monitor.",
        MonitorHealth.Unknown => "Checking…",
        MonitorHealth.NotResponding when m.HasAnswered => "Answers only some of the time.",
        MonitorHealth.NotResponding => "Not answering. Turn on DDC/CI in the monitor's on-screen menu.",
        _ when m.AdvertisedInputs is { Count: var n } => n == 1 ? "Answers. Lists 1 input." : $"Answers. Lists {n} inputs.",
        _ when m.CapabilitiesFailed => "Answers, but didn't list its inputs, so the usual ones are shown.",
        _ => "Answers. Reading its inputs…",
    };

    /// <summary>"DisplayPort", "HDMI"... or null when Windows doesn't say.</summary>
    public static string? DescribeConnection(ConnectionKind kind) => kind switch
    {
        ConnectionKind.DisplayPort => "DisplayPort",
        ConnectionKind.Hdmi => "HDMI",
        ConnectionKind.Dvi => "DVI",
        ConnectionKind.Vga => "VGA",
        _ => null,
    };
}
