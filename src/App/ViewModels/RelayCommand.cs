using System;
using System.Windows.Input;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// A minimal <see cref="ICommand"/> implementation that relays its execution
/// to delegates supplied at construction. Hand-rolled to avoid an MVVM framework dependency.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = _ => execute();
        _canExecute = canExecute is null ? null : _ => canExecute();
    }

    // The command's OWN notification, raised DIRECTLY by RaiseCanExecuteChanged (T-111). Previously
    // CanExecuteChanged forwarded solely to CommandManager.RequerySuggested, so a VM's explicit
    // RaiseCanExecuteChanged() only nudged WPF's heuristic, weak-referenced global requery — it never
    // deterministically told a bound control "your gate changed, re-query me now." That is why an apply
    // command's enabled-state could go stale after the first use (it re-evaluated only when the
    // CommandManager happened to requery on unrelated UI input). We now keep BOTH: subscribers are hooked
    // to the own-event AND to CommandManager.RequerySuggested, so RaiseCanExecuteChanged notifies them
    // deterministically while the automatic input-driven requery (and the cross-command "global requery"
    // via InvalidateRequerySuggested) is fully preserved.
    private EventHandler? _canExecuteChanged;

    public event EventHandler? CanExecuteChanged
    {
        add
        {
            _canExecuteChanged += value;
            CommandManager.RequerySuggested += value;
        }
        remove
        {
            _canExecuteChanged -= value;
            CommandManager.RequerySuggested -= value;
        }
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged()
    {
        // Direct, deterministic notification to the command's own subscribers…
        _canExecuteChanged?.Invoke(this, EventArgs.Empty);
        // …plus WPF's global requery, so every other bound RelayCommand re-evaluates too (unchanged behavior).
        CommandManager.InvalidateRequerySuggested();
    }
}
