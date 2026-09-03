using System;
using System.Windows;
using System.Windows.Threading;

namespace DeployToSolution
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnUnhandled;
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
                MessageBox.Show(args.ExceptionObject?.ToString(), "Unhandled error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(e.Exception.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
