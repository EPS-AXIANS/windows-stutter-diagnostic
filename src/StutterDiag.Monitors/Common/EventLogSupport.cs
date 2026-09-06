using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using System.Xml.Linq;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;

namespace StutterDiag.Monitors.Common;

/// <summary>
/// A single push subscription to one Windows event-log channel. Wraps
/// <see cref="EventLogWatcher"/> so a missing/denied channel is reported via
/// <see cref="Error"/> instead of throwing, and every delivered record is handed to the
/// callback already disposed-safe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EventLogSubscription : IDisposable
{
    private readonly string _channel;
    private readonly string? _xpath;
    private readonly Action<EventRecord> _onRecord;
    private EventLogWatcher? _watcher;

    public EventLogSubscription(string channel, string? xpath, Action<EventRecord> onRecord)
    {
        _channel = channel;
        _xpath = xpath;
        _onRecord = onRecord;
    }

    public string Channel => _channel;
    public bool Active { get; private set; }
    public string? Error { get; private set; }

    public bool TryStart()
    {
        try
        {
            var query = _xpath is null
                ? new EventLogQuery(_channel, PathType.LogName)
                : new EventLogQuery(_channel, PathType.LogName, _xpath);

            // Bubble query/channel errors here rather than on the callback thread.
            query.TolerateQueryErrors = true;

            _watcher = new EventLogWatcher(query);
            _watcher.EventRecordWritten += OnWritten;
            _watcher.Enabled = true;
            Active = true;
            return true;
        }
        catch (EventLogNotFoundException ex) { Error = $"channel not present: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { Error = $"access denied: {ex.Message}"; }
        catch (EventLogException ex) { Error = ex.Message; }
        catch (Exception ex) { Error = ex.Message; }
        return false;
    }

    private void OnWritten(object? sender, EventRecordWrittenEventArgs e)
    {
        if (e.EventException is not null || e.EventRecord is null) return;
        try
        {
            using EventRecord rec = e.EventRecord!;
            _onRecord(rec);
        }
        catch
        {
            // A single malformed record must not tear down the subscription.
        }
    }

    public void Dispose()
    {
        try
        {
            if (_watcher is not null)
            {
                _watcher.EventRecordWritten -= OnWritten;
                _watcher.Enabled = false;
                _watcher.Dispose();
            }
        }
        catch { /* ignore */ }
        _watcher = null;
        Active = false;
    }
}

/// <summary>Turns an <see cref="EventRecord"/> into a <see cref="MonitorEvent"/> on the QPC axis.</summary>
[SupportedOSPlatform("windows")]
public static class EventRecordConverter
{
    public static MonitorEvent ToMonitorEvent(EventRecord rec, QpcClock clock, string source, EventCategory category)
    {
        DateTime utc = (rec.TimeCreated ?? DateTime.UtcNow).ToUniversalTime();
        long qpc = clock.UtcToQpc(utc);

        string? xml = SafeXml(rec);
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        PopulateFromXml(xml, data);
        PopulateFromProperties(rec, data);

        int? eventId = TryGet(() => rec.Id);
        string? provider = TryGet(() => rec.ProviderName);
        if (provider is not null) data.TryAdd("provider", provider);
        string? task = TryGet(() => rec.TaskDisplayName);
        if (!string.IsNullOrEmpty(task)) data.TryAdd("task", task!);

        return new MonitorEvent
        {
            TimestampQpc = qpc,
            TimestampUtc = utc,
            Category = category,
            Source = source,
            Provider = provider,
            EventId = eventId,
            Severity = MapSeverity(TryGet(() => rec.Level)),
            Message = SafeDescription(rec) ?? $"{provider} event {eventId}",
            RawXml = xml,
            Data = data.Count == 0 ? null : data
        };
    }

    public static EventSeverity MapSeverity(byte? level) => level switch
    {
        1 => EventSeverity.Critical,
        2 => EventSeverity.Error,
        3 => EventSeverity.Warning,
        4 => EventSeverity.Info,
        5 => EventSeverity.Verbose,
        _ => EventSeverity.Info
    };

    private static string? SafeDescription(EventRecord rec)
    {
        try { return rec.FormatDescription(); }
        catch { return null; }
    }

    private static string? SafeXml(EventRecord rec)
    {
        try { return rec.ToXml(); }
        catch { return null; }
    }

    private static void PopulateFromProperties(EventRecord rec, Dictionary<string, string> data)
    {
        try
        {
            var props = rec.Properties;
            for (int i = 0; i < props.Count; i++)
            {
                string key = $"p{i}";
                if (data.ContainsKey(key)) continue;
                data[key] = props[i].Value?.ToString() ?? "";
            }
        }
        catch { /* some records expose no properties */ }
    }

    private static void PopulateFromXml(string? xml, Dictionary<string, string> data)
    {
        if (string.IsNullOrEmpty(xml)) return;
        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            foreach (var d in doc.Descendants(ns + "Data"))
            {
                string? name = d.Attribute("Name")?.Value;
                if (string.IsNullOrEmpty(name)) continue;
                data[name!] = d.Value;
            }

            var sys = doc.Root?.Element(ns + "System");
            if (sys is not null)
            {
                string? keywords = sys.Element(ns + "Keywords")?.Value;
                if (!string.IsNullOrEmpty(keywords)) data.TryAdd("keywords", keywords!);
                string? opcode = sys.Element(ns + "Opcode")?.Value;
                if (!string.IsNullOrEmpty(opcode)) data.TryAdd("opcode", opcode!);
            }
        }
        catch { /* non-fatal: structured extraction is best-effort */ }
    }

    private static T? TryGet<T>(Func<T> f)
    {
        try { return f(); }
        catch { return default; }
    }
}
