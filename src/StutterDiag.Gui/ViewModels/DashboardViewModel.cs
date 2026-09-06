using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StutterDiag.Gui.Models;
using StutterDiag.Gui.Services;
using StutterDiag.Ipc;

namespace StutterDiag.Gui.ViewModels;

/// <summary>
/// The at-a-glance page: monitoring state, elapsed time, the running counters, per-monitor
/// health, and the last observed event. Wording here is descriptive only — counts and the
/// DTO-supplied text, never causal claims (docs/ARCHITECTURE.md §1).
/// </summary>
public sealed partial class DashboardViewModel : ViewModelBase
{
    private readonly Action<string> _navigate;

    public DashboardViewModel(ServiceConnection service, Action<string> navigate) : base(service)
    {
        _navigate = navigate;
        Modes = new[] { "Standard", "Gaming" };
    }

    public IReadOnlyList<string> Modes { get; }

    [ObservableProperty]
    private string _selectedMode = "Standard";

    [ObservableProperty]
    private bool _monitoring;

    /// <summary>Label for the single START/STOP button.</summary>
    public string ToggleLabel => Monitoring ? "STOP" : "START";

    partial void OnMonitoringChanged(bool value) => OnPropertyChanged(nameof(ToggleLabel));

    [ObservableProperty]
    private string _statusGlyph = "○"; // ○

    [ObservableProperty]
    private string _statusText = "STOPPED";

    [ObservableProperty]
    private string _elapsedText = "00:00:00";

    [ObservableProperty]
    private int _stuttersDetected;

    [ObservableProperty]
    private int _majorStutters;

    [ObservableProperty]
    private int _tpmEvents;

    [ObservableProperty]
    private int _wheaEvents;

    [ObservableProperty]
    private int _dpcSpikes;

    [ObservableProperty]
    private string _lastEventText = "No events observed yet.";

    [ObservableProperty]
    private bool _busy;

    public ObservableCollection<MonitorHealthRow> Monitors { get; } = new();

    public ObservableCollection<string> Warnings { get; } = new();

    /// <summary>Automatic diagnostic — observation/hypothesis lines, refreshed when a new stutter lands.</summary>
    public ObservableCollection<FindingRow> Findings { get; } = new();

    private int _findingsAtStutterCount = -1;
    private bool _findingsBusy;

    /// <summary>Map the latest polled status onto the page. Null = service not reachable.</summary>
    public void ApplyStatus(StatusDto? s)
    {
        if (s is null)
        {
            Monitoring = false;
            StatusGlyph = "○";
            StatusText = "STOPPED";
            ElapsedText = "00:00:00";
            return;
        }

        Monitoring = s.Monitoring;
        StatusGlyph = s.Monitoring ? "●" : "○"; // ● / ○
        StatusText = s.Monitoring ? "MONITORING" : "STOPPED";
        var elapsed = TimeSpan.FromSeconds(Math.Max(0, s.MonitoringSeconds));
        ElapsedText = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        StuttersDetected = s.StuttersDetected;
        MajorStutters = s.MajorStutters;
        TpmEvents = s.TpmEvents;
        WheaEvents = s.WheaEvents;
        DpcSpikes = s.DpcSpikes;

        LastEventText = string.IsNullOrWhiteSpace(s.LastEventText)
            ? "No events observed yet."
            : $"{FormatStamp(s.LastEventUtcIso)} - {s.LastEventText}";

        SyncCollection(Monitors, s.Monitors.Select(MonitorHealthRow.From));
        SyncCollection(Warnings, s.Warnings);

        // Pull the automatic diagnostic only when the stutter count actually moved
        // (so the dashboard poll stays a single IPC call in the steady state).
        if (s.StuttersDetected != _findingsAtStutterCount)
        {
            _findingsAtStutterCount = s.StuttersDetected;
            _ = RefreshFindingsAsync();
        }

        ToggleMonitoringCommand.NotifyCanExecuteChanged();
    }

    private async Task RefreshFindingsAsync()
    {
        if (_findingsBusy) return;
        _findingsBusy = true;
        try
        {
            var findings = await Service.GetRecentFindingsAsync(10).ConfigureAwait(true);
            SyncCollection(Findings, findings.Select(FindingRow.From));
        }
        catch (OperationCanceledException) { /* app closing */ }
        finally
        {
            _findingsBusy = false;
        }
    }

    private bool CanToggle() => IsServiceAvailable && !Busy;

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task ToggleMonitoringAsync()
    {
        Busy = true;
        ToggleMonitoringCommand.NotifyCanExecuteChanged();
        try
        {
            _ = Monitoring
                ? await Service.StopMonitoringAsync().ConfigureAwait(true)
                : await Service.StartMonitoringAsync(SelectedMode).ConfigureAwait(true);
        }
        finally
        {
            Busy = false;
            ToggleMonitoringCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void ViewTimeline() => _navigate("Timeline");

    [RelayCommand]
    private void GenerateReport() => _navigate("Report");

    protected override void OnServiceAvailabilityChanged() => ToggleMonitoringCommand.NotifyCanExecuteChanged();

    private static string FormatStamp(string? iso)
    {
        if (!string.IsNullOrEmpty(iso) &&
            DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
        {
            return dt.ToLocalTime().ToString("HH:mm:ss");
        }
        return iso ?? "--:--:--";
    }

    private static void SyncCollection<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }
}
