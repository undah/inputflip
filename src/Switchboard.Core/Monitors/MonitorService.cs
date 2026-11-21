using System.Diagnostics;
using System.Text.Json;
using Switchboard.Core.Ddc;
using Switchboard.Core.Interop;

namespace Switchboard.Core.Monitors;

public enum MonitorHealth
{
    /// <summary>Not asked yet.</summary>
    Unknown,

    /// <summary>The last read worked.</summary>
    Responding,

    /// <summary>We have a handle, but the monitor didn't answer.</summary>
    NotResponding,

    /// <summary>Windows couldn't open the monitor for DDC/CI at all.</summary>
    Unavailable,
}

/// <summary>What we know about one monitor right now. Immutable; the service publishes a new one on every change.</summary>
/// <param name="Connection">The cable type Windows reports (DisplayPort, HDMI...).</param>
/// <param name="CapabilitiesText">The raw capabilities string, for diagnostics.</param>
/// <param name="HasAnswered">At least one read worked since the app started, even if the latest one failed.</param>
/// <param name="InputReadUtc">When the monitor itself last reported <paramref name="CurrentInput"/>; default when it's
/// assumed or unknown.</param>
public sealed record MonitorSnapshot(
    string Id,
    string Name,
    bool IsPrimary,
    ScreenRect Bounds,
    MonitorHealth Health,
    uint? CurrentInput,
    IReadOnlyList<uint>? AdvertisedInputs,
    bool CapabilitiesFailed,
    ConnectionKind Connection = ConnectionKind.Unknown,
    string? CapabilitiesText = null,
    bool HasAnswered = false,
    DateTime InputReadUtc = default);

public enum SwitchStatus
{
    /// <summary>The monitor reports the new input.</summary>
    Confirmed,

    /// <summary>The command went through, but the monitor didn't answer the read-back. It may still have switched.</summary>
    Unconfirmed,

    /// <summary>The monitor answered, but it still reports a different input.</summary>
    StillOnOtherInput,

    /// <summary>The command itself failed.</summary>
    Failed,

    /// <summary>The monitor isn't connected any more.</summary>
    MonitorGone,

    /// <summary>A newer switch on the same monitor started before this one was confirmed; that one reports instead.</summary>
    Superseded,
}

public sealed record SwitchOutcome(SwitchStatus Status, uint? ReportedInput);

/// <summary>
/// Finds monitors, keeps one <see cref="DdcChannel"/> per physical monitor, and reads and switches inputs.
/// All methods are safe to call from any thread; <see cref="Changed"/> is raised on a background thread.
/// </summary>
public sealed class MonitorService : IAsyncDisposable
{
    private const int ReadAttempts = 3;
    private const int WriteAttempts = 3;
    private const int CapabilityAttempts = 2;

    // Read-back after a switch: the first read goes out as soon as the command returns (dxva2 already waits the 50 ms
    // DDC/CI asks for after a write), then one every 100 ms for up to 2 s. Before 1.0.6 the first read waited 150 ms;
    // the MSI G27CQ4 over HDMI always confirmed on that first read, so it may well be ready sooner.
    private static readonly TimeSpan ReadBackInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ReadBackFor = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _lock = new();
    private readonly object _fileLock = new();
    private readonly Dictionary<string, (IReadOnlyList<uint> Inputs, string Text)> _capabilitiesCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _answered = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _capabilitiesInFlight = new(StringComparer.OrdinalIgnoreCase);
    private List<Entry> _entries = new();
    private bool _capabilitiesFileLoaded;

    /// <summary>Never send a switch command (the --no-switch option). Reads still work.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Keeps capabilities strings between runs, so starting Switchboard doesn't read them again. Reading one is a single
    /// call that can't be interrupted (2.6 s on the MSI G27CQ4), and a switch on that monitor had to wait behind it.
    /// Null: remember them only while running.
    /// </summary>
    public string? CapabilitiesFile { get; init; }

    /// <summary>Raised after the monitor list or anything in a snapshot changes.</summary>
    public event Action? Changed;

    public IReadOnlyList<MonitorSnapshot> Monitors
    {
        get
        {
            lock (_lock) return _entries.Select(e => e.Snapshot).ToList();
        }
    }

