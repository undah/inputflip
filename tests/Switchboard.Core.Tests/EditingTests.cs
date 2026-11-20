using Microsoft.Win32;
using Switchboard.Core.Branding;
using Switchboard.Core.Hotkeys;
using Switchboard.Core.Interop;
using Switchboard.Core.Monitors;
using Switchboard.Core.Settings;

namespace Switchboard.Core.Tests;

public class SettingsEditorTests
{
    [Fact]
    public void Monitors_follow_settings_order_and_new_ones_go_last()
    {
        var settings = new AppSettings();
        settings.Monitors["ACR0673"] = new MonitorSettings();
        settings.Monitors["MSI3CB0"] = new MonitorSettings();
        var ordered = SettingsEditor.OrderMonitors([Snap("MSI3CB0"), Snap("NEW1"), Snap("acr0673")], settings);
        Assert.Equal(new[] { "acr0673", "MSI3CB0", "NEW1" }, ordered.Select(m => m.Id));
    }

    [Fact]
    public void Moving_a_monitor_reorders_the_file_and_stops_at_the_ends()
    {
        var settings = new AppSettings();
        settings.Monitors["A"] = new MonitorSettings();
        settings.Monitors["B"] = new MonitorSettings();
        settings.Monitors["C"] = new MonitorSettings();

        Assert.True(SettingsEditor.MoveMonitor(settings, "c", -1));
        Assert.Equal(new[] { "A", "C", "B" }, settings.Monitors.Keys);
        Assert.False(SettingsEditor.MoveMonitor(settings, "A", -1));
        Assert.False(SettingsEditor.MoveMonitor(settings, "B", +1));
        Assert.True(settings.Monitors.ContainsKey("a")); // still case-insensitive
    }

    [Fact]
    public void Moving_an_input_saves_inputs_that_were_only_shown()
    {
        // The MSI over DisplayPort: one saved input, the usual ports shown as a fallback.
        var ms = new MonitorSettings { Inputs = { new InputSettings { Value = 0x11, Name = "PC" } } };
        Assert.True(SettingsEditor.MoveInput(ms, [0x11, 0x0F, 0x10, 0x12], 0x12, -1));
        Assert.Equal(new uint[] { 0x11, 0x0F, 0x12, 0x10 }, ms.Inputs.Select(i => i.Value));
        Assert.Equal("PC", ms.Inputs[0].Name);
        Assert.False(SettingsEditor.MoveInput(ms, [0x11, 0x0F, 0x12, 0x10], 0x11, -1));
    }

    [Fact]
    public void Adding_an_input_checks_the_value()
    {
        var ms = new MonitorSettings { Inputs = { new InputSettings { Value = 0x11 }, new InputSettings { Value = 0x12, Hidden = true } } };

        Assert.Null(SettingsEditor.AddInput(ms, [0x11], "0x1b", out uint added));
        Assert.Equal(0x1Bu, added);
        Assert.Contains(ms.Inputs, i => i.Value == 0x1B);

        Assert.NotNull(SettingsEditor.AddInput(ms, [0x11], "HDMI", out _));
        Assert.NotNull(SettingsEditor.AddInput(ms, [0x11], "0", out _));
        Assert.Contains("already", SettingsEditor.AddInput(ms, [0x11], "17", out _));
        Assert.Contains("already", SettingsEditor.AddInput(ms, [0x0F], "0x0F", out _)); // shown, not saved

        // A hidden input comes back instead of being added twice.
        Assert.Null(SettingsEditor.AddInput(ms, [0x11], "12h", out _));
        Assert.False(ms.Inputs.Single(i => i.Value == 0x12).Hidden);
    }

    [Fact]
    public void Only_inputs_the_monitor_does_not_list_can_be_removed()
    {
        var monitor = Snap("X") with { AdvertisedInputs = [0x11, 0x12] };
        Assert.False(SettingsEditor.CanRemoveInput(monitor, 0x11));
        Assert.True(SettingsEditor.CanRemoveInput(monitor, 0x1B));
        Assert.True(SettingsEditor.CanRemoveInput(null, 0x11)); // not connected

        var ms = new MonitorSettings { BlankScreensWhenLeaving = 0x1B, Inputs = { new InputSettings { Value = 0x1B } } };
        Assert.True(SettingsEditor.RemoveInput(ms, 0x1B));
        Assert.Empty(ms.Inputs);
        Assert.Null(ms.BlankScreensWhenLeaving);
    }

    [Theory]
    [InlineData("Dark", "dark")]
    [InlineData(" light ", "light")]
    [InlineData("system", null)]
    [InlineData(null, null)]
    public void Theme_is_normalized(string? input, string? expected) =>
        Assert.Equal(expected, SettingsEditor.NormalizeTheme(input));

    internal static MonitorSnapshot Snap(string id) =>
        new(id, "Monitor", false, default, MonitorHealth.Responding, null, null, false);
}

