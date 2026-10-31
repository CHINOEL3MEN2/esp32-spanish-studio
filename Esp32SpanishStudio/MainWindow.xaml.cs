using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Esp32SpanishStudio.ViewModels;

namespace Esp32SpanishStudio;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.Logs.CollectionChanged += OnLogsChanged;
        Closing += (_, e) =>
        {
            if (_vm.IsBusy &&
                MessageBox.Show("Hay una tarea en curso. ¿Cancelarla y salir?", "ESP32 Spanish Studio",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _vm.Dispose();
        };
    }

    private bool _scrollPending;

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _scrollPending) return;
        _scrollPending = true;
        // Scrolling inside the collection event forces a re-entrant layout pass; defer and coalesce it.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scrollPending = false;
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        });
    }

    private void MonitorBox_TextChanged(object sender, TextChangedEventArgs e) => MonitorBox.ScrollToEnd();
}
