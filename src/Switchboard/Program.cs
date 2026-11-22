using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Switchboard.Core;
using Switchboard.Core.Actions;
using Switchboard.Core.Branding;
using Switchboard.Core.Settings;
using Switchboard.Native;
using Velopack;

namespace Switchboard;

public static class Program
{
    public static StartupOptions Options { get; private set; } = StartupOptions.Parse([]);

    [STAThread]
    private static int Main(string[] args)
    {
        // Velopack runs install, update and uninstall hooks here and exits; on a normal start it returns at once.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ =>
            {
                // Don't leave a start-with-Windows entry or inputflip:// links pointing at a deleted exe.
                try { new StartupRegistration().Disable(); } catch { }
                try { new LinkRegistration().Unregister(); } catch { }
            })
            .Run();

        Options = StartupOptions.Parse(args,
            Environment.GetEnvironmentVariable("INPUTFLIP_DATA_DIR") ?? Environment.GetEnvironmentVariable("SWITCHBOARD_DATA_DIR"));

        if (Options.ExportIconPath is { } iconPath)
        {
            File.WriteAllBytes(iconPath, IconArt.CreateIco(IconArt.IcoSizes));
            return 0;
        }

        AppPaths.UseDataDirectory(Options.DataDirectory);
        foreach (string problem in Options.Problems) Log.Warn($"Command line: {problem}");

        // A command (--switch MSI PS5, an inputflip:// link): hand it to the copy that's running and print its answer.
        var command = RemoteCommand.FromArgs(args, out string? commandProblem);
        if (commandProblem is not null)
        {
            Print(commandProblem);
            return 2;
        }
        if (command is not null)
        {
            var reply = CommandPipeHost.TrySend(App.CommandPipeName, command.ToArgs(), TimeSpan.FromSeconds(15));
            if (reply is not null)
            {
                Print(reply.Message);
                return reply.Ok ? 0 : 1;
            }
            // Nothing running: start InputFlip and run the command once it has found the monitors.
            AppController.StartupCommand = command;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>
    /// Prints to the console InputFlip was started from, if any. It's a Windows app, so it has no console of its own;
    /// it borrows the parent's, or writes to a pipe when the output is redirected.
    /// </summary>
    private static void Print(string text)
    {
        try
        {
            AttachConsole(-1); // ATTACH_PARENT_PROCESS; fails harmlessly when there's none
            using var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            stdout.WriteLine(text);
        }
        catch
        {
            // Started from a shortcut or a link: nowhere to print, and the toast or the window says what happened.
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
