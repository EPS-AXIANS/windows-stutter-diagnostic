using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StutterDiag.Gui.Services;
using StutterDiag.Ipc;

namespace StutterDiag.Gui.ViewModels;

/// <summary>
/// "Configuration A → measure, Configuration B → measure, compare." Picks two sessions and
/// drives a compare-mode HTML report (there is no dedicated A/B IPC call). The report itself
/// carries the "differences are not proof of causation" framing; this page repeats it.
/// </summary>
public sealed partial class CompareViewModel : ViewModelBase
{
    public const string Disclaimer =
        "Configuration A → measure, Configuration B → measure, compare. " +
        "Differences between the two runs are observations only — they are not proof of causation.";

    public CompareViewModel(ServiceConnection service) : base(service)
    {
    }

    public ObservableCollection<SessionDto> Sessions { get; } = new();

    [ObservableProperty]
    private SessionDto? _sessionA;

    [ObservableProperty]
    private SessionDto? _sessionB;

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string? _resultPath;

    private bool CanLoad() => IsServiceAvailable && !Busy;

    private bool CanCompare() =>
        IsServiceAvailable && !Busy &&
        SessionA is not null && SessionB is not null && SessionA.Id != SessionB.Id;

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadSessionsAsync()
    {
        Busy = true;
        RaiseCanExecute();
        try
        {
            var sessions = await Service.ListSessionsAsync().ConfigureAwait(true);
            Sessions.Clear();
            foreach (var s in sessions) Sessions.Add(s);
        }
        finally
        {
            Busy = false;
            RaiseCanExecute();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private async Task CompareAsync()
    {
        if (SessionA is null || SessionB is null) return;

        Busy = true;
        RaiseCanExecute();
        try
        {
            var outPath = Path.Combine(
                Shell.DefaultReportDirectory(),
                $"compare-{SessionA.Id}-vs-{SessionB.Id}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.html");

            var request = new ReportRequestDto
            {
                Format = "Html",
                OutputPath = outPath,
                SessionIds = new[] { SessionA.Id, SessionB.Id },
                CompareMode = true,
                IncludeProcessNames = true,
                IncludeUserNames = false,
                IncludeCommandLines = false,
                IncludeRawEventXml = true,
                IncludeFullEventLog = true
            };

            var produced = await Service.GenerateReportAsync(request).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(produced))
            {
                StatusText = "The service did not return a report path.";
                ResultPath = null;
            }
            else
            {
                ResultPath = produced;
                StatusText = $"Comparison report generated: {produced}";
                Shell.OpenPath(produced);
            }
        }
        finally
        {
            Busy = false;
            RaiseCanExecute();
        }
        OpenResultCommand.NotifyCanExecuteChanged();
    }

    private bool CanOpenResult() => !string.IsNullOrWhiteSpace(ResultPath);

    [RelayCommand(CanExecute = nameof(CanOpenResult))]
    private void OpenResult() => Shell.OpenPath(ResultPath);

    partial void OnSessionAChanged(SessionDto? value) => RaiseCanExecute();

    partial void OnSessionBChanged(SessionDto? value) => RaiseCanExecute();

    protected override void OnServiceAvailabilityChanged() => RaiseCanExecute();

    private void RaiseCanExecute()
    {
        LoadSessionsCommand.NotifyCanExecuteChanged();
        CompareCommand.NotifyCanExecuteChanged();
        OpenResultCommand.NotifyCanExecuteChanged();
    }
}
