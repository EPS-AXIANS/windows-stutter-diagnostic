using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StutterDiag.Gui.Models;
using StutterDiag.Gui.Services;
using StutterDiag.Ipc;

namespace StutterDiag.Gui.ViewModels;

/// <summary>
/// Session picker plus a custom-drawn timeline (see <c>Controls/TimelineCanvas</c>).
/// Marks are placed on a seconds axis relative to the session start. Selecting a stutter
/// shows the little window-detail panel; the jump commands ask the canvas to recentre.
/// </summary>
public sealed partial class TimelineViewModel : ViewModelBase
{
    public TimelineViewModel(ServiceConnection service) : base(service)
    {
    }

    public ObservableCollection<SessionDto> Sessions { get; } = new();

    public ObservableCollection<TimelineItem> TimelineItems { get; } = new();

    [ObservableProperty]
    private SessionDto? _selectedSession;

    [ObservableProperty]
    private double _totalSeconds = 60;

    [ObservableProperty]
    private long _selectedStutterId = -1;

    [ObservableProperty]
    private string _selectedStutterDetail = "Select a mark on the timeline to see its detail.";

    /// <summary>Full detail window for the selected stutter mark (null for non-stutter marks).</summary>
    [ObservableProperty]
    private StutterWindowDto? _selectedWindow;

    [ObservableProperty]
    private bool _busy;

    /// <summary>Raised by the jump commands. Payload: +1 = next stutter, -1 = previous.</summary>
    public event EventHandler<int>? JumpRequested;

    private bool CanLoad() => IsServiceAvailable && !Busy;

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
            SelectedSession ??= Sessions.FirstOrDefault();
        }
        finally
        {
            Busy = false;
            RaiseCanExecute();
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadTimelineAsync()
    {
        if (SelectedSession is null) return;

        Busy = true;
        RaiseCanExecute();
        try
        {
            var start = ParseUtc(SelectedSession.StartedUtcIso) ?? DateTime.UtcNow.AddHours(-1);
            var end = ParseUtc(SelectedSession.EndedUtcIso) ?? DateTime.UtcNow;
            if (end <= start) end = start.AddMinutes(1);

            var entries = await Service
                .GetTimelineAsync(SelectedSession.Id, start.ToString("o"), end.ToString("o"))
                .ConfigureAwait(true);

            TimelineItems.Clear();
            long synthetic = 0;
            foreach (var e in entries.OrderBy(e => e.TimestampUtcIso, StringComparer.Ordinal))
            {
                var ts = ParseUtc(e.TimestampUtcIso) ?? start;
                TimelineItems.Add(new TimelineItem(
                    synthetic++,
                    ts,
                    (ts - start).TotalSeconds,
                    e.Kind ?? "Other",
                    e.Label ?? "",
                    e.DurationMs,
                    e.StutterId));
            }

            TotalSeconds = Math.Max(60, (end - start).TotalSeconds);
            SelectedStutterId = -1;
            SelectedStutterDetail = TimelineItems.Count == 0
                ? "No entries in this session's timeline."
                : "Select a mark on the timeline to see its detail.";
        }
        finally
        {
            Busy = false;
            RaiseCanExecute();
        }
    }

    [RelayCommand]
    private void JumpNextStutter() => JumpRequested?.Invoke(this, +1);

    [RelayCommand]
    private void JumpPrevStutter() => JumpRequested?.Invoke(this, -1);

    partial void OnSelectedSessionChanged(SessionDto? value)
    {
        if (value is not null && LoadTimelineCommand.CanExecute(null))
            LoadTimelineCommand.Execute(null);
    }

    partial void OnSelectedStutterIdChanged(long value)
    {
        var item = TimelineItems.FirstOrDefault(i => i.SyntheticId == value);
        SelectedStutterDetail = item is null
            ? "Select a mark on the timeline to see its detail."
            : $"{item.Kind} · {item.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}"
              + (item.DurationMs is { } d ? $" · {d:0.#} ms" : string.Empty)
              + (string.IsNullOrWhiteSpace(item.Label) ? string.Empty : $"\n{item.Label}");

        SelectedWindow = null;
        if (item?.StutterId is { } sid && SelectedSession is { } session)
            _ = LoadStutterWindowAsync(session.Id, sid);
    }

    private async Task LoadStutterWindowAsync(long sessionId, long stutterId)
    {
        try
        {
            var window = await Service.GetStutterWindowAsync(sessionId, stutterId).ConfigureAwait(true);
            // Ignore a stale response if the selection moved on while we awaited.
            var current = TimelineItems.FirstOrDefault(i => i.SyntheticId == SelectedStutterId);
            if (current?.StutterId == stutterId) SelectedWindow = window;
        }
        catch (OperationCanceledException) { /* view closing */ }
    }

    protected override void OnServiceAvailabilityChanged() => RaiseCanExecute();

    private void RaiseCanExecute()
    {
        LoadSessionsCommand.NotifyCanExecuteChanged();
        LoadTimelineCommand.NotifyCanExecuteChanged();
    }

    private static DateTime? ParseUtc(string? iso) =>
        !string.IsNullOrEmpty(iso) &&
        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToUniversalTime()
            : null;
}
