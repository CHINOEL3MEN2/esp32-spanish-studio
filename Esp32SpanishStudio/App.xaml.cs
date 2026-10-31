using System.Windows;

namespace Esp32SpanishStudio;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "Error inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }
}
