using Microsoft.Diagnostics.Tracing;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Etw;
using StutterDiag.Monitors.Common;

namespace StutterDiag.Monitors.Etw;

/// <summary>
/// Emits a <see cref="EventCategory.DiskLatency"/> event for every physical I/O whose service
/// time exceeds a threshold, with the initiating process and file in
/// <see cref="MonitorEvent.Data"/>. Primary source is the kernel <c>DiskIO</c> stream; if the
/// user session has <c>Microsoft-Windows-StorPort</c> it is also consumed for port-driver
/// timing (which sees queueing the kernel layer hides).
/// </summary>
public sealed class EtwDiskIoMonitor : MonitorBase, IMonitor
{
    private const string StorPort = "Microsoft-Windows-StorPort";

    private readonly IEtwKernelStream _stream;
    private readonly EtwUserSession? _user;
    private readonly EtwTimebase _timebase;
    private readonly double _thresholdMs;
    private int _started;

    public EtwDiskIoMonitor(
        IEtwKernelStream stream,
        QpcClock clock,
        double latencyThresholdMs = 20.0,
        EtwUserSession? userSession = null,
        EtwTimebase? timebase = null)
        : base("Etw.DiskIo", clock)
    {
        _stream = stream;
        _user = userSession;
        _thresholdMs = latencyThresholdMs;
        _timebase = timebase ?? new EtwTimebase(clock);
    }

    public override Task StartAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return Task.CompletedTask;

        bool kernel = _stream.Health.IsUsable;
        bool storport = _user is { IsAvailable: true } && _user.IsEnabled(StorPort);

        if (kernel)
        {
            _stream.DiskIoObserved += OnKernelDiskIo;
            _stream.HealthChanged += OnStreamHealth;
        }
        if (storport)
            _user!.Session!.Source.Dynamic.All += OnDynamic;

        SetHealth(
            kernel ? MonitorHealth.Ok
            : storport ? MonitorHealth.Degraded("kernel DiskIO ETW unavailable; StorPort only")
            : MonitorHealth.Unavailable("disk I/O ETW unavailable (requires administrator)"));
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
            return Task.CompletedTask;

        _stream.DiskIoObserved -= OnKernelDiskIo;
        _stream.HealthChanged -= OnStreamHealth;
        if (_user?.Session is { } s)
            s.Source.Dynamic.All -= OnDynamic;
        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void OnStreamHealth(object? sender, MonitorHealth h)
    {
        if (!h.IsUsable && (_user is not { IsAvailable: true } || !_user.IsEnabled(StorPort)))
            SetHealth(MonitorHealth.Unavailable("kernel DiskIO ETW stopped"));
    }

    private void OnKernelDiskIo(object? sender, DiskIoObservation o)
    {
        if (o.LatencyMs < _thresholdMs)
            return;

        RaiseEvent(NewEvent(EventCategory.DiskLatency,
            $"{(o.IsWrite ? "write" : "read")} {o.LatencyMs:F1} ms on disk {o.DiskNumber}"
            + $" ({o.TransferBytes / 1024.0:F0} KiB){(o.FileName.Length > 0 ? $" {o.FileName}" : "")}",
            o.LatencyMs >= _thresholdMs * 5 ? EventSeverity.Warning : EventSeverity.Info,
            qpc: o.TimestampQpc,
            data: new Dictionary<string, string>
            {
                ["op"] = o.IsWrite ? "write" : "read",
                ["latencyMs"] = o.LatencyMs.ToString("F3"),
                ["bytes"] = o.TransferBytes.ToString(),
                ["disk"] = o.DiskNumber.ToString(),
                ["pid"] = o.ProcessId.ToString(),
                ["process"] = o.ProcessName,
                ["file"] = o.FileName,
            }));
    }

    // NOTE(build): StorPort request-timing event name / payload fields ("RequestDuration_us",
    // "MiniportName", "Lun") must be confirmed against the Microsoft-Windows-StorPort manifest.
    private void OnDynamic(TraceEvent data)
    {
        if (data.ProviderName != StorPort)
            return;

        double us = PayloadDouble(data, "RequestDuration_us", "IoDuration", "DurationUs");
        if (double.IsNaN(us))
            return;
        double ms = us / 1000.0;
        if (ms < _thresholdMs)
            return;

        RaiseEvent(NewEvent(EventCategory.DiskLatency,
            $"StorPort request {ms:F1} ms",
            ms >= _thresholdMs * 5 ? EventSeverity.Warning : EventSeverity.Info,
            qpc: _timebase.ToQpc(data),
            provider: StorPort,
            eventId: (int)data.ID,
            data: new Dictionary<string, string>
            {
                ["latencyMs"] = ms.ToString("F3"),
                ["miniport"] = data.PayloadByName("MiniportName")?.ToString() ?? "",
                ["event"] = data.EventName ?? "",
            }));
    }

    private static double PayloadDouble(TraceEvent data, params string[] names)
    {
        foreach (var n in names)
        {
            try
            {
                object? v = data.PayloadByName(n);
                if (v is null) continue;
                return v switch
                {
                    double d => d,
                    float f => f,
                    int i => i,
                    uint u => u,
                    long l => l,
                    ulong ul => ul,
                    _ => double.TryParse(v.ToString(), out var p) ? p : double.NaN,
                };
            }
            catch { /* not on this event */ }
        }
        return double.NaN;
    }
}
