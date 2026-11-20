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
    bool HasAnswered = false);

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

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _lock = new();
    private readonly Dictionary<string, (IReadOnlyList<uint> Inputs, string Text)> _capabilitiesCache = new();
    private readonly HashSet<string> _answered = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _capabilitiesInFlight = new();
    private List<Entry> _entries = new();

    /// <summary>Never send a switch command (the --no-switch option). Reads still work.</summary>
    public bool ReadOnly { get; set; }

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
    public async Task RefreshAsync(bool enumerate)
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (enumerate || _entries.Count == 0)
                await EnumerateAsync().ConfigureAwait(false);

            List<Entry> entries;
            lock (_lock) entries = _entries.ToList();
            await Task.WhenAll(entries.Where(e => e.Channel is not null).Select(ReadInputAsync)).ConfigureAwait(false);
            RaiseChanged();

            foreach (var entry in entries)
                StartCapabilitiesRead(entry);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<SwitchOutcome> SwitchAsync(string monitorId, uint value)
    {
        Entry? entry;
        lock (_lock) entry = _entries.FirstOrDefault(e => e.Snapshot.Id == monitorId);
        if (entry?.Channel is null) return new SwitchOutcome(SwitchStatus.MonitorGone, null);
        if (ReadOnly)
        {
            Log.Info($"{entry.Snapshot.Name}: not switching to {InputCatalog.FormatValue(value)}, started with --no-switch");
            return new SwitchOutcome(SwitchStatus.Failed, null);
        }

        Log.Info($"{entry.Snapshot.Name}: switching to {InputCatalog.FormatValue(value)}");
        var write = await entry.Channel.WriteAsync(VcpCodes.InputSource, value, WriteAttempts).ConfigureAwait(false);
        if (!write.Ok)
        {
            Update(entry, s => s with { Health = MonitorHealth.NotResponding });
            return new SwitchOutcome(SwitchStatus.Failed, null);
        }

        // Monitors take a moment to report the new input (about 150 ms on the MSI G27CQ4 over HDMI).
        uint? lastReported = null;
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(150).ConfigureAwait(false);
            var read = await entry.Channel.ReadAsync(VcpCodes.InputSource, 1).ConfigureAwait(false);
            if (!read.Ok) continue;
            lastReported = read.Current;
            if (InputCatalog.Matches(read.Current, value))
            {
                Update(entry, s => s with { Health = MonitorHealth.Responding, CurrentInput = value, HasAnswered = true });
                Log.Info($"{entry.Snapshot.Name}: confirmed on {InputCatalog.FormatValue(value)} after {(i + 1) * 150} ms");
                return new SwitchOutcome(SwitchStatus.Confirmed, read.Current);
            }
        }

        if (lastReported is uint other)
        {
            Update(entry, s => s with { Health = MonitorHealth.Responding, CurrentInput = other });
            Log.Info($"{entry.Snapshot.Name}: still reports {InputCatalog.FormatValue(other)}");
            return new SwitchOutcome(SwitchStatus.StillOnOtherInput, other);
        }

        // A failed read-back doesn't mean the command didn't arrive.
        Update(entry, s => s with { CurrentInput = null });
        Log.Info($"{entry.Snapshot.Name}: switch sent, no read-back");
        return new SwitchOutcome(SwitchStatus.Unconfirmed, null);
    }

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
            if (entry is not null) entry.Snapshot = entry.Snapshot with { CurrentInput = value };
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
            ? s with { Health = MonitorHealth.Responding, CurrentInput = read.Current, HasAnswered = true }
            : s with { Health = MonitorHealth.NotResponding, CurrentInput = null });
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
    }
}
