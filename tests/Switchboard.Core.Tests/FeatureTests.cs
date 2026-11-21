using Switchboard.Core.Actions;
using Switchboard.Core.Ddc;
using Switchboard.Core.Hotkeys;
using Switchboard.Core.Monitors;
using Switchboard.Core.Settings;
using Switchboard.Core.Usb;

namespace Switchboard.Core.Tests;

/// <summary>Stage 4: scenes, commands and links, USB rules, the picker, the toast, levels.</summary>
public sealed class FeatureTests
{
    private const string Msi = "MSI3CB0-000000D1";
    private const string LeftAcer = "ACR0673-83001C4F";
    private const string RightAcer = "ACR06F0-TG5EE0018529";

    private static readonly ScreenRect LeftBounds = new(-1920, 304, 0, 1384);
    private static readonly ScreenRect MsiBounds = new(0, 0, 2560, 1440);
    private static readonly ScreenRect RightBounds = new(2560, 304, 4480, 1384);

    private static MonitorSnapshot Monitor(string id, string name, bool primary, ScreenRect bounds, uint current, uint[] inputs,
        MonitorHealth health = MonitorHealth.Responding, string? caps = null) =>
        new(id, name, primary, bounds, health, current, inputs, false, CapabilitiesText: caps, HasAnswered: true);

    /// <summary>The owner's three monitors: MSI (main) on the PC's HDMI 2, both Acers on HDMI 1.</summary>
    private static IReadOnlyList<MonitorSnapshot> OwnerMonitors(uint msiInput = 0x12) =>
    [
        Monitor(Msi, "MSI G27CQ4", true, MsiBounds, msiInput, [0x11, 0x12, 0x0F, 0x10]),
        Monitor(LeftAcer, "Acer VG240Y", false, LeftBounds, 0x11, [0x01, 0x0F, 0x11]),
        Monitor(RightAcer, "VG240Y", false, RightBounds, 0x11, [0x01, 0x11, 0x12]),
    ];

    private static AppSettings OwnerSettings()
    {
        var settings = new AppSettings();
        settings.Monitors[Msi] = new MonitorSettings
        {
            Nickname = "MSI G27CQ4",
            PcInput = 0x12,
            Inputs =
            {
                new InputSettings { Value = 0x12, Name = "Gaming PC", Icon = "pc" },
                new InputSettings { Value = 0x11, Name = "PS5", Icon = "console", Hotkey = "Alt+PageUp" },
                new InputSettings { Value = 0x0F, Hidden = true },
                new InputSettings { Value = 0x10, Hidden = true },
            },
        };
        settings.Monitors[LeftAcer] = new MonitorSettings { Nickname = "Left Acer" };
        settings.Monitors[RightAcer] = new MonitorSettings { Nickname = "Right Acer", Hidden = true };
        settings.Scenes.Add(new SceneSettings
        {
            Id = "console",
            Name = "Console",
            Steps = { new SceneStep { Monitor = Msi, Input = 0x11 }, new SceneStep { Monitor = LeftAcer, Brightness = 30 } },
        });
        return settings;
    }

    // ---- Actions ----

    [Theory]
    [InlineData("scene:console", "scene:console")]
    [InlineData(" Scene:Console ", "scene:Console")]
    [InlineData("switch:MSI3CB0-000000D1:0x11", "switch:MSI3CB0-000000D1:0x11")]
    [InlineData("switch:MSI3CB0-000000D1:17", "switch:MSI3CB0-000000D1:0x11")]
    [InlineData("BACK", "back")]
    public void Actions_round_trip(string text, string expected) => Assert.Equal(expected, AppAction.Parse(text)!.ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("scene:")]
    [InlineData("switch:MSI")]
    [InlineData("switch::0x11")]
    [InlineData("switch:MSI:zero")]
    [InlineData("reboot")]
    public void Unreadable_actions_are_no_action(string? text) => Assert.Null(AppAction.Parse(text));

    [Fact]
    public void Actions_describe_themselves_with_names()
    {
        var settings = OwnerSettings();
        Assert.Equal("Run Console", AppAction.Parse("scene:console")!.Describe(settings, _ => "?"));
        Assert.Equal("MSI to PS5", AppAction.ForSwitch(Msi, 0x11).Describe(settings, _ => "MSI"));
        Assert.Equal("MSI to DisplayPort 1", AppAction.ForSwitch(Msi, 0x0F).Describe(settings, _ => "MSI"));
        Assert.Contains("deleted", AppAction.ForScene("gone").Describe(settings, _ => "?"));
    }

