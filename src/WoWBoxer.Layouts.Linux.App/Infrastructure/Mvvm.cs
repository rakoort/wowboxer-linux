using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace WoWBoxer.Layouts.App.Infrastructure;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        execute(parameter);
        CommandManager.InvalidateRequerySuggested();
    }
}

/// <summary>
/// WPF's CommandManager, which Avalonia does not have: every <see cref="RelayCommand"/> asks again whether it can run
/// after an input event or when the view model says so. Coalesced into one re-query per dispatcher turn.
/// </summary>
public static class CommandManager
{
    private static bool _queued;

    public static event EventHandler? RequerySuggested;

    public static void InvalidateRequerySuggested()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            RequerySuggested?.Invoke(null, EventArgs.Empty);
        }, DispatcherPriority.Background);
    }

    /// <summary>Re-query after every click and key in the app, as WPF does (called once at start).</summary>
    public static void RequeryOnInput()
    {
        InputElement.PointerReleasedEvent.AddClassHandler<InputElement>((_, _) => InvalidateRequerySuggested(), RoutingStrategies.Bubble, handledEventsToo: true);
        InputElement.KeyUpEvent.AddClassHandler<InputElement>((_, _) => InvalidateRequerySuggested(), RoutingStrategies.Bubble, handledEventsToo: true);
        InputElement.GotFocusEvent.AddClassHandler<InputElement>((_, _) => InvalidateRequerySuggested(), RoutingStrategies.Bubble, handledEventsToo: true);
    }
}
