using System.Windows.Threading;
using StutterDiag.Ipc;

namespace StutterDiag.Gui.Services;

/// <summary>
/// Periodically calls <see cref="ServiceConnection.GetStatusAsync"/> and raises
/// <see cref="StatusChanged"/> on the UI thread. Polls every 1&#160;s while the main window
/// is visible and every 5&#160;s when the app is only in the tray, to keep idle CPU low.
/// This is the app's single "polling service".
/// </summary>
public sealed class StatusPoller
{
    private static readonly TimeSpan ForegroundInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BackgroundInterval = TimeSpan.FromSeconds(5);

    private readonly ServiceConnection _service;
    private readonly DispatcherTimer _timer;
    private bool _inFlight;

    public StatusPoller(ServiceConnection service)
    {
        _service = service;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = ForegroundInterval
        };
        _timer.Tick += OnTick;
    }

    /// <summary>Last status seen, or null if the service has never answered.</summary>
    public StatusDto? Last { get; private set; }

    public event EventHandler<StatusDto?>? StatusChanged;

    public void Start()
    {
        if (!_timer.IsEnabled)
        {
            _timer.Start();
            // Kick an immediate poll rather than waiting a full interval.
            _ = PollOnceAsync();
        }
    }

    public void Stop() => _timer.Stop();

    /// <summary>Switch cadence based on whether the main window is on screen.</summary>
    public void SetForeground(bool foreground)
    {
        _timer.Interval = foreground ? ForegroundInterval : BackgroundInterval;
    }

    /// <summary>Force an out-of-band refresh (e.g. right after Start/Stop monitoring).</summary>
    public Task RefreshNowAsync() => PollOnceAsync();

    private async void OnTick(object? sender, EventArgs e) => await PollOnceAsync();

    private async Task PollOnceAsync()
    {
        if (_inFlight) return;
        _inFlight = true;
        try
        {
            var status = await _service.GetStatusAsync().ConfigureAwait(true);
            Last = status;
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception ex)
        {
            // ServiceConnection already swallows IPC failures; this is belt-and-braces so a
            // poll tick can never escape to the dispatcher.
            GuiLog.Error("Status poll failed", ex);
            StatusChanged?.Invoke(this, null);
        }
        finally
        {
            _inFlight = false;
        }
    }
}
