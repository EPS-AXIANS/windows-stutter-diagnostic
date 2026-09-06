using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Monitors.Common;

namespace StutterDiag.Monitors.Whea;

/// <summary>
/// Windows Hardware Error Architecture records, from the
/// <c>Microsoft-Windows-WHEA-Logger</c> ETW provider (via <see cref="IEtwEventFeed"/>) and the
/// matching System-log entries. Each is normalised to a <see cref="WheaError"/> and emitted as
/// <see cref="EventCategory.Whea"/> via <see cref="WheaError.ToMonitorEvent"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WheaMonitor : MonitorBase, IWheaMonitor
{
    private readonly IEtwEventFeed? _etw;
    private readonly List<EventLogSubscription> _subs = new();

    public WheaMonitor(QpcClock clock, IEtwEventFeed? etwFeed = null) : base("Whea", clock)
    {
        _etw = etwFeed;
    }

    public override Task StartAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            SetHealth(MonitorHealth.Unavailable("WHEA monitor requires Windows"));
            return Task.CompletedTask;
        }

        try
        {
            if (_etw is not null)
            {
                _etw.EventReceived += OnEtwEvent;
                _etw.Subscribe(EtwFeedTopic.Whea);
            }

            TrySubscribe("System",
                "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger']]]");
            TrySubscribe("Microsoft-Windows-Kernel-WHEA/Errors", null);
            TrySubscribe("Microsoft-Windows-Kernel-WHEA/Operational", "*[System[(Level=1 or Level=2 or Level=3)]]");

            int active = _subs.Count(s => s.Active);
            if (active == 0 && _etw is null)
                SetHealth(MonitorHealth.Degraded("no WHEA source available (provider/channel absent)"));
            else if (active < _subs.Count)
                SetHealth(MonitorHealth.Degraded($"{active}/{_subs.Count} WHEA channels active"));
            else
                SetHealth(MonitorHealth.Ok);
        }
        catch (Exception ex)
        {
            SetHealth(MonitorHealth.Failed(ex.Message));
        }
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken ct)
    {
        if (_etw is not null) _etw.EventReceived -= OnEtwEvent;
        foreach (var s in _subs) s.Dispose();
        _subs.Clear();
        return Task.CompletedTask;
    }

    private void TrySubscribe(string channel, string? xpath)
    {
        var sub = new EventLogSubscription(channel, xpath, OnRecord);
        sub.TryStart();
        _subs.Add(sub);
    }

    private void OnRecord(EventRecord rec)
    {
        DateTime utc = (SafeTime(rec) ?? DateTime.UtcNow).ToUniversalTime();
        long qpc = Clock.UtcToQpc(utc);

        string? xml = SafeXml(rec);
        string message = SafeDescription(rec) ?? $"WHEA-Logger event {SafeId(rec)}";
        int level = SafeLevel(rec) ?? 4;

        var err = new WheaError(
            TimestampQpc: qpc,
            TimestampUtc: utc,
            ErrorSource: ClassifySource(message, xml),
            Severity: ClassifySeverity(message, level),
            Description: Trim(message),
            RawXml: xml);

        RaiseEvent(err.ToMonitorEvent());
    }

    private void OnEtwEvent(object? sender, EtwFeedEvent e)
    {
        if (e.Topic != EtwFeedTopic.Whea) return;

        string text = e.FormattedMessage ?? $"{e.TaskName} {e.OpcodeName}".Trim();
        string payloadSource = FirstNonEmpty(
            Get(e.Payload, "ErrorSource"), Get(e.Payload, "ErrorSourceType"), Get(e.Payload, "Source"));

        var err = new WheaError(
            TimestampQpc: e.TimestampQpc,
            TimestampUtc: e.TimestampUtc,
            ErrorSource: string.IsNullOrEmpty(payloadSource) ? ClassifySource(text, null) : NormaliseSource(payloadSource),
            Severity: ClassifySeverity(text, e.Level),
            Description: string.IsNullOrWhiteSpace(text) ? $"WHEA-Logger id {e.EventId}" : Trim(text),
            RawXml: null);

        RaiseEvent(err.ToMonitorEvent());
    }

    // ---- classification (keyword + level; exact WHEA ids vary by build) ----

    private static string ClassifySeverity(string message, int level)
    {
        string m = message.ToLowerInvariant();
        if (m.Contains("fatal") || level == 1) return "Fatal";
        if (m.Contains("recoverable") || m.Contains("uncorrect")) return "Recoverable";
        if (m.Contains("corrected") || m.Contains("correctable")) return "Corrected";
        if (m.Contains("informational") || level >= 4) return "Informational";
        return level == 2 ? "Recoverable" : "Corrected";
    }

    private static string ClassifySource(string message, string? xml)
    {
        string m = (message + " " + (xml ?? "")).ToLowerInvariant();
        if (m.Contains("pci express") || m.Contains("pcie")) return "Pcie";
        if (m.Contains("memory") || m.Contains("dram") || m.Contains("ecc")) return "Memory";
        if (m.Contains("cache")) return "Cache";
        if (m.Contains("tlb")) return "Tlb";
        if (m.Contains("machine check") || m.Contains(" mca")) return "MachineCheck";
        if (m.Contains("processor") || m.Contains(" cpu")) return "Processor";
        if (m.Contains("nmi")) return "Nmi";
        if (m.Contains("bus")) return "Bus";
        if (m.Contains("platform")) return "Platform";
        return "Unknown";
    }

    private static string NormaliseSource(string raw)
    {
        string r = raw.Trim().ToLowerInvariant();
        return r switch
        {
            "0" or "mce" or "machinecheck" or "machine check exception" => "MachineCheck",
            "1" or "cmc" => "Processor",
            "3" or "nmi" => "Nmi",
            "4" or "pcie" or "pci express" => "Pcie",
            "5" or "generic" => "Platform",
            _ when r.Contains("mem") => "Memory",
            _ when r.Contains("cache") => "Cache",
            _ when r.Contains("pcie") || r.Contains("pci express") => "Pcie",
            _ when r.Contains("processor") || r.Contains("cpu") => "Processor",
            _ => "Unknown"
        };
    }

    private static string Trim(string s) => s.Length <= 1024 ? s : s[..1024];

    private static string FirstNonEmpty(params string[] values)
        => Array.Find(values, v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static string Get(IReadOnlyDictionary<string, string> d, string k)
        => d.TryGetValue(k, out var v) ? v : "";

    private static DateTime? SafeTime(EventRecord r) { try { return r.TimeCreated; } catch { return null; } }
    private static string? SafeXml(EventRecord r) { try { return r.ToXml(); } catch { return null; } }
    private static string? SafeDescription(EventRecord r) { try { return r.FormatDescription(); } catch { return null; } }
    private static int SafeId(EventRecord r) { try { return r.Id; } catch { return 0; } }
    private static byte? SafeLevel(EventRecord r) { try { return r.Level; } catch { return null; } }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