public class ShortcutTests
{
    [Fact]
    public void Slots_cover_app_and_input_shortcuts()
    {
        var settings = new AppSettings();
        settings.Monitors["MSI3CB0-000000D1"] = new MonitorSettings();
        string key = ShortcutSlots.InputKey("MSI3CB0-000000D1", 0x12);

        Assert.True(ShortcutSlots.Set(settings, key, "Ctrl+Alt+Shift+2"));
        Assert.Equal("Ctrl+Alt+Shift+2", ShortcutSlots.Get(settings, key));
        Assert.Equal(new[] { ShortcutSlots.Show, ShortcutSlots.Cycle, key }, ShortcutSlots.All(settings).Select(s => s.Key));

        Assert.True(ShortcutSlots.Set(settings, ShortcutSlots.Cycle, " "));
        Assert.Null(settings.CycleHotkey);
        Assert.False(ShortcutSlots.Set(settings, ShortcutSlots.InputKey("UNKNOWN", 0x11), "Ctrl+Alt+1"));
    }

    [Fact]
    public void Input_keys_round_trip_with_odd_ids()
    {
        string key = ShortcutSlots.InputKey("display-DISPLAY1#2", 0x1B);
        Assert.True(ShortcutSlots.TryParseInputKey(key, out string id, out uint value));
        Assert.Equal("display-DISPLAY1#2", id);
        Assert.Equal(0x1Bu, value);
        Assert.False(ShortcutSlots.TryParseInputKey("cycle", out _, out _));
    }

    [Fact]
    public void Conflicts_are_found_however_the_shortcut_is_written()
    {
        var settings = new AppSettings { ShowHotkey = "Ctrl+Alt+Shift+S" };
        settings.Monitors["A"] = new MonitorSettings { Inputs = { new InputSettings { Value = 0x11, Hotkey = "shift+alt+ctrl+s" } } };

        Assert.True(Hotkey.TryParse("Ctrl+Shift+Alt+S", out var hotkey, out _));
        Assert.Equal(ShortcutSlots.Show, ShortcutSlots.FindOwner(settings, hotkey, ShortcutSlots.Cycle));
        Assert.Equal(ShortcutSlots.InputKey("A", 0x11), ShortcutSlots.FindOwner(settings, hotkey, ShortcutSlots.Show));

        var duplicates = ShortcutSlots.Duplicates(settings);
        Assert.Equal(ShortcutSlots.Show, duplicates[ShortcutSlots.InputKey("A", 0x11)]);
        Assert.Single(duplicates);
    }

    [Fact]
    public void Recorder_accepts_known_keys_with_a_modifier()
    {
        Assert.True(Hotkey.TryCreate(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x31, out var hotkey, out _));
        Assert.Equal("Ctrl+Alt+1", hotkey.ToString());
        Assert.True(Hotkey.TryCreate(HotkeyModifiers.None, 0x7C, out _, out _)); // F13 alone is fine

        Assert.False(Hotkey.TryCreate(HotkeyModifiers.Shift, 0x41, out _, out string? error));
        Assert.Contains("Ctrl, Alt or Win", error);
        Assert.False(Hotkey.TryCreate(HotkeyModifiers.Control, 0xBA, out _, out error)); // ; on US keyboards
        Assert.Contains("can't use that key", error);

        Assert.True(Hotkey.IsModifierKey(0xA2)); // left Ctrl
        Assert.False(Hotkey.IsModifierKey(0x41));
        Assert.True(Hotkey.SameShortcut("ctrl+alt+shift+i", "Ctrl+Shift+Alt+I"));
        Assert.False(Hotkey.SameShortcut("Ctrl+Alt+I", null));
    }
}

public class SetupAdvisorTests
{
    private static MonitorSnapshot Msi(ConnectionKind connection, uint? current, bool answered = true) =>
        new("MSI3CB0", "MSI G27CQ4", true, default, MonitorHealth.Responding, current, null, true, connection, null, answered);

    [Fact]
    public void Suggests_screens_off_for_a_DisplayPort_monitor_without_capabilities()
    {
        Assert.Equal(0x0Fu, SetupAdvisor.SuggestScreensOff(Msi(ConnectionKind.DisplayPort, 0x0F), null));
        Assert.Equal(0x10u, SetupAdvisor.SuggestScreensOff(Msi(ConnectionKind.DisplayPort, 0x10), null));
        // Its reads can be wrong (it once said 0x11 on DisplayPort): fall back to DisplayPort 1.
        Assert.Equal(0x0Fu, SetupAdvisor.SuggestScreensOff(Msi(ConnectionKind.DisplayPort, 0x11), null));
        Assert.Equal(0x0Fu, SetupAdvisor.SuggestScreensOff(Msi(ConnectionKind.DisplayPort, null), null));
    }

