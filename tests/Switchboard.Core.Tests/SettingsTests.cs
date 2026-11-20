using Switchboard.Core.Monitors;
using Switchboard.Core.Settings;

namespace Switchboard.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "switchboard-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsTests()
    {
        // Keep test noise out of the real log in %LOCALAPPDATA%.
        Log.FilePath = Path.Combine(_dir, "test.log");
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var result = new SettingsStore(SettingsPath).Load();
        Assert.False(result.FileExisted);
        Assert.Null(result.Problem);
        Assert.Equal("Ctrl+Alt+Shift+I", result.Settings.CycleHotkey);
    }

    [Fact]
    public void Round_trips_with_hex_values()
    {
        var store = new SettingsStore(SettingsPath);
        var settings = new AppSettings();
        settings.Monitors["MSI3CB0"] = new MonitorSettings
        {
            Nickname = "Main monitor",
            Inputs = { new InputSettings { Value = 0x11, Name = "Gaming PC", Icon = "pc" }, new InputSettings { Value = 0x12, Name = "PS5", Hotkey = "Ctrl+Alt+Shift+2" } },
        };
        Assert.True(store.Save(settings));

        string json = File.ReadAllText(SettingsPath);
        Assert.Contains("\"0x11\"", json);

        var loaded = store.Load().Settings;
        var inputs = loaded.Monitors["msi3cb0"].Inputs; // ids are case-insensitive
        Assert.Equal(new uint[] { 0x11, 0x12 }, inputs.Select(i => i.Value));
        Assert.Equal("PS5", inputs[1].Name);
    }

    [Fact]
    public void Accepts_comments_trailing_commas_and_decimal_values()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
            {
              // hand-edited
              "monitors": {
                "MSI3CB0": { "inputs": [ { "value": 17, "name": "PC" }, { "value": "12h" }, ] },
              },
            }
            """);
        var result = new SettingsStore(SettingsPath).Load();
        Assert.Null(result.Problem);
        Assert.Equal(new uint[] { 0x11, 0x12 }, result.Settings.Monitors["MSI3CB0"].Inputs.Select(i => i.Value));
    }

    [Fact]
    public void Broken_file_is_reported_and_never_overwritten()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ \"monitors\": { oops } }");
        var store = new SettingsStore(SettingsPath);
        var result = store.Load();

        Assert.NotNull(result.Problem);
        Assert.Contains("line 1", result.Problem);
        Assert.False(store.Save(result.Settings));
        Assert.Equal("{ \"monitors\": { oops } }", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Merge_adds_new_monitors_and_inputs_once()
    {
        var settings = new AppSettings();
        var msi = Snapshot("MSI3CB0", current: 0x11, advertised: [0x11, 0x12, 0x0F, 0x10]);

        Assert.True(SettingsStore.MergeDetected(settings, [msi]));
        Assert.Equal("G27CQ4", settings.Monitors["MSI3CB0"].Nickname);
        Assert.Equal(new uint[] { 0x11, 0x12, 0x0F, 0x10 }, settings.Monitors["MSI3CB0"].Inputs.Select(i => i.Value));
        Assert.False(SettingsStore.MergeDetected(settings, [msi]));
    }

    [Fact]
    public void Layout_uses_names_hides_and_marks_current()
    {
        var ms = new MonitorSettings
        {
            Inputs =
            {
                new InputSettings { Value = 0x11, Name = "Gaming PC", Icon = "pc" },
                new InputSettings { Value = 0x10, Hidden = true },
                new InputSettings { Value = 0x12, Name = "PS5", Icon = "nonsense" },
            },
        };
        var layout = InputLayout.Build(Snapshot("MSI3CB0", current: 0x11, advertised: [0x11, 0x12, 0x0F, 0x10]), ms);

        Assert.Equal(new uint[] { 0x11, 0x10, 0x12, 0x0F }, layout.Select(i => i.Value));
        Assert.Equal("Gaming PC", layout[0].Name);
        Assert.True(layout[0].IsCurrent);
        Assert.Equal("pc", layout[0].Icon);
        Assert.True(layout[1].Hidden);
        Assert.Equal("display", layout[2].Icon);
        Assert.Equal("DisplayPort 1", layout[3].Name);
    }

    [Fact]
    public void Layout_falls_back_to_common_ports_when_capabilities_fail()
    {
        var snapshot = Snapshot("X", current: null, advertised: null) with { CapabilitiesFailed = true };
        Assert.Equal(4, InputLayout.Build(snapshot, null).Count);
    }

    [Fact]
    public void Fallback_ports_join_the_current_input_when_capabilities_fail()
    {
        // The MSI over DisplayPort: one input read, no capabilities string.
        var settings = new MonitorSettings { Inputs = { new InputSettings { Value = 0x11 } } };
        var snapshot = Snapshot("MSI3CB0", current: 0x11, advertised: null) with { CapabilitiesFailed = true };
        Assert.Equal(new uint[] { 0x11, 0x0F, 0x10, 0x12 }, InputLayout.Build(snapshot, settings).Select(i => i.Value));
    }

    [Fact]
    public void Saved_file_is_readable_by_hand()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings());
        string json = File.ReadAllText(SettingsPath);
        Assert.Contains("\"Ctrl+Alt+Shift+I\"", json);
        Assert.DoesNotContain("keepOpen", json);
    }

    [Fact]
    public void Next_wraps_around()
    {
        var layout = InputLayout.Build(Snapshot("MSI3CB0", current: 0x12, advertised: [0x11, 0x12]), null);
        Assert.Equal(0x11u, InputLayout.Next(layout)!.Value);

        var noCurrent = InputLayout.Build(Snapshot("MSI3CB0", current: null, advertised: [0x11, 0x12]), null);
        Assert.Equal(0x11u, InputLayout.Next(noCurrent)!.Value);
        Assert.Null(InputLayout.Next([]));
    }

    private static MonitorSnapshot Snapshot(string id, uint? current, uint[]? advertised) =>
        new(id, "G27CQ4", true, default, MonitorHealth.Responding, current, advertised, false);
}