    // ---- Scenes ----

    [Theory]
    [InlineData("Console", "console")]
    [InlineData("  Console night! ", "console-night")]
    [InlineData("PS5 + Xbox", "ps5-xbox")]
    [InlineData("", "scene")]
    [InlineData("日本", "scene")]
    public void Scene_ids_come_from_the_name(string name, string expected) => Assert.Equal(expected, ScenePlanner.MakeId(name, []));

    [Fact]
    public void Scene_ids_stay_unique()
    {
        var existing = new[] { new SceneSettings { Id = "console" }, new SceneSettings { Id = "console-2" } };
        Assert.Equal("console-3", ScenePlanner.MakeId("Console", existing));
    }

    [Fact]
    public void A_scene_plans_connected_monitors_and_reports_missing_ones()
    {
        var scene = new SceneSettings
        {
            Steps =
            {
                new SceneStep { Monitor = Msi, Input = 0x11, Brightness = 140 },
                new SceneStep { Monitor = "GONE-1", Input = 0x11 },
                new SceneStep { Monitor = LeftAcer },                       // does nothing: left out
                new SceneStep { Monitor = Msi.ToLowerInvariant(), Input = 0x12 }, // second step for the MSI: ignored
            },
        };
        var plan = ScenePlanner.Plan(scene, OwnerMonitors());
        var step = Assert.Single(plan.Steps);
        Assert.Equal(new PlannedStep(Msi, 0x11, 100), step);
        Assert.Equal(["GONE-1"], plan.MissingMonitors);
    }

    [Fact]
    public void A_scene_is_active_when_its_switches_show()
    {
        var scene = OwnerSettings().Scenes[0];
        Assert.False(ScenePlanner.IsActive(scene, OwnerMonitors(msiInput: 0x12)));
        Assert.True(ScenePlanner.IsActive(scene, OwnerMonitors(msiInput: 0x11)));
        // Brightness only: never "active".
        Assert.False(ScenePlanner.IsActive(new SceneSettings { Steps = { new SceneStep { Monitor = Msi, Brightness = 50 } } }, OwnerMonitors()));
    }

    [Fact]
    public void Going_back_puts_back_what_a_scene_switched()
    {
        var monitors = OwnerMonitors(msiInput: 0x12);
        var plan = ScenePlanner.Plan(OwnerSettings().Scenes[0], monitors);
        var point = ScenePlanner.Remember("console", plan.Steps, monitors);
        Assert.Equal(0x12u, point.Inputs[Msi]);
        Assert.False(point.Inputs.ContainsKey(LeftAcer)); // brightness only

        var after = OwnerMonitors(msiInput: 0x11);
        Assert.Equal([new PlannedStep(Msi, 0x12, null)], ScenePlanner.PlanBack(point, after));
        // Already back: nothing to do.
        Assert.Empty(ScenePlanner.PlanBack(point, monitors));
    }

    [Fact]
    public void Save_as_scene_takes_what_shows_now()
    {
        var settings = OwnerSettings();
        var scene = ScenePlanner.FromCurrent("Desk", OwnerMonitors(), settings);
        Assert.Equal("desk", scene.Id);
        // The hidden right Acer is left out.
        Assert.Equal([(Msi, 0x12u), (LeftAcer, 0x11u)], scene.Steps.Select(s => (s.Monitor, s.Input!.Value)));
    }

    [Fact]
    public void Scenes_find_by_id_or_name()
    {
        var settings = OwnerSettings();
        Assert.Equal("console", ScenePlanner.Find(settings, "CONSOLE")!.Id);
        settings.Scenes[0].Id = "c1";
        Assert.Equal("c1", ScenePlanner.Find(settings, "console")!.Id);
        Assert.Null(ScenePlanner.Find(settings, "desk"));
    }

    [Fact]
    public void Scene_and_picker_shortcuts_have_slots()
    {
        var settings = OwnerSettings();
        string key = ShortcutSlots.SceneKey("console");
        Assert.True(ShortcutSlots.Set(settings, key, "Ctrl+Alt+Shift+C"));
        Assert.Equal("Ctrl+Alt+Shift+C", settings.Scenes[0].Hotkey);
        Assert.Contains(ShortcutSlots.All(settings), s => s.Key == key);
        Assert.Contains(ShortcutSlots.All(settings), s => s.Key == ShortcutSlots.Picker);
        Assert.False(ShortcutSlots.Set(settings, ShortcutSlots.SceneKey("nope"), "Ctrl+Alt+Shift+N"));
    }