    /// <summary>
    /// Re-reads the current input of every monitor. With <paramref name="enumerate"/>, also finds monitors again
    /// (after a display change, or sleep and wake, when old handles can go stale).
    /// </summary>
    /// <param name="skip">Monitors to leave out of a quick re-read (hidden ones). Ignored when finding monitors again.
    /// DDC/CI calls to different monitors wait for each other on the owner's PC, so a needless read on one can hold up a
    /// switch on another.</param>
    public async Task RefreshAsync(bool enumerate, Func<string, bool>? skip = null)
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool enumerated = enumerate || _entries.Count == 0;
            if (enumerated)
                await EnumerateAsync().ConfigureAwait(false);

            List<Entry> entries;
            lock (_lock) entries = _entries.ToList();
            await Task.WhenAll(entries.Where(e => e.Channel is not null && (enumerated || skip?.Invoke(e.Snapshot.Id) != true)).Select(ReadInputAsync)).ConfigureAwait(false);
            RaiseChanged();

            // Only after finding monitors (start, display change, the Refresh button), not on every quick re-read:
            // a capabilities string that failed would otherwise be read again each time the window is activated,
            // holding up a switch on that monitor for seconds.
            if (enumerated)
                foreach (var entry in entries)
                    StartCapabilitiesRead(entry);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Switches the input and confirms it by reading it back. The command is on its way before this returns its task,
    /// so callers can update the window afterwards without delaying it; nothing (not even the log) comes before it.
    /// A newer switch on the same monitor ends this one's read-back: it returns <see cref="SwitchStatus.Superseded"/>.
    /// </summary>
    /// <param name="trigger">What asked for the switch ("Alt+PageUp", "click"), for the log.</param>
    /// <param name="requestedAt"><see cref="Stopwatch.GetTimestamp"/> at the key press or click, so the log shows how long the
    /// command waited. 0: now.</param>
    public async Task<SwitchOutcome> SwitchAsync(string monitorId, uint value, string? trigger = null, long requestedAt = 0)
    {
        if (requestedAt == 0) requestedAt = Stopwatch.GetTimestamp();
        Entry? entry;
        lock (_lock) entry = _entries.FirstOrDefault(e => e.Snapshot.Id == monitorId);
        if (entry?.Channel is null) return new SwitchOutcome(SwitchStatus.MonitorGone, null);
        if (ReadOnly)
        {
            Log.Info($"{entry.Snapshot.Name}: not switching to {InputCatalog.FormatValue(value)}, started with --no-switch");
            return new SwitchOutcome(SwitchStatus.Failed, null);
        }

        int generation = Interlocked.Increment(ref entry.SwitchGeneration);
        var write = await entry.Channel.WriteAsync(VcpCodes.InputSource, value, WriteAttempts).ConfigureAwait(false);
        long returned = Stopwatch.GetTimestamp();
        // The command reaches the monitor at the start of the native call; the rest of it is dxva2's wait after a write.
        long commandAt = returned - write.Milliseconds * Stopwatch.Frequency / 1000;
        string name = entry.Snapshot.Name, target = InputCatalog.FormatValue(value), by = trigger ?? "request";
        Log.Info($"{name}: switching to {target} ({by}): command out {Ms(requestedAt, commandAt)} ms after the {by}, took {write.Milliseconds} ms");
        if (!write.Ok)
        {
            Update(entry, s => s with { Health = MonitorHealth.NotResponding });
            return new SwitchOutcome(SwitchStatus.Failed, null);
        }

        // Monitors take a moment to report the new input (the MSI G27CQ4 over HDMI: by the first read, 150 ms after).
        uint? lastReported = null;
        int reads = 0;
        long giveUpAt = returned + (long)(ReadBackFor.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            if (Volatile.Read(ref entry.SwitchGeneration) != generation) return Superseded();
            reads++;
            var read = await entry.Channel.ReadAsync(VcpCodes.InputSource, 1).ConfigureAwait(false);
            if (Volatile.Read(ref entry.SwitchGeneration) != generation) return Superseded();
            if (read.Ok)
            {
                lastReported = read.Current;
                if (InputCatalog.Matches(read.Current, value))
                {
                    long now = Stopwatch.GetTimestamp();
                    Update(entry, s => s with { Health = MonitorHealth.Responding, CurrentInput = value, HasAnswered = true, InputReadUtc = DateTime.UtcNow });
                    Log.Info($"{name}: confirmed on {target} {Ms(commandAt, now)} ms after the command ({reads} {(reads == 1 ? "read" : "reads")}), {Ms(requestedAt, now)} ms after the {by}");
                    return new SwitchOutcome(SwitchStatus.Confirmed, read.Current);
                }
            }
            if (Stopwatch.GetTimestamp() >= giveUpAt) break;
            await Task.Delay(ReadBackInterval).ConfigureAwait(false);
        }

        if (lastReported is uint other)
        {
            Update(entry, s => s with { Health = MonitorHealth.Responding, CurrentInput = other, InputReadUtc = DateTime.UtcNow });
            Log.Info($"{name}: still reports {InputCatalog.FormatValue(other)} after {reads} reads");
            return new SwitchOutcome(SwitchStatus.StillOnOtherInput, other);
        }

        // A failed read-back doesn't mean the command didn't arrive.
        Update(entry, s => s with { CurrentInput = null, InputReadUtc = default });
        Log.Info($"{name}: switch sent, no read-back in {reads} tries");
        return new SwitchOutcome(SwitchStatus.Unconfirmed, null);

        SwitchOutcome Superseded()
        {
            Log.Info($"{name}: stopped confirming {target}, a newer switch took over");
            return new SwitchOutcome(SwitchStatus.Superseded, null);
        }
    }

