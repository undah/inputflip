using System.Diagnostics;
using System.Text;
using Switchboard.Core.Interop;

namespace Switchboard.Core.Monitors;

public readonly record struct VcpReading(bool Ok, uint Current, uint Maximum, int Attempts, long Milliseconds, string? Error);

public readonly record struct DdcWrite(bool Ok, int Attempts, long Milliseconds, string? Error);

public readonly record struct CapabilitiesReading(bool Ok, string? Text, int Attempts, long Milliseconds, string? Error);

/// <summary>
/// DDC/CI access to one physical monitor. Commands run one at a time on a background thread,
/// with retries and a timeout, because DDC/CI is slow and monitors get confused by overlapping requests.
/// </summary>
internal sealed class DdcChannel : IAsyncDisposable
{
    private static readonly TimeSpan VcpTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CapabilitiesTimeout = TimeSpan.FromSeconds(10);

    private readonly IntPtr _handle;
    private readonly string _label;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public DdcChannel(IntPtr handle, string label)
    {
        _handle = handle;
        _label = label;
    }

    public Task<VcpReading> ReadAsync(byte code, int attempts) =>
        RunAsync(
            () => Read(code, attempts),
            VcpTimeout,
            new VcpReading(false, 0, 0, attempts, (long)VcpTimeout.TotalMilliseconds, "Timed out"));

    public Task<DdcWrite> WriteAsync(byte code, uint value, int attempts) =>
        RunAsync(
            () => Write(code, value, attempts),
            VcpTimeout,
            new DdcWrite(false, attempts, (long)VcpTimeout.TotalMilliseconds, "Timed out"));

    public Task<CapabilitiesReading> ReadCapabilitiesAsync(int attempts) =>
        RunAsync(
            () => ReadCapabilities(attempts),
            CapabilitiesTimeout,
            new CapabilitiesReading(false, null, attempts, (long)CapabilitiesTimeout.TotalMilliseconds, "Timed out"));

    public async ValueTask DisposeAsync()
    {
        // Wait for the command in flight, then release the handle so nothing uses it afterwards.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (!NativeMethods.DestroyPhysicalMonitor(_handle))
                Log.Warn($"{_label}: releasing the monitor handle failed: {NativeMethods.LastErrorText()}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> RunAsync<T>(Func<T> work, TimeSpan timeout, T timedOut)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        if (_disposed)
        {
            _gate.Release();
            return timedOut;
        }

        var task = Task.Run(work);
        var finished = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (finished == task)
        {
            _gate.Release();
            return await task.ConfigureAwait(false);
        }

        // The native call can't be cancelled. Keep the channel locked until it returns.
        Log.Warn($"{_label}: DDC/CI call took longer than {timeout.TotalSeconds:0} s");
        _ = task.ContinueWith(_ => _gate.Release(), TaskScheduler.Default);
        return timedOut;
    }

    private VcpReading Read(byte code, int attempts)
    {
        var clock = Stopwatch.StartNew();
        string? error = null;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(_handle, code, IntPtr.Zero, out uint current, out uint maximum))
                return new VcpReading(true, current, maximum, attempt, clock.ElapsedMilliseconds, null);
            error = NativeMethods.LastErrorText();
            if (attempt < attempts) Thread.Sleep(Backoff(attempt));
        }
        Log.Info($"{_label}: reading VCP 0x{code:X2} failed after {attempts} tries: {error}");
        return new VcpReading(false, 0, 0, attempts, clock.ElapsedMilliseconds, error);
    }

    private DdcWrite Write(byte code, uint value, int attempts)
    {
        var clock = Stopwatch.StartNew();
        string? error = null;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            if (NativeMethods.SetVCPFeature(_handle, code, value))
                return new DdcWrite(true, attempt, clock.ElapsedMilliseconds, null);
            error = NativeMethods.LastErrorText();
            if (attempt < attempts) Thread.Sleep(Backoff(attempt));
        }
        Log.Info($"{_label}: setting VCP 0x{code:X2} to 0x{value:X2} failed after {attempts} tries: {error}");
        return new DdcWrite(false, attempts, clock.ElapsedMilliseconds, error);
    }

    private CapabilitiesReading ReadCapabilities(int attempts)
    {
        var clock = Stopwatch.StartNew();
        string? error = null;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            if (!NativeMethods.GetCapabilitiesStringLength(_handle, out uint length))
            {
                error = NativeMethods.LastErrorText();
            }
            else if (length == 0)
            {
                error = "The monitor returned an empty capabilities string.";
            }
            else
            {
                var buffer = new byte[length];
                if (NativeMethods.CapabilitiesRequestAndCapabilitiesReply(_handle, buffer, length))
                {
                    int end = Array.IndexOf(buffer, (byte)0);
                    if (end < 0) end = buffer.Length;
                    string text = Encoding.ASCII.GetString(buffer, 0, end);
                    Log.Info($"{_label}: capabilities ({clock.ElapsedMilliseconds} ms): {text}");
                    return new CapabilitiesReading(true, text, attempt, clock.ElapsedMilliseconds, null);
                }
                error = NativeMethods.LastErrorText();
            }
            if (attempt < attempts) Thread.Sleep(Backoff(attempt) * 2);
        }
        Log.Info($"{_label}: reading the capabilities string failed after {attempts} tries: {error}");
        return new CapabilitiesReading(false, null, attempts, clock.ElapsedMilliseconds, error);
    }

    // 100 ms, 200 ms, 400 ms...
    private static int Backoff(int attempt) => 100 << Math.Min(attempt - 1, 4);
}
