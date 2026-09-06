using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;

namespace StutterDiag.Etw;

/// <summary>
/// Self-health for the ETW sessions. Tracks lost / dropped events and buffer overflows from
/// <c>ETWTraceEventSource</c> counters and the <c>Microsoft-Windows-Kernel-EventTracing</c>
/// provider, and raises an <see cref="EventCategory.Health"/> <see cref="MonitorEvent"/>
/// whenever losses increase — so every report can state how trustworthy the fine-grained
/// data is (see docs/LIMITATIONS.md §3).
/// </summary>
public sealed class EtwSessionHealth
{
    private readonly QpcClock _clock;
    private readonly List<(string Label, TraceEventSource Source)> _sources = new();

    private long _lastReportedLost;
    private long _bufferOverflowEvents;

    public EtwSessionHealth(QpcClock clock) => _clock = clock;

    /// <summary>Cumulative events the OS reported as lost across all attached sources.</summary>
    public long LostEventCount { get; private set; }

    /// <summary>Count of Kernel-EventTracing buffer-overflow notifications seen.</summary>
    public long BufferOverflowEvents => Interlocked.Read(ref _bufferOverflowEvents);

    public event EventHandler<MonitorEvent>? EventCaptured;

    /// <summary>
    /// Attach a session's source. Call once per session (kernel + user) after
    /// <c>Session.Source</c> exists but before processing starts.
    /// </summary>
    public void Attach(TraceEventSource source, string label)
    {
        _sources.Add((label, source));

        // Kernel-EventTracing surfaces buffer-loss records through the dynamic parser.
        // NOTE(build): event names ("RTLostEvent", "LostEvent", "BufferLost") and IDs vary by
        //              Windows build; match on opcode/keyword defensively.
        source.Dynamic.All += OnDynamic;
    }

    private void OnDynamic(TraceEvent data)
    {
        if (data.ProviderName != "Microsoft-Windows-Kernel-EventTracing")
            return;

        var name = data.EventName ?? string.Empty;
        if (name.IndexOf("Lost", StringComparison.OrdinalIgnoreCase) < 0 &&
            name.IndexOf("Buffer", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        Interlocked.Increment(ref _bufferOverflowEvents);
        Raise(EventSeverity.Warning, $"ETW buffer/loss notification: {name}", new Dictionary<string, string>
        {
            ["provider"] = data.ProviderName,
            ["event"] = name,
        });
    }

    /// <summary>
    /// Poll the per-source lost-event counters. Call on a low-frequency timer (e.g. every flush
    /// interval). Raises one Health event per increase.
    /// </summary>
    public void Poll()
    {
        long total = 0;
        foreach (var (label, src) in _sources)
        {
            // ETWTraceEventSource.EventsLost is a running count for a real-time session.
            long lost = src.EventsLost;
            total += lost;
        }

        LostEventCount = total;
        if (total > _lastReportedLost)
        {
            long delta = total - _lastReportedLost;
            _lastReportedLost = total;
            Raise(EventSeverity.Warning, $"{delta} ETW event(s) lost (total {total})", new Dictionary<string, string>
            {
                ["lostDelta"] = delta.ToString(),
                ["lostTotal"] = total.ToString(),
            });
        }
    }

    private void Raise(EventSeverity severity, string message, IReadOnlyDictionary<string, string> data)
    {
        long qpc = _clock.GetTimestamp();
        EventCaptured?.Invoke(this, new MonitorEvent
        {
            TimestampQpc = qpc,
            TimestampUtc = _clock.QpcToUtc(qpc),
            Category = EventCategory.Health,
            Source = "Etw.Health",
            Provider = "Microsoft-Windows-Kernel-EventTracing",
            Severity = severity,
            Message = message,
            Data = data,
        });
    }
}
