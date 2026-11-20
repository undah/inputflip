using Switchboard.Core.Ddc;
using Switchboard.Core.Hotkeys;
using Switchboard.Core.Monitors;

namespace Switchboard.Core.Tests;

public class CapabilitiesParserTests
{
    // Shaped like the MSI G27CQ4's string read over HDMI (docs/plan.md, stage 1 results).
    private const string Msi =
        "(prot(monitor)type(LCD)model(FALCON)cmds(01 02 03 07 0C E3 F3)vcp(02 04 05 08 10 12 14(05 06 08 0B) 16 18 1A 52 60( 11 12 0F 10) 62 AC AE B2 B6 C6 C8 C9 D6(01 05) DF)mccs_ver(2.0))";

    [Fact]
    public void Reads_input_sources_in_order()
    {
        Assert.Equal(new uint[] { 0x11, 0x12, 0x0F, 0x10 }, CapabilitiesParser.GetInputSources(Msi));
    }

    [Fact]
    public void Reads_model()
    {
        Assert.Equal("FALCON", CapabilitiesParser.GetModel(Msi));
    }

    [Fact]
    public void Handles_run_together_values_and_no_spaces()
    {
        const string caps = "(vcp(1012 60(0F1112)D6(0104)))";
        Assert.Equal(new uint[] { 0x0F, 0x11, 0x12 }, CapabilitiesParser.GetInputSources(caps));
    }

    [Fact]
    public void Missing_vcp_60_gives_empty_list()
    {
        Assert.Empty(CapabilitiesParser.GetInputSources("(prot(monitor)model(acer)vcp(10 12))"));
        Assert.Empty(CapabilitiesParser.GetInputSources(null));
    }

    [Fact]
    public void Does_not_match_group_inside_longer_name()
    {
        Assert.Null(CapabilitiesParser.GetGroup("(vcpname(10(Brightness)))", "name"));
    }

    [Fact]
    public void Unbalanced_string_still_parses()
    {
        Assert.Equal(new uint[] { 0x11, 0x12 }, CapabilitiesParser.GetInputSources("(vcp(10 60(11 12"));
    }
}

public class InputCatalogTests
{
    [Theory]
    [InlineData(0x11u, "HDMI 1")]
    [InlineData(0x0Fu, "DisplayPort 1")]
    [InlineData(0x1Bu, "USB-C")]
    [InlineData(0x0311u, "HDMI 1")]
    [InlineData(0x1Fu, "Input 0x1F")]
    public void Names_ports(uint value, string expected)
    {
        Assert.Equal(expected, InputCatalog.GetPortName(value));
    }

    [Fact]
    public void Matches_on_low_byte_for_standard_values()
    {
        Assert.True(InputCatalog.Matches(0x11, 0x11));
        Assert.True(InputCatalog.Matches(0x0311, 0x11));
        Assert.False(InputCatalog.Matches(0x12, 0x11));
        Assert.False(InputCatalog.Matches(0x11, 0x0311));
    }
}

public class EdidTests
{
    [Fact]
    public void Parses_identity_name_and_serial()
    {
        var edid = MakeEdid("MSI", 0x3CB0, 0x01020304, name: "G27CQ4", serial: "ABC123");
        var info = EdidParser.Parse(edid);
        Assert.NotNull(info);
        Assert.Equal("MSI", info!.Manufacturer);
        Assert.Equal(0x3CB0, info.ProductCode);
        Assert.Equal(0x01020304u, info.SerialNumber);
        Assert.Equal("G27CQ4", info.Name);
        Assert.Equal("ABC123", info.SerialText);
    }

    [Fact]
    public void Rejects_bad_header_and_short_data()
    {
        var edid = MakeEdid("ACR", 0x0673, 0);
        edid[0] = 1;
        Assert.Null(EdidParser.Parse(edid));
        Assert.Null(EdidParser.Parse(new byte[64]));
        Assert.Null(EdidParser.Parse(null));
    }

    [Fact]
    public void Converts_device_interface_to_instance_id()
    {
        Assert.Equal(
            @"DISPLAY\MSI3CB0\5&2f7a&0&UID4353",
            EdidReader.DeviceInterfaceToInstanceId(@"\\?\DISPLAY#MSI3CB0#5&2f7a&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"));
        Assert.Null(EdidReader.DeviceInterfaceToInstanceId(null));
    }

    [Fact]
    public void Ids_prefer_serial_text_then_number()
    {
        Assert.Equal("MSI3CB0-ABC123", MonitorIdentity.MakeId(EdidParser.Parse(MakeEdid("MSI", 0x3CB0, 7, serial: "ABC123")), @"\\.\DISPLAY1"));
        Assert.Equal("ACR0673-00000007", MonitorIdentity.MakeId(EdidParser.Parse(MakeEdid("ACR", 0x0673, 7)), @"\\.\DISPLAY2"));
        Assert.Equal("ACR06F0", MonitorIdentity.MakeId(EdidParser.Parse(MakeEdid("ACR", 0x06F0, 0)), @"\\.\DISPLAY3"));
        Assert.Equal("display-DISPLAY3", MonitorIdentity.MakeId(null, @"\\.\DISPLAY3"));
    }

    [Fact]
    public void Duplicate_ids_get_a_suffix()
    {
        var taken = new HashSet<string>();
        Assert.Equal("ACR06F0", MonitorIdentity.MakeUnique("ACR06F0", taken));
        Assert.Equal("ACR06F0#2", MonitorIdentity.MakeUnique("ACR06F0", taken));
    }

    internal static byte[] MakeEdid(string manufacturer, ushort product, uint serialNumber, string? name = null, string? serial = null)
    {
        var e = new byte[128];
        new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 }.CopyTo(e, 0);
        int m = ((manufacturer[0] - '@') << 10) | ((manufacturer[1] - '@') << 5) | (manufacturer[2] - '@');
        e[8] = (byte)(m >> 8);
        e[9] = (byte)m;
        e[10] = (byte)product;
        e[11] = (byte)(product >> 8);
        BitConverter.GetBytes(serialNumber).CopyTo(e, 12);
        if (name is not null) WriteDescriptor(e, 72, 0xFC, name);
        if (serial is not null) WriteDescriptor(e, 90, 0xFF, serial);
        return e;
    }

    private static void WriteDescriptor(byte[] e, int offset, byte tag, string text)
    {
        e[offset + 3] = tag;
        for (int i = 0; i < 13; i++)
            e[offset + 5 + i] = i < text.Length ? (byte)text[i] : i == text.Length ? (byte)0x0A : (byte)0x20;
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Shift+I", "Ctrl+Alt+Shift+I")]
    [InlineData("alt + ctrl + 1", "Ctrl+Alt+1")]
    [InlineData("Win+Alt+F5", "Alt+Win+F5")]
    [InlineData("Ctrl+Numpad3", "Ctrl+Numpad3")]
    [InlineData("Ctrl+PageUp", "Ctrl+PageUp")]
    [InlineData("F13", "F13")]
    public void Parses_and_formats(string text, string expected)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey, out var error), error);
        Assert.Equal(expected, hotkey.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("I")]
    [InlineData("Shift+I")]
    [InlineData("Ctrl+Hyper+I")]
    [InlineData("Ctrl+Alt+Escape")]
    public void Rejects_unusable_shortcuts(string text)
    {
        Assert.False(Hotkey.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Uses_register_hotkey_flags()
    {
        Assert.True(Hotkey.TryParse("Ctrl+Alt+1", out var hotkey, out _));
        Assert.Equal(0x3u, (uint)hotkey.Modifiers);
        Assert.Equal((uint)'1', hotkey.VirtualKey);
    }
}
