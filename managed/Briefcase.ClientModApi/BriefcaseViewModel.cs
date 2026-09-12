using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Briefcase.ClientModApi;

/// <summary>
/// Small dependency-free ViewModel base for client mods. Property notifications
/// can be raised from any thread; Briefcase dispatches retained UI updates to the
/// Avalonia thread.
/// </summary>
public abstract class BriefcaseViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        RaisePropertyChanged(propertyName);
        return true;
    }

    protected void RaisePropertyChanged(
        [CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected void RaisePropertiesChanged(params string[] propertyNames)
    {
        ArgumentNullException.ThrowIfNull(propertyNames);
        foreach (var propertyName in propertyNames) RaisePropertyChanged(propertyName);
    }
}