    // ---- Command line and links ----

    [Fact]
    public void Command_line_switch_with_monitor_and_input()
    {
        var c = RemoteCommand.FromArgs(["--data-dir", "x", "--switch", "MSI", "HDMI", "1"], out var problem)!;
        Assert.Null(problem);
        Assert.Equal(new RemoteCommand(CommandVerb.Switch, "MSI", "HDMI 1"), c);
    }

    [Fact]
    public void Command_line_switch_with_input_only_means_the_main_monitor()
    {
        var c = RemoteCommand.FromArgs(["--switch", "PS5"], out _)!;
        Assert.Null(c.Monitor);
        Assert.Equal("PS5", c.Target);
    }

    [Theory]
    [InlineData("--brightness", "50", null, 50, false)]
    [InlineData("--brightness", "+10", null, 10, true)]
    [InlineData("--volume", "-5", null, -5, true)]
    public void Command_line_levels(string verb, string level, string? monitor, int amount, bool relative)
    {
        var c = RemoteCommand.FromArgs([verb, level], out _)!;
        Assert.Equal(monitor, c.Monitor);
        Assert.Equal(new Level(amount, relative), c.Level);
    }

    [Fact]
    public void Command_line_level_for_a_named_monitor()
    {
        var c = RemoteCommand.FromArgs(["--contrast", "Left", "Acer", "70%"], out _)!;
        Assert.Equal("Left Acer", c.Monitor);
        Assert.Equal(new Level(70, false), c.Level);
    }

    [Theory]
    [InlineData("--switch")]
    [InlineData("--scene")]
    [InlineData("--brightness", "150")]
    [InlineData("--brightness", "MSI", "lots")]
    public void Command_line_mistakes_are_explained(params string[] args)
    {
        Assert.Null(RemoteCommand.FromArgs(args, out var problem));
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Fact]
    public void A_normal_start_has_no_command()
    {
        Assert.Null(RemoteCommand.FromArgs(["--data-dir", "C:\\x", "--no-switch", "--open", "settings"], out var problem));
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("inputflip://switch/MSI/PS5", CommandVerb.Switch, "MSI", "PS5")]
    [InlineData("inputflip://switch/Left%20Acer/HDMI%201/", CommandVerb.Switch, "Left Acer", "HDMI 1")]
    [InlineData("inputflip:switch/PS5", CommandVerb.Switch, null, "PS5")]
    [InlineData("INPUTFLIP://Scene/Console%20night?from=deck", CommandVerb.Scene, null, "Console night")]
    [InlineData("inputflip://toggle", CommandVerb.Toggle, null, null)]
    [InlineData("inputflip://toggle/MSI", CommandVerb.Toggle, "MSI", null)]
    [InlineData("inputflip://picker", CommandVerb.Picker, null, null)]
    public void Links(string uri, CommandVerb verb, string? monitor, string? target)
    {
        var c = RemoteCommand.FromArgs([uri], out var problem)!;
        Assert.Null(problem);
        Assert.Equal(verb, c.Verb);
        Assert.Equal(monitor, c.Monitor);
        Assert.Equal(target, c.Target);
    }

    [Fact]
    public void Link_levels_and_unknown_links()
    {
        Assert.Equal(new Level(-10, true), RemoteCommand.FromUri("inputflip://brightness/all/-10", out _)!.Level);
        Assert.Null(RemoteCommand.FromUri("inputflip://format/c", out var problem));
        Assert.Contains("inputflip://switch", problem);
    }

    [Fact]
    public void Commands_survive_the_trip_to_the_running_copy()
    {
        var c = new RemoteCommand(CommandVerb.Brightness, "Left Acer", null, new Level(-10, true));
        var args = CommandPipe.DecodeRequest(CommandPipe.EncodeRequest(c.ToArgs()))!;
        Assert.Equal(c, RemoteCommand.FromArgs(args, out _));

        var reply = CommandPipe.DecodeReply(CommandPipe.EncodeReply(new CommandPipe.Reply(true, "MSI \"G27\" → PS5")));
        Assert.Equal(new CommandPipe.Reply(true, "MSI \"G27\" → PS5"), reply);
        Assert.Null(CommandPipe.DecodeReply("not json"));
        Assert.Null(CommandPipe.DecodeRequest("{\"a\":1}"));
    }

