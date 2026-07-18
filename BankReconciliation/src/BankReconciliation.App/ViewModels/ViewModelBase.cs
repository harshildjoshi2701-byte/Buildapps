using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BankReconciliation.App.ViewModels;

/// <summary>Standard MVVM base: raises <see cref="PropertyChanged"/> and
/// provides the usual SetField helper to keep property setters one-liners.</summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
