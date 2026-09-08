using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Correlation;
using StutterDiag.Core.Model;
using StutterDiag.Core.Retention;
using StutterDiag.Core.Time;
using StutterDiag.Etw;
using StutterDiag.Monitors.Common;
using StutterDiag.Monitors.Cpu;
using StutterDiag.Monitors.Devices;
using StutterDiag.Monitors.Disk;
using StutterDiag.Monitors.Etw;
using StutterDiag.Monitors.EventLog;
using StutterDiag.Monitors.Frametime;
using StutterDiag.Monitors.Gpu;
using StutterDiag.Monitors.Machine;
using StutterDiag.Monitors.Memory;
using StutterDiag.Monitors.Process;
using StutterDiag.Monitors.Stutter;
using StutterDiag.Monitors.Tpm;
using StutterDiag.Monitors.Whea;
using StutterDiag.Service.Ipc;

namespace StutterDiag.Service;

/// <summary>
/// The heart of the service. Builds the whole collector graph (ETW sessions, every detector and
/// monitor), wires the runtime data flow of ARCHITECTURE §7, owns the session lifecycle and the
/// periodic timers, and exposes a small control surface for the IPC layer. One monitor failing
/// never stops the others: each <c>StartAsync</c> is isolated.
/// </summary>
public sealed class MonitorOrchestrator : IAsyncDisposable
{
    private readonly QpcClock _clock;
    private readonly AppConfig _config;
    private readonly IEventStore _store;
    private readonly ILogger<MonitorOrchestrator> _log;
    private readonly IReadOnlyList<string> _startupWarnings;

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly RetentionManager _retention;

    private readonly BoundedRecentEvents _recentEvents = new(512);
    private readonly object _recentStuttersGate = new();
    private readonly List<RecentStutterSnapshot> _recentStutters = new();
    private readonly object _knownStutterGate = new();
    private readonly List<long> _knownStutterQpcs = new();
    private readonly Random _rng = new();

    // ---- composition (null until a session is running) ----
    private RingBufferDataSource? _ring;
    private BaselineStats? _baseline;
    private BaseRateSampler? _baseRates;
    private StutterAggregator? _aggregator;
    private CorrelationEngine? _correlation;
    private HighResWindowController? _highRes;

    private ProviderDiscovery? _discovery;
    private EtwKernelSession? _kernelSession;
    private EtwUserSession? _userSession;
    private ModuleResolver? _moduleResolver;
    private EtwProcessing? _etwProcessing;
    private EtwSessionHealth? _etwHealth;
    private EtwUserFeedAdapter? _feed;

    private List<IMonitor> _monitors = new();
    private List<IStutterDetector> _detectors = new();
    private List<IDpcMonitor> _dpcMonitors = new();
    private TpmMonitor? _tpmMonitor;
    private ProcessMonitor? _processMonitor;
    private SystemInfoProvider? _systemInfo;

    // ---- timers ----
    private Timer? _flushTimer;
    private Timer? _evictTimer;
    private Timer? _baseRateTimer;
    private Timer? _reanchorTimer;
    private Timer? _retentionTimer;

    // ---- session state ----
    private CancellationTokenSource _sessionCts = new();
    private volatile bool _running;
    private long _sessionId;
    private DateTime? _startedUtc;
    private long _startedQpc;
    private string _mode = "Standard";
    private long _lastDpcPollQpc;

    private int _stutterCount;
    private int _majorStutters;
    private int _tpmEvents;
    private int _wheaEvents;
    private int _dpcSpikes;
    private MonitorEvent? _lastEvent;

    public MonitorOrchestrator(
        QpcClock clock,
        AppConfig config,
        IEventStore store,
        StartupWarnings startupWarnings,
        ILogger<MonitorOrchestrator> log)
    {
        _clock = clock;
        _config = config;
        _store = store;
        _startupWarnings = startupWarnings.Items;
        _log = log;
        _retention = new RetentionManager(store, config.Retention);
    }

    /// <summary>The persistence layer, for the report generator and history reads over IPC.</summary>
    public IEventStore Store => _store;

