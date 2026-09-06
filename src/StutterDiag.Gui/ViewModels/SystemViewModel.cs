using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StutterDiag.Gui.Models;
using StutterDiag.Gui.Services;

namespace StutterDiag.Gui.ViewModels;

/// <summary>
/// Read-only machine description. All values come straight from
/// <c>GetSystemInfoJsonAsync</c>; anything the service could not obtain reliably is shown
/// as "Unavailable" and styled grey (docs/ARCHITECTURE.md §12). The TPM type is labelled
/// "inferred" and its <c>InferenceBasis</c> is shown verbatim.
/// </summary>
public sealed partial class SystemViewModel : ViewModelBase
{
    public SystemViewModel(ServiceConnection service) : base(service)
    {
    }

    [ObservableProperty]
    private SystemInfoView _info = SystemInfoView.Empty;

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private bool _loadedOnce;

    private bool CanRefresh() => IsServiceAvailable && !Busy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        Busy = true;
        RefreshCommand.NotifyCanExecuteChanged();
        try
        {
            var json = await Service.GetSystemInfoJsonAsync().ConfigureAwait(true);
            Info = SystemInfoView.Parse(json);
            LoadedOnce = true;
        }
        finally
        {
            Busy = false;
            RefreshCommand.NotifyCanExecuteChanged();
        }
    }

    protected override void OnServiceAvailabilityChanged() => RefreshCommand.NotifyCanExecuteChanged();
}
