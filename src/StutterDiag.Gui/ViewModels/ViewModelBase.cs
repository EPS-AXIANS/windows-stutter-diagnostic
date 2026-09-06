using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StutterDiag.Gui.Services;

namespace StutterDiag.Gui.ViewModels;

/// <summary>
/// Base for the page view-models. Holds the shared <see cref="ServiceConnection"/> and
/// re-raises availability changes so pages can enable/disable their commands and show the
/// non-blocking "service unavailable" hint (docs/ARCHITECTURE.md §3 — the GUI is optional
/// and must degrade gracefully, never crash).
/// </summary>
public abstract class ViewModelBase : ObservableObject, IDisposable
{
    protected ViewModelBase(ServiceConnection service)
    {
        Service = service;
        Service.PropertyChanged += OnServicePropertyChanged;
    }

    protected ServiceConnection Service { get; }

    public bool IsServiceAvailable => Service.IsServiceAvailable;

    /// <summary>Short hint shown on each page when the backend cannot be reached.</summary>
    public string ServiceHint => Service.IsServiceAvailable ? string.Empty : ServiceConnection.ServiceDownMessage;

    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServiceConnection.IsServiceAvailable))
        {
            OnPropertyChanged(nameof(IsServiceAvailable));
            OnPropertyChanged(nameof(ServiceHint));
            OnServiceAvailabilityChanged();
        }
    }

    /// <summary>Hook for derived VMs to call <c>NotifyCanExecuteChanged()</c> on their commands.</summary>
    protected virtual void OnServiceAvailabilityChanged() { }

    public virtual void Dispose() => Service.PropertyChanged -= OnServicePropertyChanged;
}
