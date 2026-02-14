using System.Windows;
using System.Windows.Threading;
using MyNotes.Desktop.Services;

namespace MyNotes.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global exception handlers for diagnostics
        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show($"Unhandled Exception:\n\n{args.Exception.Message}\n\n{args.Exception.StackTrace}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                MessageBox.Show($"Fatal Exception:\n\n{ex.Message}\n\n{ex.StackTrace}",
                    "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        try
        {
            // Initialize the SQLite database
            DatabaseService.Instance.InitializeDatabase();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Database initialization failed:\n\n{ex.Message}\n\n{ex.StackTrace}",
                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
