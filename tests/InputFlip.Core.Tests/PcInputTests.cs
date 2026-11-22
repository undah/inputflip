using InputFlip.Core.Interop;
using InputFlip.Core.Monitors;
using InputFlip.Core.Settings;

namespace InputFlip.Core.Tests;

/// <summary>Finding the input this PC is on, modelled on the owner's three monitors.</summary>
public sealed class PcInputTests
{
    public PcInputTests()
    {
        Log.FilePath = Path.Combine(Path.GetTempPath(), "switchboard-tests-pc-input.log");
    }

    private static MonitorSnapshot Snapshot(ConnectionKind connection, uint? current, uint[]? advertised, string id = "M") =>
        new(id, "Monitor", false, default, MonitorHealth.Responding, current, advertised, false, connection);

    private static uint? Detect(MonitorSnapshot m, MonitorSettings? s = null) =>
        PcInputDetector.Detect(m, s, InputLayout.Build(m, s).Select(i => i.Value).ToList());

    [Fact]
    public void Acer_on_hdmi_has_one_port_that_fits()
    {
        // ACR 0673: lists VGA, DisplayPort (which it doesn't have) and HDMI; Windows says HDMI.
        var acer = Snapshot(ConnectionKind.Hdmi, current: 0x11, advertised: [0x01, 0x0F, 0x11]);
        Assert.Equal(0x11u, Detect(acer));
    }

    [Fact]
    public void Two_hdmi_ports_behind_a_dvi_cable_use_the_input_on_screen()
    {
        // ACR 06F0: HDMI 1, VGA, HDMI 2; Windows reports the DVI-to-HDMI cable as DVI.
        Assert.Equal(0x11u, Detect(Snapshot(ConnectionKind.Dvi, current: 0x11, advertised: [0x01, 0x11, 0x12])));
        Assert.Equal(0x12u, Detect(Snapshot(ConnectionKind.Dvi, current: 0x12, advertised: [0x01, 0x11, 0x12])));
    }

    [Fact]
    public void Two_ports_that_fit_and_another_input_on_screen_stays_unknown()
    {
        var showingVga = Snapshot(ConnectionKind.Hdmi, current: 0x01, advertised: [0x01, 0x11, 0x12]);
        Assert.Null(Detect(showingVga));

        var notAnswering = Snapshot(ConnectionKind.Hdmi, current: null, advertised: [0x01, 0x11, 0x12]) with { Health = MonitorHealth.NotResponding };
        Assert.Null(Detect(notAnswering));
    }

    [Fact]
    public void Hidden_inputs_and_ones_marked_as_a_console_are_left_out()
    {
        var m = Snapshot(ConnectionKind.DisplayPort, current: null, advertised: [0x0F, 0x10, 0x11, 0x12]);
        var hidden = new MonitorSettings { Inputs = { new InputSettings { Value = 0x10, Hidden = true } } };
        Assert.Equal(0x0Fu, Detect(m, hidden));

        var hdmi = Snapshot(ConnectionKind.Hdmi, current: 0x12, advertised: [0x11, 0x12]);
        var console = new MonitorSettings { Inputs = { new InputSettings { Value = 0x12, Icon = "console" } } };
        Assert.Equal(0x11u, Detect(hdmi, console));
    }

    [Fact]
    public void Waits_for_the_input_list_before_guessing()
    {
        // Only the current input is known: it might be a PS5 on the second HDMI port.
        var early = Snapshot(ConnectionKind.Hdmi, current: 0x12, advertised: null);
        Assert.Null(Detect(early));
    }

    [Fact]
    public void Unknown_cable_type_finds_nothing()
    {
        Assert.Null(Detect(Snapshot(ConnectionKind.Unknown, current: 0x11, advertised: [0x11])));
    }

    [Fact]
    public void Usb_c_counts_as_displayport()
    {
        Assert.True(PcInputDetector.Fits(ConnectionKind.DisplayPort, 0x1B));
        Assert.False(PcInputDetector.Fits(ConnectionKind.DisplayPort, 0x11));
        Assert.True(PcInputDetector.Fits(ConnectionKind.Dvi, 0x12));
        Assert.True(PcInputDetector.Fits(ConnectionKind.Hdmi, 0x0111)); // vendor bits in the high byte
    }

    [Fact]
    public void Learn_remembers_the_input_so_it_stays_when_the_monitor_shows_something_else()
    {
        var settings = new AppSettings();
        settings.Monitors["M"] = new MonitorSettings { Inputs = { new() { Value = 0x01 }, new() { Value = 0x11 }, new() { Value = 0x12 } } };

        Assert.True(PcInputDetector.Learn(settings, [Snapshot(ConnectionKind.Dvi, current: 0x11, advertised: [0x01, 0x11, 0x12])]));
        Assert.Equal(0x11u, settings.Monitors["M"].PcInput);

        // Later the monitor shows the PS5 on HDMI 2: nothing changes.
        var showingPs5 = Snapshot(ConnectionKind.Dvi, current: 0x12, advertised: [0x01, 0x11, 0x12]);
        Assert.False(PcInputDetector.Learn(settings, [showingPs5]));
        var layout = InputLayout.Build(showingPs5, settings.Monitors["M"]);
        Assert.True(layout.Single(i => i.Value == 0x11).IsThisPc);
        Assert.False(layout.Single(i => i.Value == 0x12).IsThisPc);
    }

    [Fact]
    public void Screens_off_input_counts_as_the_pc_and_is_not_overwritten()
    {
        // The MSI: the screens-off input is the PC's by definition.
        var settings = new AppSettings();
        settings.Monitors["M"] = new MonitorSettings { BlankScreensWhenLeaving = 0x0F };
        var msi = Snapshot(ConnectionKind.DisplayPort, current: 0x11, advertised: [0x0F, 0x10, 0x11, 0x12]);

        Assert.False(PcInputDetector.Learn(settings, [msi]));
        Assert.Null(settings.Monitors["M"].PcInput);
        Assert.True(InputLayout.Build(msi, settings.Monitors["M"]).Single(i => i.Value == 0x0F).IsThisPc);
    }

    [Fact]
    public void Set_pc_input_wins_and_gets_the_pc_icon_unless_one_was_chosen()
    {
        var m = Snapshot(ConnectionKind.Hdmi, current: 0x11, advertised: [0x11, 0x12]);
        var ms = new MonitorSettings { PcInput = 0x12, BlankScreensWhenLeaving = 0x11 };
        var layout = InputLayout.Build(m, ms);
        Assert.True(layout.Single(i => i.Value == 0x12).IsThisPc);
        Assert.Equal("pc", layout.Single(i => i.Value == 0x12).Icon);
        Assert.Equal("display", layout.Single(i => i.Value == 0x11).Icon);

        ms.Inputs.Add(new InputSettings { Value = 0x12, Icon = "laptop" });
        Assert.Equal("laptop", InputLayout.Build(m, ms).Single(i => i.Value == 0x12).Icon);
    }

    [Fact]
    public void Pc_input_round_trips_as_hex_and_is_cleared_with_its_input()
    {
        var ms = new MonitorSettings { PcInput = 0x12, Inputs = { new() { Value = 0x12 } } };
        string json = System.Text.Json.JsonSerializer.Serialize(ms, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.Contains("\"pcInput\":\"0x12\"", json);

        SettingsEditor.RemoveInput(ms, 0x12);
        Assert.Null(ms.PcInput);
    }
}
