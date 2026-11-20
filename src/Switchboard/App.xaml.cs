using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Switchboard.Core;

namespace Switchboard;

public partial class App : Application
{
    private const string MutexName = @"Local\Switchboard.SingleInstance";
    private const string ShowEventName = @"Local\Switchboard.Show";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private AppController? _controller;

    public App()
    {
        InitializeComponent();
        // A tray app has no main window; keep running until Quit.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled exception", e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out bool first);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!first)
        {
            // Already running: ask that copy to show its window, then leave.
            _showEvent.Set();
            Exit();
            return;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _controller = new AppController(dispatcher);
        _controller.Start();

        var showEvent = _showEvent;
        var thread = new Thread(() =>
        {
            while (showEvent.WaitOne())
                dispatcher.TryEnqueue(() => _controller?.ShowFlyout());
        })
        {
            IsBackground = true,
            Name = "Switchboard single-instance listener",
        };
        thread.Start();
    }
}
