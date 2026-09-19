using System.Windows;
using System.Windows.Threading;
using Flamoris.Logging;
using Kachinco.Infrastructure;

namespace Kachinco.App;

public partial class App : Application
{
    private FlamorisLogger? logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        var logging = KachincoLogging.Create(diagnostic: message => System.Diagnostics.Debug.WriteLine(message));
        logger = logging.Logger;
        RegisterExceptionObservers();
        try
        {
            logger.Info("app.startup", "Application starting", new Dictionary<string, object?>
            {
                ["version"] = typeof(App).Assembly.GetName().Version?.ToString(),
                ["argumentsCount"] = e.Args.Length,
            });
            base.OnStartup(e);
            MainWindow = new MainWindow(logger);
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            logger.Error("app.startup", "Application startup failed", exception);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        logger?.Info("app.shutdown", "Application stopped",
            new Dictionary<string, object?> { ["exitCode"] = e.ApplicationExitCode });
        base.OnExit(e);
    }

    private void RegisterExceptionObservers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            logger?.Error("app", "Fatal application exception", eventArgs.ExceptionObject as Exception,
                new Dictionary<string, object?> { ["terminating"] = eventArgs.IsTerminating });
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
            logger?.Error("app", "Unobserved task exception", eventArgs.Exception);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) =>
        logger?.Error("app", "Unhandled dispatcher exception", e.Exception);
}