    [Theory]
    [InlineData(40, 0, 0, 40)]
    [InlineData(40, 0, 5, 45)]
    [InlineData(40, 0, -50, 0)]
    [InlineData(90, 0, 20, 100)]
    public void Levels_apply(int current, int _, int step, int expected) =>
        Assert.Equal(expected, new Level(step, true).Apply(current));

    // ---- Finding monitors and inputs from what was typed ----

    [Theory]
    [InlineData(null, Msi)]
    [InlineData("main", Msi)]
    [InlineData("2", LeftAcer)]
    [InlineData("msi", Msi)]
    [InlineData("left acer", LeftAcer)]
    [InlineData("LEFTACER", LeftAcer)]
    [InlineData("ACR06F0-TG5EE0018529", RightAcer)]
    [InlineData("right", RightAcer)]
    public void Monitors_by_name_number_or_id(string? spec, string expected)
    {
        var m = TargetResolver.ResolveMonitor(spec, OwnerMonitors(), OwnerSettings(), out var problem);
        Assert.Null(problem);
        Assert.Equal(expected, m!.Id);
    }

    [Fact]
    public void Ambiguous_and_unknown_monitors_say_why()
    {
        Assert.Null(TargetResolver.ResolveMonitor("acer", OwnerMonitors(), OwnerSettings(), out var problem));
        Assert.Contains("Left Acer or Right Acer", problem);
        Assert.Null(TargetResolver.ResolveMonitor("dell", OwnerMonitors(), OwnerSettings(), out problem));
        Assert.Contains("Connected: MSI G27CQ4, Left Acer, Right Acer", problem);
        Assert.Null(TargetResolver.ResolveMonitor("7", OwnerMonitors(), OwnerSettings(), out problem));
        Assert.Contains("1 to 3", problem);
    }

    [Theory]
    [InlineData("PS5", 0x11u)]
    [InlineData("ps5", 0x11u)]
    [InlineData("HDMI 1", 0x11u)]
    [InlineData("hdmi1", 0x11u)]
    [InlineData("gaming", 0x12u)]
    [InlineData("pc", 0x12u)]
    [InlineData("0x0F", 0x0Fu)]
    [InlineData("DisplayPort 2", 0x10u)]
    [InlineData("27", 0x1Bu)] // a value works even when not listed
    public void Inputs_by_name_port_or_value(string spec, uint expected)
    {
        var settings = OwnerSettings();
        uint? v = TargetResolver.ResolveInput(spec, OwnerMonitors()[0], settings.Monitors[Msi], out var problem);
        Assert.Null(problem);
        Assert.Equal(expected, v);
    }

    [Fact]
    public void Unknown_and_ambiguous_inputs_say_why()
    {
        var settings = OwnerSettings();
        Assert.Null(TargetResolver.ResolveInput("xbox", OwnerMonitors()[0], settings.Monitors[Msi], out var problem));
        Assert.Contains("Gaming PC, PS5", problem);
        Assert.DoesNotContain("DisplayPort", problem); // hidden ones aren't offered
        Assert.Null(TargetResolver.ResolveInput("displayport", OwnerMonitors()[0], settings.Monitors[Msi], out problem));
        Assert.Contains("DisplayPort 1 or DisplayPort 2", problem);
    }

    // ---- USB rules ----

    [Theory]
    [InlineData(@"\\?\USB#VID_046D&PID_C52B#5&1a2b3c4d&0&3#{a5dcbf10-6530-11d2-901f-00c04fb951ed}", 0x046D, 0xC52B, null)]
    [InlineData(@"\\?\USB#VID_0951&PID_16E6#ABCDEF012345#{a5dcbf10-6530-11d2-901f-00c04fb951ed}", 0x0951, 0x16E6, "ABCDEF012345")]
    [InlineData(@"USB\VID_046D&PID_C52B&MI_00\6&2B&0&0000", 0x046D, 0xC52B, null)]
    [InlineData(@"USB\VID_1532&PID_0084\SERIAL9", 0x1532, 0x0084, "SERIAL9")]
    [InlineData("vid_046d&pid_c52b", 0x046D, 0xC52B, null)]
    public void Usb_ids_from_windows_paths(string text, int vid, int pid, string? serial) =>
        Assert.Equal(new UsbDeviceId((ushort)vid, (ushort)pid, serial), UsbDeviceId.TryParse(text));

