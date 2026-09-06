using Microsoft.Extensions.Logging;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Etw;

namespace StutterDiag.Service;

/// <summary>
/// Owns the transient "high-resolution" capture window opened around every aggregated stutter
/// (ARCHITECTURE §7). On <see cref="Trigger"/> it layers the heavy kernel ETW keywords
/// (context-switch / dispatcher / profile) on, flips the detectors that support a finer mode,
/// grabs a full process snapshot, and flushes the store so the window's data is durable. After
/// <see cref="HighResOptions.WindowSeconds"/> with no fresh trigger it reverts everything.
/// Overlapping triggers extend the window rather than stacking.
/// </summary>
public sealed class HighResWindowController : IDisposable
{
    private readonly QpcClock _clock;
    private readonly HighResOptions _opt;
    private readonly IEventStore _store;
    private readonly Func<ProcessSnapshot, Task> _persistSnapshot;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly Timer _revertTimer;

    private EtwKernelSession? _kernel;
    private IReadOnlyList<IStutterDetector> _detectors = Array.Empty<IStutterDetector>();
    private IProcessMonitor? _processMonitor;

    private bool _active;
    private long _windowEndsQpc;
    private bool _disposed;

    public HighResWindowController(
        QpcClock clock,
        HighResOptions options,
        IEventStore store,
        Func<ProcessSnapshot, Task> persistSnapshot,
        ILogger log)
    {
        _clock = clock;
        _opt = options;
        _store = store;
        _persistSnapshot = persistSnapshot;
        _log = log;
        _revertTimer = new Timer(_ => SafeRevertTick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Wire the live composition. Called by the orchestrator right after it builds the graph.</summary>
    public void Attach(EtwKernelSession kernel, IReadOnlyList<IStutterDetector> detectors, IProcessMonitor processMonitor)
    {
        _kernel = kernel;
        _detectors = detectors;
        _processMonitor = processMonitor;
    }

    /// <summary>Open (or extend) the high-res window for a detected stutter.</summary>
    public void Trigger(Stutter stutter) => Open(SnapshotTrigger.AutoStutter);

    /// <summary>Open (or extend) the window and force a snapshot — used by a user mark.</summary>
    public void ForceHighResWindow(SnapshotTrigger trigger) => Open(trigger);

    private void Open(SnapshotTrigger trigger)
    {
        if (_disposed) return;

        bool needEnable;
        lock (_gate)
        {
            _windowEndsQpc = _clock.GetTimestamp() + _clock.MsToTicks(_opt.WindowSeconds * 1000.0);
            needEnable = !_active;
            _active = true;
        }

        if (needEnable) EnableHighRes();

        // A fresh snapshot for every trigger, even when only extending an open window.
        CaptureSnapshot(trigger);

        // Re-arm the revert check just past the (possibly extended) window end.
        _revertTimer.Change(TimeSpan.FromSeconds(_opt.WindowSeconds + 1), Timeout.InfiniteTimeSpan);
    }

    private void EnableHighRes()
    {
        try
        {
            if (_kernel is not null)
            {
                var result = _kernel.EnableHighResolution();
                if (result == KeywordChangeResult.RestartRequired)
                {
                    _log.LogInformation("High-res keyword change requires a kernel ETW session restart; restarting");
                    _kernel.Restart();
                }
            }

            foreach (var d in _detectors)
            {
                if (!d.SupportsHighResolutionMode) continue;
                try { d.SetHighResolutionMode(true); }
                catch (Exception ex) { _log.LogDebug(ex, "detector {Name} rejected high-res mode", d.Name); }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "failed to enable the high-resolution ETW window");
        }
    }

    private void CaptureSnapshot(SnapshotTrigger trigger)
    {
        var pm = _processMonitor;
        if (pm is null) return;

        // Snapshot capture + persist + flush runs off the trigger thread (ETW worker / timer).
        _ = Task.Run(async () =>
        {
            try
            {
                var snap = pm.CaptureSnapshot(trigger);
                await _persistSnapshot(snap).ConfigureAwait(false);
                await _store.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "high-resolution process snapshot failed");
            }
        });
    }

    private void SafeRevertTick()
    {
        try { RevertTick(); }
        catch (Exception ex) { _log.LogWarning(ex, "high-res revert tick faulted"); }
    }

    private void RevertTick()
    {
        bool revert = false;
        lock (_gate)
        {
            if (!_active) return;

            long now = _clock.GetTimestamp();
            if (now < _windowEndsQpc)
            {
                // A later trigger pushed the end out; check again when it should actually end.
                double remainMs = _clock.TicksToMs(_windowEndsQpc - now) + 1000;
                _revertTimer.Change(TimeSpan.FromMilliseconds(remainMs), Timeout.InfiniteTimeSpan);
                return;
            }

            _active = false;
            revert = true;
        }

        if (revert) DisableHighRes();
    }

    private void DisableHighRes()
    {
        try
        {
            foreach (var d in _detectors)
            {
                if (!d.SupportsHighResolutionMode) continue;
                try { d.SetHighResolutionMode(false); } catch { /* best effort */ }
            }

            if (_kernel is not null)
            {
                var result = _kernel.DisableHighResolution();
                if (result == KeywordChangeResult.RestartRequired) _kernel.Restart();
            }

            _log.LogDebug("High-resolution window reverted");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "failed to revert the high-resolution ETW window");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _revertTimer.Dispose(); } catch { /* ignore */ }
        DisableHighRes();
    }
}