    /// <summary>
    /// Asks the monitor which input it shows (one try, about 60 ms), for when the remembered one may be out of date:
    /// a monitor can change input from its own buttons, or by itself when a device goes quiet. Null if it didn't answer.
    /// </summary>
    public async Task<uint?> ReadCurrentInputAsync(string monitorId)
    {
        Entry? entry;
        lock (_lock) entry = _entries.FirstOrDefault(e => e.Snapshot.Id == monitorId);
        if (entry?.Channel is null) return null;
        var read = await entry.Channel.ReadAsync(VcpCodes.InputSource, 1).ConfigureAwait(false);
        if (!read.Ok) return null;
        bool changed;
        lock (_lock)
        {
            _answered.Add(entry.Snapshot.Id);
            changed = entry.Snapshot.CurrentInput != read.Current || entry.Snapshot.Health != MonitorHealth.Responding;
            entry.Snapshot = entry.Snapshot with { Health = MonitorHealth.Responding, CurrentInput = read.Current, HasAnswered = true, InputReadUtc = DateTime.UtcNow };
        }
        if (changed) RaiseChanged();
        return read.Current;
    }

    /// <summary>Reads a VCP code (brightness, contrast, volume...). Null if the monitor didn't answer.</summary>
    public async Task<(uint Current, uint Maximum)?> ReadVcpAsync(string monitorId, byte code)
    {
        Entry? entry;
        lock (_lock) entry = _entries.FirstOrDefault(e => e.Snapshot.Id == monitorId);
        if (entry?.Channel is null) return null;
        var read = await entry.Channel.ReadAsync(code, ReadAttempts).ConfigureAwait(false);
        return read.Ok ? (read.Current, read.Maximum) : null;
    }

    /// <summary>Sets a VCP code other than the input (brightness, contrast, volume, mute). False if it didn't go through.</summary>
    public async Task<bool> SetVcpAsync(string monitorId, byte code, uint value)
    {
        Entry? entry;
        lock (_lock) entry = _entries.FirstOrDefault(e => e.Snapshot.Id == monitorId);
        if (entry?.Channel is null) return false;
        if (ReadOnly)
        {
            Log.Info($"{entry.Snapshot.Name}: not setting VCP 0x{code:X2} to {value}, started with --no-switch");
            return false;
        }
        var write = await entry.Channel.WriteAsync(code, value, 1).ConfigureAwait(false);
        return write.Ok;
    }

    private static long Ms(long from, long to) => Math.Max(0, (to - from) * 1000 / Stopwatch.Frequency);

