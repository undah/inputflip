using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Switchboard.Core;

namespace Switchboard;

public partial class App : Application
{
    private const string MutexName = @"Local\Switchboard.SingleInstance";
    private const string ShowEventName = @"Local\Switchboard.Show";

    // A copy started with another data folder (testing, --data-dir) runs alongside the normal one.
    private static string InstanceSuffix => AppPaths.IsCustomDataDirectory
        ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.DataDirectory.ToLowerInvariant())))[..12]
        : "";

    /// <summary>Where a second start sends its command (--switch, a link) to the copy that is running.</summary>
    public static string CommandPipeName => "InputFlip.Commands." + Environment.UserName + InstanceSuffix;

    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private AppController? _controller;

    public App()
    {
        InitializeComponent();
        // Keep running until Quit, even while the window is being recreated or closed.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled exception", e.Exception);
            e.Handled = true;
        };
        // A missing theme resource or a broken binding is a bug worth seeing in the log.
        DebugSettings.BindingFailed += (_, e) => Log.Warn("Binding failed: " + e.Message);
        DebugSettings.XamlResourceReferenceFailed += (_, e) => Log.Warn("Resource missing: " + e.Message);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstance = new Mutex(initiallyOwned: true, MutexName + InstanceSuffix, out bool first);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName + InstanceSuffix);
        if (!first)
        {
            // Already running: ask that copy to show its window, then leave.
            _showEvent.Set();
            Exit();
            return;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _controller = new AppController(dispatcher, Program.Options);
        _controller.Start();

        var showEvent = _showEvent;
        var thread = new Thread(() =>
        {
            while (showEvent.WaitOne())
                dispatcher.TryEnqueue(() => _controller?.ShowWindow());
        })
        {
            IsBackground = true,
            Name = "InputFlip single-instance listener",
        };
        thread.Start();
    }
}