    /// <summary>Live configuration (read-only use by the IPC layer for window sizing etc.).</summary>
    public AppConfig Config => _config;

    /// <summary>Id of the running session, or 0 when monitoring is stopped.</summary>
    public long CurrentSessionId => _running ? _sessionId : 0;

    public bool IsRunning => _running;

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    /// <summary>One-shot startup work: enforce retention before any session begins.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        // Close sessions a previous crash left open BEFORE retention runs, otherwise they are
        // invisible to age-based pruning and can also stall size-based pruning (which only drops
        // finished sessions). Safe here: no session is running yet — StartAsync comes later.
        try
        {
            int closed = await _store.CloseOpenSessionsAsync(ct).ConfigureAwait(false);
            if (closed > 0)
                _log.LogInformation("Closed {Count} session(s) left open by a previous run", closed);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "closing previously-open sessions failed");
        }

        try
        {
            var result = await _retention.RunAsync(_clock.UtcNow, ct).ConfigureAwait(false);
            if (result.Total > 0)
                _log.LogInformation("Startup retention pruned {Count} session(s)", result.Total);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "startup retention pass failed");
        }
    }

    /// <summary>Begin a monitoring session in the given mode ("Standard" | "Gaming").</summary>
    public async Task StartAsync(string mode, CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_running) return;

            _mode = NormalizeMode(mode);
            try { _sessionCts.Dispose(); } catch { /* first run: fresh CTS */ }
            _sessionCts = new CancellationTokenSource();
            ResetCounters();

            Compose();

            var tpmInfo = SafeGetTpmInfo();
            var session = new MonitoringSession
            {
                StartedUtc = _clock.UtcNow,
                Label = $"{_mode} session",
                TpmTypeInferred = tpmInfo.InferredType,
                TpmBasis = tpmInfo.InferenceBasis,
                MachineFingerprint = SafeFingerprint(),
                ConfigJson = ConfigLoader.ToJson(_config),
                Mode = _mode,
            };
            _sessionId = await _store.StartSessionAsync(session, ct).ConfigureAwait(false);
            _startedUtc = session.StartedUtc;
            _startedQpc = _clock.GetTimestamp();
            _lastDpcPollQpc = _startedQpc;

            _ = PersistSystemInfoAsync(_sessionId);

            // --- start order: sessions -> monitors subscribe -> ETW pumps -> timers ---
            bool kernelOk = _kernelSession!.Start();
            bool userOk = _userSession!.Start();
            _log.LogInformation("ETW sessions: kernel={Kernel}, user={User} ({Providers} providers)",
                kernelOk ? "on" : _kernelSession.Health.ToString(),
                userOk ? "on" : _userSession.Health.ToString(),
                _userSession.EnabledProviders.Count);

            if (_kernelSession.Session is not null)
                _etwHealth!.Attach(_kernelSession.Session.Source, "kernel");
            if (_userSession.Session is not null)
                _etwHealth!.Attach(_userSession.Session.Source, "user");

            _feed!.Attach();

            foreach (var d in _detectors) await SafeStartAsync(d).ConfigureAwait(false);
            foreach (var m in _monitors) await SafeStartAsync(m).ConfigureAwait(false);

            await _etwProcessing!.StartAsync(_sessionCts.Token).ConfigureAwait(false);

            StartTimers();
            _running = true;

            await SafeRecordSessionEvent("Monitoring session started").ConfigureAwait(false);
            _log.LogInformation("Monitoring started: session {SessionId}, mode {Mode}", _sessionId, _mode);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>End the current session, flush and dispose the graph cleanly.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_running) return;
            _running = false;

            StopTimers();
            _sessionCts.Cancel();

            try { _aggregator?.Flush(_clock.GetTimestamp()); } catch { /* best effort */ }

            foreach (var d in _detectors) await SafeStopAsync(d).ConfigureAwait(false);
            foreach (var m in _monitors) await SafeStopAsync(m).ConfigureAwait(false);

            try { if (_etwProcessing is not null) await _etwProcessing.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "ETW processing stop"); }

            try { _highRes?.Dispose(); } catch { /* ignore */ }

            try { _kernelSession?.Stop(); _kernelSession?.Dispose(); } catch { /* ignore */ }
            try { _userSession?.Stop(); _userSession?.Dispose(); } catch { /* ignore */ }
            try { if (_etwProcessing is not null) await _etwProcessing.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }

            try { await _store.FlushAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "final store flush failed"); }

            if (_sessionId != 0)
            {
                try { await _store.EndSessionAsync(_sessionId, _clock.UtcNow, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _log.LogWarning(ex, "EndSessionAsync failed"); }
            }

            foreach (var d in _detectors) await SafeDisposeAsync(d).ConfigureAwait(false);
            foreach (var m in _monitors) await SafeDisposeAsync(m).ConfigureAwait(false);

            _sessionId = 0;
            _startedUtc = null;
            _startedQpc = 0;
            _log.LogInformation("Monitoring stopped");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); } catch { /* ignore */ }
        _lifecycleLock.Dispose();
        _sessionCts.Dispose();
    }

    // ------------------------------------------------------------------
    // Composition
    // ------------------------------------------------------------------

    private void Compose()
    {
        _ring = new RingBufferDataSource(_config.HighRes, _clock.Frequency);
        _baseline = new BaselineStats(_config.Correlation, _clock.Frequency);
        _baseRates = new BaseRateSampler(_clock, _ring, _config.Correlation);
        _aggregator = new StutterAggregator(_clock, _config.StutterThresholds);
        _aggregator.StutterAggregated += OnStutterAggregated;
        _correlation = new CorrelationEngine(_clock, _config.Correlation);

        _discovery = new ProviderDiscovery();
        _discovery.Refresh();
        _kernelSession = new EtwKernelSession(_config.Etw);
        _userSession = new EtwUserSession(_config.Etw, _discovery);
        _moduleResolver = new ModuleResolver();
        _etwProcessing = new EtwProcessing(_kernelSession, _moduleResolver, _clock, _userSession);
        _etwHealth = new EtwSessionHealth(_clock);
        _etwHealth.EventCaptured += OnMonitorEvent;
        _feed = new EtwUserFeedAdapter(_userSession, _etwProcessing.Timebase, _clock);

        // --- detectors ---
        var heartbeat = new HeartbeatStutterDetector(_config.Heartbeat, _config.StutterThresholds, _clock);
        var dpcAccum = new DpcIsrAccumulationDetector(_etwProcessing, _clock);
        var readyThread = new ReadyThreadLatencyDetector(_etwProcessing, _clock);
        var gpuGap = new GpuPacketGapDetector(_userSession, _clock, _etwProcessing.Timebase);
        var frametime = new FrametimeMonitor(_userSession, _clock, _config.StutterThresholds, _etwProcessing.Timebase);
        _detectors = new List<IStutterDetector> { heartbeat, dpcAccum, readyThread, gpuGap, frametime };

        // --- ETW-fed monitors ---
        var dpcMon = new DpcIsrMonitor(_etwProcessing, _clock);
        var diskEtw = new EtwDiskIoMonitor(_etwProcessing, _clock, 20.0, _userSession, _etwProcessing.Timebase);
        var hardFault = new EtwHardFaultMonitor(_etwProcessing, _clock);

        // --- domain monitors ---
        _tpmMonitor = new TpmMonitor(_clock, _feed);
        var cpu = new CpuMonitor(_config, _clock);
        var power = new CpuPowerStateMonitor(_feed, _clock);
        var gpu = new GpuMonitor(_config, _clock, BuildGpuVendorProviders());
        var gpuDriver = new GpuDriverEventMonitor(_clock, _feed);
        var disk = new DiskMonitor(_config, _clock);
        var mem = new MemoryMonitor(_config, _clock);
        _processMonitor = new ProcessMonitor(_config, _clock);
        var device = new DeviceMonitor(_clock, _feed);
        var eventLog = new EventLogMonitor(_config, _clock);
        var whea = new WheaMonitor(_clock, _feed);

        _monitors = new List<IMonitor>
        {
            dpcMon, diskEtw, hardFault,
            _tpmMonitor, cpu, power, gpu, gpuDriver, disk, mem, _processMonitor, device, eventLog, whea,
        };
        _dpcMonitors = new List<IDpcMonitor> { dpcMon };
        _systemInfo = new SystemInfoProvider(_clock, _tpmMonitor);

        // Every logical topic; each monitor filters internally. Cheap (providers are already enabled).
        foreach (var topic in Enum.GetValues<EtwFeedTopic>()) _feed.Subscribe(topic);

        _highRes = new HighResWindowController(_clock, _config.HighRes, _store, PersistSnapshotAsync, _log);
        _highRes.Attach(_kernelSession, _detectors, _processMonitor);
        // _kernelSession / _processMonitor are freshly assigned above; the null-forgiving reads
        // in StartAsync (_kernelSession!, _userSession!, _feed!, _etwProcessing!) are safe for the
        // same reason — Compose() always runs first inside the lifecycle lock.

        WireGraph();
    }

    /// <summary>Wire ARCHITECTURE §7: events/samples/DPC → store + ring + baseline; candidates → aggregator.</summary>
    private void WireGraph()
    {
        foreach (var m in AllCollectors())
        {
            m.EventCaptured += OnMonitorEvent;
            var captured = m;
            m.HealthChanged += (_, h) => OnMonitorHealthChanged(captured.Name, h);
        }

        foreach (var ms in AllCollectors().OfType<IMetricSource>())
            ms.SampleCaptured += OnSampleCaptured;

        foreach (var d in _detectors)
            d.StutterDetected += (_, c) => _aggregator!.Add(c);

        if (_processMonitor is not null)
            _processMonitor.SnapshotCaptured += (_, snap) => _ = PersistSnapshotAsync(snap);
    }

    private IEnumerable<IMonitor> AllCollectors()
    {
        foreach (var m in _monitors) yield return m;
        foreach (var d in _detectors) yield return d;
    }

    private IReadOnlyList<IGpuVendorProvider> BuildGpuVendorProviders()
    {
        var list = new List<IGpuVendorProvider>();
        if (!OperatingSystem.IsWindows()) return list;
        var sdk = _config.GpuVendorSdks;
        if (sdk.Nvml) list.Add(new NvmlGpuVendorProvider());
        if (sdk.Adlx) list.Add(new AdlxGpuVendorProvider());
        if (sdk.Igcl) list.Add(new IgclGpuVendorProvider());
        return list;
    }

    // ------------------------------------------------------------------
    // Runtime data flow
    // ------------------------------------------------------------------

    private void OnMonitorEvent(object? sender, MonitorEvent e)
    {
        Fire(_store.AppendEventAsync(e), "AppendEventAsync");
        _ring?.Add(e);
        _recentEvents.Add(e);
        _lastEvent = e;

        switch (e.Category)
        {
            case EventCategory.Tpm or EventCategory.Tbs:
                Interlocked.Increment(ref _tpmEvents);
                break;
            case EventCategory.Whea or EventCategory.HardwareError:
                Interlocked.Increment(ref _wheaEvents);
                break;
        }
    }

    private void OnSampleCaptured(object? sender, MetricSample s)
    {
        Fire(_store.AppendSampleAsync(s), "AppendSampleAsync");
        _ring?.Add(s);
        _baseline?.Observe(s.Metric, s.Value, s.TimestampQpc);
    }

    private void OnMonitorHealthChanged(string monitor, MonitorHealth health)
    {
        long sid = _sessionId;
        if (sid == 0) return;
        _ = Task.Run(async () =>
        {
            try { await _store.RecordHealthAsync(sid, monitor, health, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "RecordHealthAsync failed for {Monitor}", monitor); }
        });
    }

    private async void OnStutterAggregated(object? sender, Stutter stutter)
    {
        try
        {
            var withSession = stutter with { SessionId = _sessionId };
            long id = await _store.AddStutterReturningIdAsync(withSession, _sessionCts.Token).ConfigureAwait(false);
            var persisted = withSession with { Id = id };

            Interlocked.Increment(ref _stutterCount);
            if (persisted.Severity >= StutterSeverity.Major) Interlocked.Increment(ref _majorStutters);
            RememberStutter(persisted);

            _highRes?.Trigger(persisted);
            _ = AnalyzeAfterPostRollAsync(persisted);
        }
        catch (OperationCanceledException) { /* session ending */ }
        catch (Exception ex)
        {
            _log.LogError(ex, "failed to persist / trigger for an aggregated stutter");
        }
    }

    private async Task AnalyzeAfterPostRollAsync(Stutter stutter)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_config.Correlation.PostRollSeconds), _sessionCts.Token)
                .ConfigureAwait(false);

            var window = _correlation!.Analyze(stutter, _ring!, _baseline, _baseRates);
            if (window.Correlations.Count > 0)
                await _store.AddCorrelationsAsync(stutter.Id, window.Correlations, _sessionCts.Token).ConfigureAwait(false);

            UpdateRecentStutterCorrelation(stutter.Id, window.Correlations);
        }
        catch (OperationCanceledException) { /* session ending */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "correlation analysis failed for stutter {Id}", stutter.Id);
        }
    }

    // ------------------------------------------------------------------
    // Timers
    // ------------------------------------------------------------------

    private void StartTimers()
    {
        var flush = TimeSpan.FromSeconds(Math.Max(0.25, _config.Etw.FlushSeconds));
        _flushTimer = new Timer(_ => SafeTimer(FlushTick), null, flush, flush);
        _evictTimer = new Timer(_ => SafeTimer(EvictTick), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        _baseRateTimer = new Timer(_ => SafeTimer(BaseRateTick), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        _reanchorTimer = new Timer(_ => SafeTimer(() => _clock.Reanchor()), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        _retentionTimer = new Timer(_ => SafeTimer(RetentionTick), null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));
    }

    private void StopTimers()
    {
        _flushTimer?.Dispose();
        _evictTimer?.Dispose();
        _baseRateTimer?.Dispose();
        _reanchorTimer?.Dispose();
        _retentionTimer?.Dispose();
        _flushTimer = _evictTimer = _baseRateTimer = _reanchorTimer = _retentionTimer = null;
    }

    private void FlushTick()
    {
        long now = _clock.GetTimestamp();
        try { _etwHealth?.Poll(); } catch (Exception ex) { _log.LogDebug(ex, "ETW health poll"); }
        try { _aggregator?.Flush(now); } catch (Exception ex) { _log.LogDebug(ex, "aggregator flush"); }
        PollDpcMonitors(now);
    }

    private void EvictTick() => _ring?.Evict(_clock.GetTimestamp());

    private void PollDpcMonitors(long now)
    {
        long from = _lastDpcPollQpc == 0 ? now - _clock.MsToTicks(1000) : _lastDpcPollQpc;
        _lastDpcPollQpc = now;
        foreach (var dpc in _dpcMonitors)
        {
            IReadOnlyList<DpcIsrStat> stats;
            try { stats = dpc.GetStats(from, now); }
            catch (Exception ex) { _log.LogDebug(ex, "GetStats on {Name}", dpc.Name); continue; }

            foreach (var stat in stats)
            {
                Fire(_store.AppendDpcIsrStatAsync(stat), "AppendDpcIsrStatAsync");
                _ring?.Add(stat);
                if (stat.MaxMs >= 2.0) Interlocked.Increment(ref _dpcSpikes);
            }
        }
    }

    private void BaseRateTick()
    {
        if (_baseRates is null) return;
        long now = _clock.GetTimestamp();
        long windowTicks = _clock.MsToTicks(_config.Correlation.BaseRateWindowSeconds * 1000.0);
        long postRollTicks = _clock.MsToTicks(_config.Correlation.PostRollSeconds * 1000.0);
        long maxOffset = _clock.MsToTicks(_config.HighRes.RingBufferSeconds * 1000.0) - windowTicks;
        if (maxOffset <= windowTicks) return;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            long offset = windowTicks + (long)(_rng.NextDouble() * (maxOffset - windowTicks));
            long centre = now - offset;
            if (IsNearKnownStutter(centre, postRollTicks + windowTicks)) continue;
            _baseRates.Sample(now, offset);
            return;
        }
    }

    private void RetentionTick()
    {
        _ = Task.Run(async () =>
        {
            try { await _retention.RunAsync(_clock.UtcNow, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "scheduled retention pass failed"); }
        });
    }

    private void SafeTimer(Action body)
    {
        try { body(); }
        catch (Exception ex) { _log.LogWarning(ex, "orchestrator timer callback faulted"); }
    }

    // ------------------------------------------------------------------
    // Control surface (consumed by StutterDiagControl)
    // ------------------------------------------------------------------

    /// <summary>Immutable status view for the IPC <c>GetStatus</c>.</summary>
    public StatusSnapshot GetStatus()
    {
        var health = new List<MonitorHealthSnapshot>();
        foreach (var m in _monitors) health.Add(new MonitorHealthSnapshot(m.Name, m.Health.Status, m.Health.Note));
        foreach (var d in _detectors) health.Add(new MonitorHealthSnapshot(d.Name, d.Health.Status, d.Health.Note));
        if (_kernelSession is not null) health.Add(new MonitorHealthSnapshot("Etw.Kernel", _kernelSession.Health.Status, _kernelSession.Health.Note));
        if (_userSession is not null) health.Add(new MonitorHealthSnapshot("Etw.User", _userSession.Health.Status, _userSession.Health.Note));

        var warnings = new List<string>(_startupWarnings);
        if (_running && _kernelSession is { IsAvailable: false })
            warnings.Add("Kernel ETW unavailable (requires administrator)");
        if (_running && _userSession is { IsAvailable: false })
            warnings.Add("User-mode ETW unavailable (requires administrator)");
        if (_running && _etwProcessing is { DroppedRecordCount: > 0 })
            warnings.Add($"{_etwProcessing.DroppedRecordCount} ETW record(s) dropped (buffer pressure)");

        double seconds = _startedQpc == 0 ? 0 : _clock.TicksToMs(_clock.GetTimestamp() - _startedQpc) / 1000.0;

        return new StatusSnapshot
        {
            Monitoring = _running,
            SessionId = _sessionId,
            StartedUtc = _startedUtc,
            MonitoringSeconds = seconds,
            Mode = _mode,
            StuttersDetected = Volatile.Read(ref _stutterCount),
            MajorStutters = Volatile.Read(ref _majorStutters),
            TpmEvents = Volatile.Read(ref _tpmEvents),
            WheaEvents = Volatile.Read(ref _wheaEvents),
            DpcSpikes = Volatile.Read(ref _dpcSpikes),
            LastEventUtc = _lastEvent?.TimestampUtc,
            LastEventText = _lastEvent?.Message,
            IsElevated = IsProcessElevated(),
            Monitors = health,
            Warnings = warnings,
        };
    }

    public IReadOnlyList<MonitorEvent> GetRecentEvents(int count) => _recentEvents.Latest(count);

    public IReadOnlyList<RecentStutterSnapshot> GetRecentStutters(int count)
    {
        lock (_recentStuttersGate)
        {
            int take = Math.Clamp(count, 0, _recentStutters.Count);
            return _recentStutters
                .Skip(_recentStutters.Count - take)
                .Reverse()
                .Select(s => s with { })
                .ToList();
        }
    }

    /// <summary>Machine description for the System page / report header.</summary>
    public SystemInfo CollectSystemInfo()
        => (_systemInfo ?? new SystemInfoProvider(_clock)).Collect();

    /// <summary>Apply a new <c>AppConfig</c> JSON to the live instance; returns validation warnings.</summary>
    public IReadOnlyList<string> ApplyConfigJson(string configJson)
    {
        AppConfig incoming;
        try { incoming = ConfigLoader.FromJson(configJson); }
        catch (Exception ex) { return new[] { $"Configuration rejected: {ex.Message}" }; }

        var warnings = new List<string>(ConfigValidator.Validate(incoming));

        // Hot-swap every section except Storage (open DB / log file must not move mid-run).
        _config.StutterThresholds = incoming.StutterThresholds;
        _config.Correlation = incoming.Correlation;
        _config.Heartbeat = incoming.Heartbeat;
        _config.HighRes = incoming.HighRes;
        _config.Etw = incoming.Etw;
        _config.Sampling = incoming.Sampling;
        _config.Retention = incoming.Retention;
        _config.GamingMode = incoming.GamingMode;
        _config.GpuVendorSdks = incoming.GpuVendorSdks;
        _config.Privacy = incoming.Privacy;
        _config.Service = incoming.Service;

        if (_running)
            warnings.Add("Some settings take effect only on the next monitoring session.");
        return warnings;
    }

    public string GetConfigJson() => ConfigLoader.ToJson(_config);

    /// <summary>Synthesize a user-marked stutter at "now" and widen high-res capture around it.</summary>
    public UserMarkOutcome MarkUserStutter(string? note)
    {
        long qpc = _clock.GetTimestamp();
        DateTime utc = _clock.QpcToUtc(qpc);

        if (!_running || _aggregator is null)
            return new UserMarkOutcome(false, utc, "Monitoring is not running; the mark was not recorded.");

        _aggregator.Add(new StutterCandidate(
            qpc, _config.StutterThresholds.MicroStutterMs, DetectorKind.UserMarked, DetectionConfidence.High, note));

        OnMonitorEvent(this, new MonitorEvent
        {
            TimestampQpc = qpc,
            TimestampUtc = utc,
            Category = EventCategory.UserMark,
            Source = "UserMark",
            Severity = EventSeverity.Info,
            Message = string.IsNullOrWhiteSpace(note) ? "User-marked stutter" : $"User-marked stutter: {note}",
        });

        _highRes?.ForceHighResWindow(SnapshotTrigger.UserMark);
        return new UserMarkOutcome(true, utc, "Mark recorded; high-resolution capture widened.");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private void RememberStutter(Stutter s)
    {
        lock (_recentStuttersGate)
        {
            _recentStutters.Add(new RecentStutterSnapshot
            {
                Id = s.Id,
                TimestampUtc = s.TimestampUtc,
                DurationMs = s.DurationMs,
                Severity = s.Severity,
                Detector = s.Detector,
                UserMarked = s.UserMarked,
                CorrelationCount = 0,
                TopCorrelation = null,
            });
            if (_recentStutters.Count > 256) _recentStutters.RemoveRange(0, _recentStutters.Count - 256);
        }
        lock (_knownStutterGate)
        {
            _knownStutterQpcs.Add(s.TimestampQpc);
            if (_knownStutterQpcs.Count > 1024) _knownStutterQpcs.RemoveRange(0, _knownStutterQpcs.Count - 1024);
        }
    }

    private void UpdateRecentStutterCorrelation(long stutterId, IReadOnlyList<StutterCorrelation> correlations)
    {
        lock (_recentStuttersGate)
        {
            var row = _recentStutters.FirstOrDefault(r => r.Id == stutterId);
            if (row is null) return;
            row.CorrelationCount = correlations.Count;
            row.TopCorrelation = correlations.Count == 0
                ? null
                : SignalCatalog.DisplayName(correlations[0].SignalType);
        }
    }

    private bool IsNearKnownStutter(long centreQpc, long guardTicks)
    {
        lock (_knownStutterGate)
        {
            foreach (var q in _knownStutterQpcs)
                if (Math.Abs(q - centreQpc) <= guardTicks) return true;
        }
        return false;
    }

    private TpmInfo SafeGetTpmInfo()
    {
        try { return _tpmMonitor?.GetTpmInfo() ?? new TpmInfo(); }
        catch (Exception ex) { _log.LogDebug(ex, "GetTpmInfo failed at session start"); return new TpmInfo(); }
    }

    private string SafeFingerprint()
    {
        try { return _systemInfo?.ComputeMachineFingerprint() ?? ""; }
        catch (Exception ex) { _log.LogDebug(ex, "ComputeMachineFingerprint failed"); return ""; }
    }

    private async Task PersistSystemInfoAsync(long sessionId)
    {
        try
        {
            var info = (_systemInfo ?? new SystemInfoProvider(_clock)).Collect();
            await _store.SaveSystemInfoAsync(sessionId, info, CancellationToken.None).ConfigureAwait(false);
            if (info.Drivers.Count > 0)
                await _store.SaveDriversAsync(sessionId, info.Drivers, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "failed to persist system info for session {SessionId}", sessionId);
        }
    }

    private async Task PersistSnapshotAsync(ProcessSnapshot snapshot)
    {
        try
        {
            await _store.AddProcessSnapshotAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
            _ring?.Add(snapshot);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "failed to persist a process snapshot");
        }
    }

    private async Task SafeRecordSessionEvent(string message)
    {
        try
        {
            long qpc = _clock.GetTimestamp();
            await _store.AppendEventAsync(new MonitorEvent
            {
                TimestampQpc = qpc,
                TimestampUtc = _clock.QpcToUtc(qpc),
                Category = EventCategory.SessionLifecycle,
                Source = "Orchestrator",
                Severity = EventSeverity.Info,
                Message = message,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) { _log.LogDebug(ex, "session lifecycle event append failed"); }
    }

    private async Task SafeStartAsync(IMonitor m)
    {
        try
        {
            await m.StartAsync(_sessionCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "monitor {Name} failed to start; continuing without it", m.Name);
            if (_sessionId != 0)
            {
                try
                {
                    await _store.RecordHealthAsync(_sessionId, m.Name,
                        MonitorHealth.Failed($"StartAsync threw: {ex.Message}"), CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* ignore */ }
            }
        }
    }

    private async Task SafeStopAsync(IMonitor m)
    {
        try { await m.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "monitor {Name} StopAsync threw", m.Name); }
    }

    private async Task SafeDisposeAsync(IMonitor m)
    {
        try { await m.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "monitor {Name} DisposeAsync threw", m.Name); }
    }

    private void ResetCounters()
    {
        Interlocked.Exchange(ref _stutterCount, 0);
        Interlocked.Exchange(ref _majorStutters, 0);
        Interlocked.Exchange(ref _tpmEvents, 0);
        Interlocked.Exchange(ref _wheaEvents, 0);
        Interlocked.Exchange(ref _dpcSpikes, 0);
        _lastEvent = null;
        lock (_recentStuttersGate) _recentStutters.Clear();
        lock (_knownStutterGate) _knownStutterQpcs.Clear();
        _recentEvents.Clear();
    }

    private static string NormalizeMode(string? mode)
        => string.Equals(mode, "Gaming", StringComparison.OrdinalIgnoreCase) ? "Gaming" : "Standard";

    private void Fire(ValueTask task, string label)
    {
        if (task.IsCompletedSuccessfully) return;
        _ = Awaited(task, label);

        async Task Awaited(ValueTask t, string what)
        {
            try { await t.ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "{Op} faulted", what); }
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsProcessElevated()
    {
        try { return OperatingSystem.IsWindows() && IsWindowsElevated(); }
        catch { return false; }
    }
}

/// <summary>Outcome of <see cref="MonitorOrchestrator.MarkUserStutter"/>.</summary>
public sealed record UserMarkOutcome(bool Accepted, DateTime TimestampUtc, string Message);

/// <summary>Tiny fixed-size, thread-safe most-recent-events buffer for the IPC "recent events" call.</summary>
internal sealed class BoundedRecentEvents
{
    private readonly object _gate = new();
    private readonly MonitorEvent[] _items;
    private int _head;
    private int _count;

    public BoundedRecentEvents(int capacity) => _items = new MonitorEvent[capacity];

    public void Add(MonitorEvent e)
    {
        lock (_gate)
        {
            int tail = (_head + _count) % _items.Length;
            if (_count == _items.Length) { _items[tail] = e; _head = (_head + 1) % _items.Length; }
            else { _items[tail] = e; _count++; }
        }
    }

    public IReadOnlyList<MonitorEvent> Latest(int count)
    {
        lock (_gate)
        {
            int take = Math.Clamp(count, 0, _count);
            var result = new List<MonitorEvent>(take);
            for (int i = 0; i < take; i++)
                result.Add(_items[(_head + _count - 1 - i) % _items.Length]); // newest first
            return result;
        }
    }

    public void Clear()
    {
        lock (_gate) { Array.Clear(_items); _head = 0; _count = 0; }
    }
}