    [Fact]
    public void Usb_ids_format_and_match()
    {
        var any = UsbDeviceId.TryParse("VID_046D&PID_C52B")!;
        var withSerial = UsbDeviceId.TryParse(@"VID_046D&PID_C52B\ABC")!;
        Assert.Equal("VID_046D&PID_C52B", any.ToString());
        Assert.Equal(@"VID_046D&PID_C52B\ABC", withSerial.ToString());
        Assert.True(any.Matches(withSerial));
        Assert.True(withSerial.Matches(UsbDeviceId.TryParse(@"USB\VID_046D&PID_C52B\abc")!));
        Assert.False(withSerial.Matches(UsbDeviceId.TryParse(@"USB\VID_046D&PID_C52B\XYZ")!));
        Assert.Null(UsbDeviceId.TryParse(@"USB\ROOT_HUB30\4&1234"));
    }

    [Fact]
    public void Usb_rules_run_once_per_burst()
    {
        var rules = new List<UsbRule>
        {
            new() { Device = "VID_046D&PID_C52B", WhenConnected = "back", WhenDisconnected = "scene:console" },
            new() { Device = "VID_1532&PID_0084", WhenDisconnected = "scene:console" }, // same box, same action
            new() { Device = "VID_1532&PID_0084", WhenDisconnected = "switch:ACR0673-83001C4F:0x01", Disabled = true },
        };
        var engine = new UsbRuleEngine();
        var t = new DateTime(2026, 11, 21, 21, 0, 0, DateTimeKind.Utc);
        const string receiver = @"\\?\USB#VID_046D&PID_C52B#5&1&0&3#{a5dcbf10-6530-11d2-901f-00c04fb951ed}";
        const string mouse = @"\\?\USB#VID_1532&PID_0084#6&2&0&1#{a5dcbf10-6530-11d2-901f-00c04fb951ed}";

        Assert.Equal(["scene:console"], engine.OnDeviceChange(rules, receiver, connected: false, t).Select(a => a.ToString()));
        Assert.Empty(engine.OnDeviceChange(rules, mouse, connected: false, t.AddMilliseconds(200)));
        Assert.Equal(["back"], engine.OnDeviceChange(rules, receiver, connected: true, t.AddSeconds(1)).Select(a => a.ToString()));
        Assert.Equal(["scene:console"], engine.OnDeviceChange(rules, mouse, connected: false, t.AddSeconds(10)).Select(a => a.ToString()));
        Assert.Empty(engine.OnDeviceChange(rules, @"\\?\HID#VID_0000", connected: true, t.AddSeconds(20)));
    }

    // ---- Picker and toast ----

    [Fact]
    public void Picker_lists_scenes_then_shown_inputs()
    {
        var settings = OwnerSettings();
        var items = PickerItems.Build(OwnerMonitors(), settings);
        Assert.Equal(
            ["Scenes/Console", "MSI G27CQ4/Gaming PC", "MSI G27CQ4/PS5", "Left Acer/VGA 1", "Left Acer/DisplayPort 1", "Left Acer/HDMI 1"],
            items.Select(i => $"{i.Group}/{i.Label}"));
        Assert.True(items[1].IsCurrent);
        Assert.Equal("switch:MSI3CB0-000000D1:0x11", items[2].Action.ToString());
        Assert.Equal("HDMI 1", items[2].Detail);
        Assert.Equal(0, PickerItems.InitialSelection(items)); // the scene isn't showing
        Assert.Equal(2, PickerItems.InitialSelection(items.Skip(1).Prepend(items[1]).ToList()));
    }

    [Fact]
    public void Toast_goes_where_you_can_see_it()
    {
        var screens = new List<(ScreenRect, bool)> { (MsiBounds, true), (LeftBounds, false), (RightBounds, false) };
        // Mouse on the MSI, which just switched: the window's screen (left Acer).
        Assert.Equal(LeftBounds, ToastPlacement.PickScreen(screens, [MsiBounds], (100, 100), new ScreenRect(-900, 500, -500, 900)));
        // Mouse on the right Acer: there.
        Assert.Equal(RightBounds, ToastPlacement.PickScreen(screens, [MsiBounds], (3000, 600), null));
        // An Acer switched, mouse nowhere useful: the main display.
        Assert.Equal(MsiBounds, ToastPlacement.PickScreen(screens, [LeftBounds], null, null));
        // Everything switched: the mouse's screen anyway.
        Assert.Equal(RightBounds, ToastPlacement.PickScreen(screens, [MsiBounds, LeftBounds, RightBounds], (3000, 600), null));
        Assert.Equal((1180, 1440 - 64 - 80), ToastPlacement.Position(MsiBounds, 200, 64, 80));
    }

