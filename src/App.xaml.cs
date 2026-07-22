using System.Windows;
using System.Windows.Threading;
using ACRLiveTiming.UI;

namespace ACRLiveTiming
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Last-chance handlers: without these, any uncaught UI-thread exception
            // kills the process with no message at all. Show the error and keep the
            // app alive when the dispatcher survives; background-task exceptions are
            // logged-and-swallowed (they have no UI to die on).
            DispatcherUnhandledException += OnDispatcherException;
            TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

            new MainWindow().Show();
        }

        void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            MessageBox.Show(
                "Unexpected error:\n\n" + e.Exception.Message,
                "ACR Live Timing", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
