using System.Windows;
using System.Windows.Threading;

namespace Coach.App;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherError;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                DiagnosticLog.Error("app.unhandled_exception", ex);
            else
                DiagnosticLog.Write("app.unhandled_exception", new { description = "Unknown exception object" });
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            DiagnosticLog.Error("app.unobserved_task_exception", e.Exception);
            e.SetObserved();
        };
    }

    private void OnDispatcherError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticLog.Error("app.dispatcher_exception", e.Exception);
        // Unknown UI errors must not disappear silently. Keep stack trace in logs;
        // nonfatal UI exceptions can be reported without terminating the match.
        e.Handled = true;
    }
}
