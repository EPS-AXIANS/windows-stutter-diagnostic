using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Monitors.Common;

namespace StutterDiag.Monitors.Gpu;

/// <summary>
/// GPU driver disturbances: TDR ("display driver stopped responding and has recovered"),
/// driver restarts and display-subsystem errors from the <c>System</c> log
/// (<c>Display</c>, <c>nvlddmkm</c>, <c>amdkmdag</c>, <c>igfx*</c> providers), plus DxgKrnl
/// adapter-reset events when an <see cref="IEtwEventFeed"/> is supplied. Emits
/// <see cref="EventCategory.GpuDriver"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GpuDriverEventMonitor : MonitorBase, IMonitor
{
    // Common display-driver ids: 4101 = TDR recovered, 4098 = driver error,
    // 4099/4100 = internal driver errors. Kept broad + Warning filter to catch build variance.
    private const string Query =
        "*[System[Provider[" +
        "@Name='Display' or @Name='nvlddmkm' or @Name='amdkmdag' or @Name='amdwddmg' or " +
        "@Name='igfx' or @Name='igfxn' or @Name='Microsoft-Windows-DxgKrnl'" +
        "]]] or *[System[(Level=1 or Level=2 or Level=3)] and System[Provider[@Name='Display']]]";

    private readonly IEtwEventFeed? _etw;
    private EventLogSubscription? _sub;

    public GpuDriverEventMonitor(QpcClock clock, IEtwEventFeed? etwFeed = null) : base("Gpu.DriverEvents", clock)
    {
        _etw = etwFeed;
    }

    public override Task StartAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            SetHealth(MonitorHealth.Unavailable("GPU driver-event monitor requires Windows"));
            return Task.CompletedTask;
        }

        try
        {
            if (_etw is not null)
            {
                _etw.EventReceived += OnEtwEvent;
                _etw.Subscribe(EtwFeedTopic.DxgKrnl);
            }

            _sub = new EventLogSubscription("System", Query, OnRecord);
            bool ok = _sub.TryStart();

            SetHealth(ok
                ? MonitorHealth.Ok
                : MonitorHealth.Degraded($"System-log GPU-driver subscription failed ({_sub.Error}); DxgKrnl ETW only"));
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
        _sub?.Dispose();
        _sub = null;
        return Task.CompletedTask;
    }

    private void OnRecord(EventRecord rec)
    {
        string provider = SafeProvider(rec);
        int id = SafeId(rec);
        string message = SafeMessage(rec) ?? $"{provider} event {id}";

        var evt = EventRecordConverter.ToMonitorEvent(rec, Clock, Name, EventCategory.GpuDriver)
            .WithData("driver", NormalizeDriver(provider))
            .WithData("isTdr", (id == 4101 || message.Contains("stopped responding", StringComparison.OrdinalIgnoreCase)).ToString());

        RaiseEvent(evt);
    }

    private void OnEtwEvent(object? sender, EtwFeedEvent e)
    {
        if (e.Topic != EtwFeedTopic.DxgKrnl) return;

        string hay = $"{e.TaskName} {e.OpcodeName} {e.FormattedMessage}".ToLowerInvariant();
        bool interesting = hay.Contains("reset") || hay.Contains("tdr") || hay.Contains("timeout")
                           || hay.Contains("recover") || hay.Contains("removed") || hay.Contains("hung");
        if (!interesting) return;

        RaiseEvent(new MonitorEvent
        {
            TimestampQpc = e.TimestampQpc,
            TimestampUtc = e.TimestampUtc,
            Category = EventCategory.GpuDriver,
            Source = Name,
            Provider = e.ProviderName,
            EventId = e.EventId,
            Severity = EventSeverity.Warning,
            Message = e.FormattedMessage ?? $"DxgKrnl {e.TaskName}/{e.OpcodeName} (id {e.EventId}).",
            Data = new Dictionary<string, string>(e.Payload) { ["driver"] = "dxgkrnl.sys", ["provider"] = e.ProviderName }
        });
    }

    private static string NormalizeDriver(string provider) => provider.ToLowerInvariant() switch
    {
        "nvlddmkm" or "nvlddmkm-displaycontainer" => "nvlddmkm.sys",
        "amdkmdag" or "amdwddmg" or "amdkmpfd" => "amdkmdag.sys",
        "igfx" or "igfxn" => "igdkmd64.sys",
        "microsoft-windows-dxgkrnl" or "display" => "dxgkrnl.sys",
        _ => provider
    };

    private static string SafeProvider(EventRecord r) { try { return r.ProviderName ?? ""; } catch { return ""; } }
    private static int SafeId(EventRecord r) { try { return r.Id; } catch { return 0; } }
    private static string? SafeMessage(EventRecord r) { try { return r.FormatDescription(); } catch { return null; } }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
