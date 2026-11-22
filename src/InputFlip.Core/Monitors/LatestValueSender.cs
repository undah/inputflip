namespace InputFlip.Core.Monitors;

/// <summary>
/// Sends slider values to a monitor without piling up DDC/CI commands: one send at a time, and while one is on its
/// way only the newest value waits. A brightness drag makes dozens of values a second; a monitor takes one in about
/// 50 ms, and on some PCs every DDC/CI call waits for the others, so a backlog would hold up input switches.
/// </summary>
public sealed class LatestValueSender
{
    private readonly Func<uint, Task> _send;
    private readonly object _lock = new();
    private uint? _waiting;
    private bool _sending;
    private Task _idle = Task.CompletedTask;

    public LatestValueSender(Func<uint, Task> send) => _send = send;

    /// <summary>Queues a value. Returns at once.</summary>
    public void Submit(uint value)
    {
        lock (_lock)
        {
            _waiting = value;
            if (_sending) return;
            _sending = true;
            _idle = Task.Run(PumpAsync);
        }
    }

    /// <summary>Finishes when nothing is waiting or being sent (for tests and before closing).</summary>
    public Task WhenIdle()
    {
        lock (_lock) return _idle;
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            uint value;
            lock (_lock)
            {
                if (_waiting is not uint next)
                {
                    _sending = false;
                    return;
                }
                value = next;
                _waiting = null;
            }
            try
            {
                await _send(value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Sending a level to a monitor failed: {ex.Message}");
            }
        }
    }
}
