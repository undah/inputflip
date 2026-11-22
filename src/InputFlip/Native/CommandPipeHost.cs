using System.IO.Pipes;
using System.Text;
using Microsoft.UI.Dispatching;
using InputFlip.Core;
using InputFlip.Core.Actions;

namespace InputFlip.Native;

/// <summary>
/// Commands from a second start of InputFlip (<c>InputFlip.exe --switch MSI PS5</c>, an <c>inputflip://</c> link):
/// that copy sends its arguments down a named pipe, this one runs them and answers with one line. Only the same
/// Windows user can connect.
/// </summary>
internal sealed class CommandPipeHost : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<IReadOnlyList<string>, Task<CommandPipe.Reply>> _run;
    private readonly DispatcherQueue _dispatcher;
    private readonly CancellationTokenSource _stop = new();

    public CommandPipeHost(string pipeName, DispatcherQueue dispatcher, Func<IReadOnlyList<string>, Task<CommandPipe.Reply>> run)
    {
        _pipeName = pipeName;
        _dispatcher = dispatcher;
        _run = run;
    }

    public void Start() => _ = Task.Run(ListenAsync);

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                string? line = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                var args = CommandPipe.DecodeRequest(line);
                var reply = args is null ? new CommandPipe.Reply(false, "InputFlip couldn't read that command.") : await OnUiThread(args).ConfigureAwait(false);
                await writer.WriteLineAsync(CommandPipe.EncodeReply(reply)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Command pipe: {ex.Message}");
                try { await Task.Delay(500, _stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private Task<CommandPipe.Reply> OnUiThread(IReadOnlyList<string> args)
    {
        var done = new TaskCompletionSource<CommandPipe.Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = _dispatcher.TryEnqueue(async () =>
        {
            try { done.SetResult(await _run(args)); }
            catch (Exception ex)
            {
                Log.Error("Running a command failed", ex);
                done.SetResult(new CommandPipe.Reply(false, "Something went wrong running that command. The log has the details."));
            }
        });
        if (!queued) done.SetResult(new CommandPipe.Reply(false, "InputFlip is closing."));
        return done.Task;
    }

    /// <summary>
    /// Sends a command to the copy that's running. Null when none is (nothing listens on the pipe).
    /// </summary>
    public static CommandPipe.Reply? TrySend(string pipeName, IReadOnlyList<string> args, TimeSpan timeout)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(300);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(CommandPipe.EncodeRequest(args));
            var read = reader.ReadLineAsync();
            if (!read.Wait(timeout)) return new CommandPipe.Reply(false, "InputFlip didn't answer in time.");
            return CommandPipe.DecodeReply(read.Result) ?? new CommandPipe.Reply(false, "InputFlip's answer couldn't be read.");
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stop.Cancel();
}
