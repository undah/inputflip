using InputFlip.Core.Monitors;
using InputFlip.Core.Settings;

namespace InputFlip.Core.Tests;

/// <summary>The next-input shortcut's monitor, and where the mouse goes while a monitor shows another device.</summary>
public sealed class SwitchingTests
{
    // The owner's layout on 2026-11-21: Acer, MSI (main), Acer, left to right.
    private static readonly ScreenRect LeftAcer = new(-1920, 304, 0, 1384);
    private static readonly ScreenRect Msi = new(0, 0, 2560, 1440);
    private static readonly ScreenRect RightAcer = new(2560, 304, 4480, 1384);

    private static MonitorSnapshot Monitor(string id, bool primary, ScreenRect bounds, uint[] inputs, MonitorHealth health = MonitorHealth.Responding) =>
        new(id, id, primary, bounds, health, inputs[0], inputs, false, HasAnswered: health == MonitorHealth.Responding);

    private static AppSettings OwnerSettings()
    {
        var settings = new AppSettings();
        settings.Monitors["MSI"] = new MonitorSettings
        {
            PcInput = 0x12,
            Inputs =
            {
                new InputSettings { Value = 0x12 },
                new InputSettings { Value = 0x11, Name = "PS5" },
                new InputSettings { Value = 0x0F, Hidden = true },
                new InputSettings { Value = 0x10, Hidden = true },
            },
        };
        settings.Monitors["ACER"] = new MonitorSettings { Hidden = true };
        return settings;
    }

    [Fact]
    public void Cycle_skips_a_hidden_main_display()
    {
        var monitors = new[]
        {
            Monitor("ACER", primary: true, LeftAcer, [0x11, 0x01, 0x0F]),
            Monitor("MSI", primary: false, Msi, [0x12, 0x11, 0x0F, 0x10]),
        };
        Assert.Equal("MSI", InputLayout.PickCycleMonitor(monitors, OwnerSettings())!.Id);
    }

    [Fact]
    public void Cycle_prefers_the_main_display()
    {
        var settings = OwnerSettings();
        settings.Monitors["ACER"].Hidden = false;
        var monitors = new[]
        {
            Monitor("ACER", primary: false, LeftAcer, [0x11, 0x01]),
            Monitor("MSI", primary: true, Msi, [0x12, 0x11]),
        };
        Assert.Equal("MSI", InputLayout.PickCycleMonitor(monitors, settings)!.Id);
    }

    [Fact]
    public void Cycle_skips_monitors_with_one_input_shown_or_no_answer()
    {
        var settings = OwnerSettings();
        settings.Monitors["MSI"].Inputs.Single(i => i.Value == 0x11).Hidden = true;
        var monitors = new[]
        {
            Monitor("MSI", primary: true, Msi, [0x12, 0x11, 0x0F, 0x10]),
            Monitor("OTHER", primary: false, RightAcer, [0x11, 0x12], MonitorHealth.NotResponding),
        };
        Assert.Null(InputLayout.PickCycleMonitor(monitors, settings));
    }

    [Fact]
    public void Toggle_flips_between_the_two_shown_inputs()
    {
        var settings = OwnerSettings();
        var msi = Monitor("MSI", primary: true, Msi, [0x12, 0x11, 0x0F, 0x10]);
        var onPc = InputLayout.Build(msi with { CurrentInput = 0x12 }, settings.Monitors["MSI"]).Where(i => !i.Hidden).ToList();
        Assert.Equal(0x11u, InputLayout.Next(onPc)!.Value);
        var onPs5 = InputLayout.Build(msi with { CurrentInput = 0x11 }, settings.Monitors["MSI"]).Where(i => !i.Hidden).ToList();
        Assert.Equal(0x12u, InputLayout.Next(onPs5)!.Value);
    }

    private static readonly (ScreenRect, bool)[] OwnerScreens = [(LeftAcer, false), (Msi, true), (RightAcer, false)];

    [Fact]
    public void Mouse_parks_on_switchboards_window()
    {
        var window = new ScreenRect(3170, 389, 3550, 789);
        Assert.Equal((3360, 589), MouseParking.Spot(Msi, OwnerScreens, window));
    }

    [Fact]
    public void Mouse_parks_on_another_screen_when_the_window_is_on_the_same_monitor_or_minimized()
    {
        // The main display is the one leaving, so the first other screen.
        Assert.Equal((-960, 844), MouseParking.Spot(Msi, OwnerScreens, new ScreenRect(100, 100, 480, 500)));
        Assert.Equal((-960, 844), MouseParking.Spot(Msi, OwnerScreens, null));
        // An Acer leaving: the main display.
        Assert.Equal((1280, 720), MouseParking.Spot(RightAcer, OwnerScreens, null));
    }

    [Fact]
    public void Mouse_stays_when_there_is_nowhere_else()
    {
        Assert.Null(MouseParking.Spot(Msi, [(Msi, true)], null));
        // A duplicated display shows the same picture, so it doesn't count.
        Assert.Null(MouseParking.Spot(Msi, [(Msi, true), (Msi, false)], null));
    }

    [Fact]
    public void Contains_uses_half_open_edges()
    {
        Assert.True(MouseParking.Contains(Msi, (0, 0)));
        Assert.True(MouseParking.Contains(Msi, (2559, 1439)));
        Assert.False(MouseParking.Contains(Msi, (2560, 500)));
        Assert.False(MouseParking.Contains(Msi, (-1, 500)));
    }
}
