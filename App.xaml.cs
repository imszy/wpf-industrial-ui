using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PlantHmi;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    /// <summary>Keeps the HMI alive and leaves a trace instead of vanishing on an unexpected error.</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryLog(e.Exception);
        MessageBox.Show(
            "AURORA-7 HMI recovered from an unexpected error.\n\n" + e.Exception.Message,
            "Plant HMI — recovered",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static void TryLog(Exception ex)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "PlantHmi-errors.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch
        {
            // logging is best-effort only
        }
    }
}
