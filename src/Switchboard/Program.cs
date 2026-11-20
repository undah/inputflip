using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Switchboard.Core;
using Switchboard.Core.Branding;
using Switchboard.Core.Settings;
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
                // Don't leave a start-with-Windows entry pointing at a deleted exe.
                try { new StartupRegistration().Disable(); } catch { }
            })
            .Run();

        Options = StartupOptions.Parse(args, Environment.GetEnvironmentVariable("SWITCHBOARD_DATA_DIR"));

        if (Options.ExportIconPath is { } iconPath)
        {
            File.WriteAllBytes(iconPath, IconArt.CreateIco(IconArt.IcoSizes));
            return 0;
        }

        AppPaths.UseDataDirectory(Options.DataDirectory);
        foreach (string problem in Options.Problems) Log.Warn($"Command line: {problem}");

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
