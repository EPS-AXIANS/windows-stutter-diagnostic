using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing;
using StutterDiag.Core.Time;
using StutterDiag.Etw;
using StutterDiag.Monitors.Common;

namespace StutterDiag.Service.Ipc;

/// <summary>
/// Adapts <see cref="EtwUserSession"/> to the <see cref="IEtwEventFeed"/> that the telemetry
/// monitors (Power, PnP, TPM, WHEA, Device, GPU-driver) consume. The Etw project already puts
/// every record on the shared QPC axis via <see cref="EtwTimebase"/>, so
/// <see cref="EtwFeedEvent.TimestampQpc"/> is authoritative and needs no further conversion.
/// </summary>
/// <remarks>
/// <see cref="Attach"/> must be called <b>before</b> <c>EtwProcessing.StartAsync</c> pumps the
/// user session, because that is when <c>Source.Process()</c> begins delivering events. When the
/// user session is unavailable (not elevated, or no providers enabled) the adapter attaches
/// nothing, reports <see cref="IsLive"/> = false and never raises — monitors then fall back to
/// <c>EventLogWatcher</c> and report <see cref="StutterDiag.Core.Model.HealthStatus.Degraded"/>.
/// </remarks>
public sealed class EtwUserFeedAdapter : IEtwEventFeed
{
    // Manifest provider name -> logical topic. Mirrors ARCHITECTURE §8 and EtwUserSession.Wanted.
    private static readonly IReadOnlyDictionary<string, EtwFeedTopic> ProviderTopics =
        new Dictionary<string, EtwFeedTopic>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft-Windows-Kernel-Power"] = EtwFeedTopic.KernelPower,
            ["Microsoft-Windows-Kernel-Processor-Power"] = EtwFeedTopic.KernelProcessorPower,
            ["Microsoft-Windows-Kernel-PnP"] = EtwFeedTopic.KernelPnp,
            ["Microsoft-Windows-DriverFrameworks-UserMode"] = EtwFeedTopic.DriverFrameworks,
            ["Microsoft-Windows-USB-USBXHCI"] = EtwFeedTopic.UsbXhci,
            ["Microsoft-Windows-Audio"] = EtwFeedTopic.Audio,
            ["Microsoft-Windows-WHEA-Logger"] = EtwFeedTopic.Whea,
            ["Microsoft-Windows-TPM-WMI"] = EtwFeedTopic.TpmWmi,
            ["Microsoft-Windows-DxgKrnl"] = EtwFeedTopic.DxgKrnl,
            ["Microsoft-Windows-DeviceGuard"] = EtwFeedTopic.DeviceGuard,
            ["Microsoft-Windows-Kernel-Memory"] = EtwFeedTopic.KernelMemory,
        };

    private readonly EtwUserSession _user;
    private readonly EtwTimebase _timebase;
    private readonly QpcClock _clock;
    private readonly ConcurrentDictionary<EtwFeedTopic, byte> _subscribed = new();
    private int _attached;

    public EtwUserFeedAdapter(EtwUserSession user, EtwTimebase timebase, QpcClock clock)
    {
        _user = user;
        _timebase = timebase;
        _clock = clock;
    }

    /// <inheritdoc/>
    public bool IsLive => _user.IsAvailable && _user.Session is not null && _user.EnabledProviders.Count > 0;

    /// <inheritdoc/>
    public event EventHandler<EtwFeedEvent>? EventReceived;

    /// <summary>
    /// Register the single dynamic-parser callback on the user session's source. Idempotent and
    /// a no-op when the session is not available. Call once, before ETW processing starts.
    /// </summary>
    public void Attach()
    {
        if (Interlocked.Exchange(ref _attached, 1) == 1) return;
        var source = _user.Session?.Source;
        if (source is null) return;

        // One handler for every dynamic (manifest) provider; we filter to the ones we mapped.
        source.Dynamic.All += OnEvent;
    }

    /// <inheritdoc/>
    public void Subscribe(EtwFeedTopic topic)
    {
        // Providers are enabled up-front by EtwUserSession; Subscribe only records interest so
        // OnEvent can drop topics no monitor asked for. Idempotent.
        _subscribed[topic] = 1;
    }

    private void OnEvent(TraceEvent data)
    {
        if (EventReceived is null) return;
        if (!ProviderTopics.TryGetValue(data.ProviderName, out var topic)) return;
        if (!_subscribed.ContainsKey(topic)) return;

        long qpc = _timebase.ToQpc(data.TimeStampRelativeMSec);

        Dictionary<string, string> payload;
        try
        {
            var names = data.PayloadNames;
            payload = new Dictionary<string, string>(names.Length, StringComparer.Ordinal);
            for (int i = 0; i < names.Length; i++)
            {
                try { payload[names[i]] = data.PayloadString(i); }
                catch { payload[names[i]] = ""; }
            }
        }
        catch
        {
            payload = new Dictionary<string, string>(0, StringComparer.Ordinal);
        }

        string? message = null;
        try { message = data.FormattedMessage; } catch { /* manifest gave none */ }

        var feed = new EtwFeedEvent(
            TimestampQpc: qpc,
            TimestampUtc: _clock.QpcToUtc(qpc),
            Topic: topic,
            ProviderName: data.ProviderName,
            EventId: (int)data.ID,
            TaskName: data.TaskName ?? "",
            OpcodeName: data.OpcodeName ?? "",
            Level: (int)data.Level,
            FormattedMessage: message,
            Payload: payload);

        try { EventReceived?.Invoke(this, feed); }
        catch { /* a slow/broken monitor sink never faults the ETW pump */ }
    }
}
