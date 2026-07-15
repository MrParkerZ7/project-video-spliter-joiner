using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// Minimal hand-rolled <see cref="INotifyPropertyChanged"/> base for view models.
/// Kept dependency-free on purpose (no CommunityToolkit.Mvvm) to avoid a restore dependency.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Sets <paramref name="field"/> to <paramref name="value"/> and raises
    /// <see cref="PropertyChanged"/> when the value actually changed.
    /// </summary>
    /// <returns><c>true</c> when the value changed; otherwise <c>false</c>.</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
