using Microsoft.Diagnostics.Tracing;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Etw;
using StutterDiag.Monitors.Common;

namespace StutterDiag.Monitors.Stutter;

/// <summary>
/// Detects a visible hitch on the GPU side: a gap in packet execution on the graphics (3D)
/// engine while work is still queued for it. The GPU scheduler had something to run and did
/// not run it — a TDR-free stall that still drops a frame.
/// </summary>
/// <remarks>
/// Reads <c>Microsoft-Windows-DxgKrnl</c> scheduler packets from the user session directly.
/// This is the PresentMon technique (observation only, no hooks) and it needs a realtime ETW
/// session (admin) plus DxgKrnl present. When DxgKrnl is not enabled the detector reports
/// <see cref="HealthStatus.Unavailable"/>.
///
/// NOTE(build): the DxgKrnl scheduler event names / opcodes / payload field names used below
/// ("DmaPacket", "QueuePacket", node/engine ordinal) must be verified against the installed
/// dxgkrnl manifest and PresentMon's PresentMonTraceConsumer. They vary by Windows build.
/// </remarks>
public sealed class GpuPacketGapDetector : MonitorBase, IStutterDetector
{
    private const string DxgKrnl = "Microsoft-Windows-DxgKrnl";
    private const int GraphicsEngineOrdinal = 0;   // node 0 is conventionally the 3D engine

    private readonly EtwUserSession _user;
    private readonly EtwTimebase _timebase;
    private readonly double _gapThresholdMs;

    private long _lastRunEndQpc;
    private int _queueDepth;
    private int _started;

    public GpuPacketGapDetector(
        EtwUserSession userSession,
        QpcClock clock,
        EtwTimebase? timebase = null,
        double gapThresholdMs = 12.0)
        : base("Stutter.GpuPacketGap", clock)
    {
        _user = userSession;
        // Pass the shared EtwProcessing.Timebase for cross-source alignment; a private one still
        // works (it self-anchors) with at most a fixed sub-ms offset.
        _timebase = timebase ?? new EtwTimebase(clock);
        _gapThresholdMs = gapThresholdMs;
    }

    public DetectorKind Kind => DetectorKind.GpuPacketGap;
    public bool SupportsHighResolutionMode => false;
    public void SetHighResolutionMode(bool enabled) { /* independent of the kernel high-res window */ }

    public event EventHandler<StutterCandidate>? StutterDetected;

    public override Task StartAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return Task.CompletedTask;

        if (!_user.IsAvailable || !_user.IsEnabled(DxgKrnl))
        {
            SetHealth(MonitorHealth.Unavailable("GPU packet-gap needs DxgKrnl ETW"));
            return Task.CompletedTask;
        }

        _user.Session!.Source.Dynamic.All += OnDynamic;
        SetHealth(MonitorHealth.Ok);
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
            return Task.CompletedTask;
        if (_user.Session is { } s)
            s.Source.Dynamic.All -= OnDynamic;
        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void OnDynamic(TraceEvent data)
    {
        if (data.ProviderName != DxgKrnl)
            return;

        var name = data.EventName ?? string.Empty;
        bool isDma = name.IndexOf("DmaPacket", StringComparison.OrdinalIgnoreCase) >= 0;
        bool isQueue = name.IndexOf("QueuePacket", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!isDma && !isQueue)
            return;

        // Best-effort engine filter: only the graphics/3D node.
        int node = PayloadInt(data, "NodeOrdinal", "hNode", "Node", "EngineOrdinal");
        if (node != int.MinValue && node != GraphicsEngineOrdinal)
            return;

        bool isStart = name.EndsWith("Start", StringComparison.OrdinalIgnoreCase);
        bool isStopOrInfo = name.EndsWith("Stop", StringComparison.OrdinalIgnoreCase)
                            || name.EndsWith("Info", StringComparison.OrdinalIgnoreCase)
                            || name.EndsWith("End", StringComparison.OrdinalIgnoreCase);

        long qpc = _timebase.ToQpc(data);

        if (isQueue && isStart)
        {
            _queueDepth++;
            return;
        }

        if (isDma && isStart)
        {
            // A packet actually began executing on the engine — check the idle gap before it.
            if (_lastRunEndQpc != 0 && _queueDepth > 0)
            {
                double gapMs = (qpc - _lastRunEndQpc) * 1000.0 / Clock.Frequency;
                if (gapMs >= _gapThresholdMs)
                {
                    var confidence = gapMs >= _gapThresholdMs * 3 ? DetectionConfidence.Medium : DetectionConfidence.Low;
                    Raise(new StutterCandidate(qpc, gapMs, DetectorKind.GpuPacketGap, confidence,
                        $"3D engine idle {gapMs:F2} ms with {_queueDepth} packet(s) queued"));
                }
            }
            return;
        }

        if (isDma && isStopOrInfo)
        {
            _lastRunEndQpc = qpc;
            if (_queueDepth > 0) _queueDepth--;
        }
    }

    private void Raise(StutterCandidate c)
    {
        try { StutterDetected?.Invoke(this, c); } catch { /* isolation */ }
    }

    /// <summary>First payload field found by name, as an int; <see cref="int.MinValue"/> if none present.</summary>
    private static int PayloadInt(TraceEvent data, params string[] names)
    {
        foreach (var n in names)
        {
            try
            {
                object? v = data.PayloadByName(n);
                if (v is null) continue;
                return v switch
                {
                    int i => i,
                    uint u => (int)u,
                    long l => (int)l,
                    ulong ul => (int)ul,
                    byte b => b,
                    short s => s,
                    _ => int.TryParse(v.ToString(), out var p) ? p : int.MinValue,
                };
            }
            catch { /* field not on this event */ }
        }
        return int.MinValue;
    }
}