    /// <summary>
    /// Sends the input command several times without waiting for an answer, for when the monitor can't
    /// answer anyway (screens off). Returns how many sends Windows accepted.
    /// </summary>
    public async Task<int> SendRepeatedlyAsync(string monitorId, uint value, int count, TimeSpan interval)
    {
        Entry? entry;
        lock (_lock) entry = _entries.FirstOrDefault(e => e.Snapshot.Id == monitorId);
        if (entry?.Channel is null) return 0;
        if (ReadOnly)
        {
            Log.Info($"{entry.Snapshot.Name}: not sending {InputCatalog.FormatValue(value)}, started with --no-switch");
            return 0;
        }

        int accepted = 0;
        for (int i = 0; i < count; i++)
        {
            if (i > 0) await Task.Delay(interval).ConfigureAwait(false);
            var write = await entry.Channel.WriteAsync(VcpCodes.InputSource, value, 1).ConfigureAwait(false);
            if (write.Ok) accepted++;
        }
        Log.Info($"{entry.Snapshot.Name}: sent {InputCatalog.FormatValue(value)} {count} times, {accepted} accepted");
        return accepted;
    }

    /// <summary>Records an input we believe the monitor is on when it can't be read back.</summary>
    public void AssumeInput(string monitorId, uint value)
    {
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => e.Snapshot.Id == monitorId);
            if (entry is not null) entry.Snapshot = entry.Snapshot with { CurrentInput = value, InputReadUtc = default };
        }
    }

    /// <summary>Forgets the capabilities string of a monitor and reads it again (About page, "Read again").</summary>
    public void RereadCapabilities(string monitorId)
    {
        Entry? entry;
        lock (_lock)
        {
            _capabilitiesCache.Remove(monitorId);
            entry = _entries.FirstOrDefault(e => e.Snapshot.Id == monitorId);
        }
        SaveCapabilitiesFile();
        if (entry is null) return;
        Update(entry, s => s with { CapabilitiesFailed = false, CapabilitiesText = null });
        RaiseChanged();
        StartCapabilitiesRead(entry);
    }

    public async ValueTask DisposeAsync()
    {
        List<Entry> entries;
        lock (_lock)
        {
            entries = _entries;
            _entries = new List<Entry>();
        }
        foreach (var e in entries)
            if (e.Channel is not null) await e.Channel.DisposeAsync().ConfigureAwait(false);
    }

    private async Task EnumerateAsync()
    {
        if (!_capabilitiesFileLoaded)
        {
            _capabilitiesFileLoaded = true;
            LoadCapabilitiesFile();
        }
        var (detected, connections) = await Task.Run(() => (MonitorEnumerator.Enumerate(), DisplayConnections.Read())).ConfigureAwait(false);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fresh = new List<Entry>();

        foreach (var d in detected.OrderByDescending(d => d.IsPrimary).ThenBy(d => d.Bounds.Left).ThenBy(d => d.Bounds.Top))
        {
            var edid = EdidReader.TryRead(d.DeviceInterface);
            string id = MonitorIdentity.MakeUnique(MonitorIdentity.MakeId(edid, d.GdiDevice), taken);
            string name = MonitorIdentity.MakeName(edid, null, d.Description);
            (IReadOnlyList<uint> Inputs, string Text)? caps;
            bool answered;
            lock (_lock)
            {
                caps = _capabilitiesCache.TryGetValue(id, out var cached) ? cached : null;
                answered = _answered.Contains(id);
            }
            var connection = d.DeviceInterface is not null && connections.TryGetValue(d.DeviceInterface, out var kind) ? kind : ConnectionKind.Unknown;
            // As when the capabilities string is read: its model only when the EDID gave no real name.
            if (name is "Monitor" && caps is not null && CapabilitiesParser.GetModel(caps.Value.Text) is { } cachedModel) name = cachedModel;

            if (!d.HasHandle) Log.Warn($"{name} ({id}): Windows couldn't open it for DDC/CI: {d.Error}");

            fresh.Add(new Entry(
                d.HasHandle ? new DdcChannel(d.Handle, name) : null,
                new MonitorSnapshot(
                    id,
                    name,
                    d.IsPrimary,
                    d.Bounds,
                    d.HasHandle ? MonitorHealth.Unknown : MonitorHealth.Unavailable,
                    null,
                    caps?.Inputs,
                    false,
                    connection,
                    caps?.Text,
                    answered)));
        }

        List<Entry> old;
        lock (_lock)
        {
            old = _entries;
            _entries = fresh;
        }

        Log.Info($"Found {fresh.Count} monitor(s): {string.Join(", ", fresh.Select(e => $"{e.Snapshot.Name} [{e.Snapshot.Id}]"))}");

        foreach (var e in old)
            if (e.Channel is not null) _ = e.Channel.DisposeAsync().AsTask();
    }

    private async Task ReadInputAsync(Entry entry)
    {
        var read = await entry.Channel!.ReadAsync(VcpCodes.InputSource, ReadAttempts).ConfigureAwait(false);
        if (read.Ok)
        {
            lock (_lock) _answered.Add(entry.Snapshot.Id);
        }
        Update(entry, s => read.Ok
            ? s with { Health = MonitorHealth.Responding, CurrentInput = read.Current, HasAnswered = true, InputReadUtc = DateTime.UtcNow }
            : s with { Health = MonitorHealth.NotResponding, CurrentInput = null, InputReadUtc = default });
    }

    private void StartCapabilitiesRead(Entry entry)
    {
        if (entry.Channel is null) return;
        string id = entry.Snapshot.Id;
        lock (_lock)
        {
            if (_capabilitiesCache.ContainsKey(id) || !_capabilitiesInFlight.Add(id)) return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var caps = await entry.Channel.ReadCapabilitiesAsync(CapabilityAttempts).ConfigureAwait(false);
                if (caps.Ok)
                {
                    var inputs = CapabilitiesParser.GetInputSources(caps.Text);
                    string? model = CapabilitiesParser.GetModel(caps.Text);
                    lock (_lock) _capabilitiesCache[id] = (inputs, caps.Text!);
                    SaveCapabilitiesFile();
                    Update(entry, s => s with
                    {
                        AdvertisedInputs = inputs,
                        CapabilitiesText = caps.Text,
                        CapabilitiesFailed = false,
                        // Only use the capabilities model when the EDID gave no real name.
                        Name = s.Name is "Monitor" && model is not null ? model : s.Name,
                    });
                }
                else
                {
                    Update(entry, s => s with { CapabilitiesFailed = true });
                }
                RaiseChanged();
            }
            catch (Exception ex)
            {
                Log.Error("Reading capabilities failed", ex);
            }
            finally
            {
                lock (_lock) _capabilitiesInFlight.Remove(id);
            }
        });
    }

    private void LoadCapabilitiesFile()
    {
        if (CapabilitiesFile is null || !File.Exists(CapabilitiesFile)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CapabilitiesFile));
            if (saved is null) return;
            lock (_lock)
            {
                foreach (var (id, text) in saved)
                    if (!string.IsNullOrWhiteSpace(text) && !_capabilitiesCache.ContainsKey(id))
                        _capabilitiesCache[id] = (CapabilitiesParser.GetInputSources(text), text);
            }
            Log.Info($"Capabilities from {Path.GetFileName(CapabilitiesFile)}: {string.Join(", ", saved.Keys)}");
        }
        catch (Exception ex)
        {
            // Not worth bothering anyone: they're read from the monitors again.
            Log.Warn($"Couldn't read {CapabilitiesFile}: {ex.Message}");
        }
    }

    private void SaveCapabilitiesFile()
    {
        if (CapabilitiesFile is null) return;
        Dictionary<string, string> copy;
        lock (_lock) copy = _capabilitiesCache.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(c => c.Key, c => c.Value.Text);
        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CapabilitiesFile)!);
                string temp = CapabilitiesFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, CapabilitiesFile, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't save {CapabilitiesFile}: {ex.Message}");
        }
    }

    private void Update(Entry entry, Func<MonitorSnapshot, MonitorSnapshot> change)
    {
        lock (_lock) entry.Snapshot = change(entry.Snapshot);
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("A monitor change handler failed", ex);
        }
    }

    private sealed class Entry(DdcChannel? channel, MonitorSnapshot snapshot)
    {
        public DdcChannel? Channel { get; } = channel;

        public MonitorSnapshot Snapshot { get; set; } = snapshot;

        /// <summary>Counts switches on this monitor, so an older switch's read-back can tell a newer one started.</summary>
        public int SwitchGeneration;
    }
}