    // ---- Brightness, contrast, volume ----

    [Theory]
    [InlineData(50u, 100u, 50)]
    [InlineData(64u, 100u, 64)]
    [InlineData(128u, 255u, 50)]
    [InlineData(300u, 255u, 100)]
    [InlineData(30u, 0u, 30)]
    public void Raw_levels_to_percent(uint current, uint max, int expected) => Assert.Equal(expected, VcpScale.ToPercent(current, max));

    [Theory]
    [InlineData(50, 100u, 50u)]
    [InlineData(50, 255u, 128u)]
    [InlineData(-5, 100u, 0u)]
    [InlineData(120, 50u, 50u)]
    public void Percent_to_raw_levels(int percent, uint max, uint expected) => Assert.Equal(expected, VcpScale.FromPercent(percent, max));

    [Fact]
    public void Controls_follow_the_capabilities_string()
    {
        const string withSound = "(prot(monitor)vcp(02 04 10 12 14(05 06 08) 60(11 12 0F) 62 8D(01 02))mccs_ver(2.2))";
        const string noSound = "(vcp(10 12 60(11 12)))";
        var msi = OwnerMonitors()[0];
        Assert.True(MonitorFeatures.Offers(msi with { CapabilitiesText = withSound }, VcpCodes.SpeakerVolume));
        Assert.False(MonitorFeatures.Offers(msi with { CapabilitiesText = noSound }, VcpCodes.SpeakerVolume));
        // No capabilities string: brightness and contrast yes, sound no.
        Assert.True(MonitorFeatures.Offers(msi, VcpCodes.Brightness));
        Assert.False(MonitorFeatures.Offers(msi, VcpCodes.SpeakerVolume));
        Assert.False(MonitorFeatures.Offers(msi with { Health = MonitorHealth.Unavailable }, VcpCodes.Brightness));
        Assert.Contains((byte)0x8D, CapabilitiesParser.GetSupportedCodes(withSound));
    }

    [Fact]
    public async Task Slider_values_dont_pile_up()
    {
        var sent = new List<uint>();
        var gate = new TaskCompletionSource();
        var sender = new LatestValueSender(async v =>
        {
            lock (sent) sent.Add(v);
            if (v == 1) await gate.Task; // the first send is slow
        });
        sender.Submit(1);
        await Task.Delay(50);
        for (uint v = 2; v <= 20; v++) sender.Submit(v);
        gate.SetResult();
        await Task.Delay(50);
        await sender.WhenIdle();
        Assert.Equal([1u, 20u], sent);
    }

    // ---- Settings file ----

    [Fact]
    public void New_settings_round_trip_and_survive_hand_edits()
    {
        string dir = Path.Combine(Path.GetTempPath(), "inputflip-tests-" + Guid.NewGuid().ToString("N"));
        Log.FilePath = Path.Combine(dir, "test.log");
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var store = new SettingsStore(path);
            var settings = OwnerSettings();
            settings.UsbRules.Add(new UsbRule { Device = "VID_046D&PID_C52B", Name = "USB Receiver", WhenDisconnected = "scene:console" });
            settings.PowerRules = new PowerRules { OnLock = "scene:console", OnUnlock = "back" };
            ShortcutSlots.Set(settings, ShortcutSlots.Picker, null);
            Assert.True(store.Save(settings));

            string json = File.ReadAllText(path);
            Assert.Contains("\"input\": \"0x11\"", json);
            var loaded = store.Load().Settings;
            Assert.Equal("console", loaded.Scenes[0].Id);
            Assert.Equal(0x11u, loaded.Scenes[0].Steps[0].Input);
            Assert.Equal(30, loaded.Scenes[0].Steps[1].Brightness);
            Assert.Equal("back", loaded.PowerRules!.OnUnlock);
            Assert.Equal("USB Receiver", loaded.UsbRules[0].Name);
            // A cleared shortcut stays cleared.
            Assert.Null(ShortcutSlots.Get(loaded, ShortcutSlots.Picker));

            File.WriteAllText(path, """{ "scenes": [ { "name": "Desk", "steps": null }, null ], "usbRules": null }""");
            loaded = store.Load().Settings;
            Assert.Equal("desk", Assert.Single(loaded.Scenes).Id);
            Assert.Empty(loaded.Scenes[0].Steps);
            Assert.Empty(loaded.UsbRules);
            Assert.Equal("Ctrl+Alt+Shift+P", loaded.PickerHotkey);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
