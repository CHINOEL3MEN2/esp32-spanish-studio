using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Esp32SpanishStudio.Infrastructure;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _run;
    private readonly Func<bool>? _canRun;

    public RelayCommand(Action run, Func<bool>? canRun = null) : this(_ => run(), canRun) { }
    public RelayCommand(Action<object?> run, Func<bool>? canRun = null) { _run = run; _canRun = canRun; }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canRun?.Invoke() ?? true;
    public void Execute(object? parameter) => _run(parameter);
}

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _run;
    private readonly Func<bool>? _canRun;

    public AsyncCommand(Func<Task> run, Func<bool>? canRun = null) { _run = run; _canRun = canRun; }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canRun?.Invoke() ?? true;
    public async void Execute(object? parameter) => await _run();
}