    [Fact]
    public void No_suggestion_when_it_does_not_fit()
    {
        Assert.Null(SetupAdvisor.SuggestScreensOff(Msi(ConnectionKind.Hdmi, 0x11), null));
        Assert.Null(SetupAdvisor.SuggestScreensOff(Msi(ConnectionKind.DisplayPort, 0x0F, answered: false), null));
        Assert.Null(SetupAdvisor.SuggestScreensOff(Msi(ConnectionKind.DisplayPort, 0x0F) with { AdvertisedInputs = [0x0F], CapabilitiesFailed = false }, null));
        Assert.Null(SetupAdvisor.SuggestScreensOff(Msi(ConnectionKind.DisplayPort, 0x0F), new MonitorSettings { BlankScreensWhenLeaving = 0x0F }));
    }

    [Fact]
    public void Describes_status_in_plain_words()
    {
        var m = SettingsEditorTests.Snap("A");
        Assert.Equal("Answers. Reading its inputs…", SetupAdvisor.Describe(m));
        Assert.Equal("Answers. Lists 3 inputs.", SetupAdvisor.Describe(m with { AdvertisedInputs = [1, 2, 3] }));
        Assert.Contains("DDC/CI", SetupAdvisor.Describe(m with { Health = MonitorHealth.NotResponding }));
        Assert.Equal("Answers only some of the time.", SetupAdvisor.Describe(m with { Health = MonitorHealth.NotResponding, HasAnswered = true }));
        Assert.Equal("DisplayPort", SetupAdvisor.DescribeConnection(DisplayConnections.Classify(10)));
        Assert.Equal("HDMI", SetupAdvisor.DescribeConnection(DisplayConnections.Classify(5)));
        Assert.Null(SetupAdvisor.DescribeConnection(DisplayConnections.Classify(16)));
    }
}

public class StartupTests
{
    [Fact]
    public void Options_parse_data_dir_and_flags()
    {
        var o = StartupOptions.Parse(["--data-dir", @"C:\temp\sb", "--no-switch", "--open=Settings:Monitors"]);
        Assert.Equal(@"C:\temp\sb", o.DataDirectory);
        Assert.True(o.NoSwitch);
        Assert.Equal("settings:monitors", o.Open);
        Assert.Empty(o.Problems);
    }

    [Fact]
    public void Environment_variable_sets_data_dir_and_argument_wins()
    {
        Assert.Equal(@"C:\env", StartupOptions.Parse([], @"C:\env").DataDirectory);
        Assert.Equal(@"C:\arg", StartupOptions.Parse(["--data-dir=C:\\arg"], @"C:\env").DataDirectory);
        var bad = StartupOptions.Parse(["--data-dir", "--wat"]);
        Assert.Equal(2, bad.Problems.Count);
    }

    [Fact]
    public void Run_entry_is_added_and_removed()
    {
        string keyPath = @"Software\Switchboard.Tests\" + Guid.NewGuid().ToString("N");
        var reg = new StartupRegistration(keyPath, "Switchboard");
        try
        {
            Assert.False(reg.IsEnabled(@"C:\Apps\Switchboard.exe"));
            reg.Enable(@"C:\Apps\Switchboard.exe");
            Assert.True(reg.IsEnabled(@"c:\apps\switchboard.exe"));
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
                Assert.Equal("\"C:\\Apps\\Switchboard.exe\"", key!.GetValue("Switchboard"));

            Assert.False(reg.IsEnabled(@"D:\Other copy\Switchboard.exe"));
            Assert.True(reg.Exists());

            reg.Disable();
            Assert.False(reg.Exists());        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Switchboard.Tests", throwOnMissingSubKey: false);
        }
    }
}

public class IconTests
{
    [Fact]
    public void Ico_has_every_size_as_png()
    {
        byte[] ico = IconArt.CreateIco(IconArt.IcoSizes);
        Assert.Equal(0, BitConverter.ToUInt16(ico, 0));
        Assert.Equal(1, BitConverter.ToUInt16(ico, 2));
        Assert.Equal(IconArt.IcoSizes.Length, BitConverter.ToUInt16(ico, 4));
        for (int i = 0; i < IconArt.IcoSizes.Length; i++)
        {
            int entry = 6 + 16 * i;
            int size = ico[entry] == 0 ? 256 : ico[entry];
            Assert.Equal(IconArt.IcoSizes[i], size);
            int offset = BitConverter.ToInt32(ico, entry + 12);
            Assert.Equal(0x89, ico[offset]);
            Assert.Equal((byte)'P', ico[offset + 1]);
        }
    }

    [Fact]
    public void Committed_icon_matches_the_drawing()
    {
        // If this fails after changing IconArt, regenerate: Switchboard.exe --export-icon src\Switchboard\Assets\Switchboard.ico
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Switchboard.slnx"))) dir = Path.GetDirectoryName(dir)!;
        Assert.NotNull(dir);
        string path = Path.Combine(dir, "src", "Switchboard", "Assets", "Switchboard.ico");
        Assert.Equal(IconArt.CreateIco(IconArt.IcoSizes), File.ReadAllBytes(path));
    }

    [Fact]
    public void Icon_is_opaque_in_the_middle_and_clear_in_the_corner()
    {
        var pixels = IconArt.Render(32);
        Assert.Equal(0u, pixels[0] >> 24);
        Assert.Equal(255u, pixels[16 * 32 + 2] >> 24);
    }
}
