namespace StutterDiag.Monitors.Common;

/// <summary>
/// Minimal surface the telemetry monitors need from <c>StutterDiag.Etw</c>'s user-mode
/// session. It is declared here (not in the Etw project) so the two engineers can work in
/// parallel: the Service is responsible for adapting whatever the Etw project actually
/// exposes (an <c>EtwUserSession</c> / TraceEvent <c>ETWTraceEventSource</c>) to this
/// interface and injecting it. When no feed is available, monitors fall back to
/// <c>EventLogWatcher</c> and report <see cref="StutterDiag.Core.Model.HealthStatus.Degraded"/>.
/// </summary>
public interface IEtwEventFeed
{
    /// <summary>True while the underlying real-time session is running.</summary>
    bool IsLive { get; }

    /// <summary>
    /// Request that the given logical provider group be enabled on the session. Idempotent;
    /// a no-op when the provider is not present or the session is not elevated.
    /// </summary>
    void Subscribe(EtwFeedTopic topic);

    /// <summary>Raised for every decoded ETW event belonging to a subscribed topic.</summary>
    event EventHandler<EtwFeedEvent>? EventReceived;
}

/// <summary>Logical provider groups the telemetry monitors consume. Names map 1:1 to ARCHITECTURE §8 providers.</summary>
public enum EtwFeedTopic
{
    /// <summary>Microsoft-Windows-Kernel-Power (sleep/resume, monitor power, battery).</summary>
    KernelPower,

    /// <summary>Microsoft-Windows-Kernel-Processor-Power (P-state / idle-state / throttle).</summary>
    KernelProcessorPower,

    /// <summary>Microsoft-Windows-Kernel-PnP (device arrival/removal/start problems).</summary>
    KernelPnp,

    /// <summary>Microsoft-Windows-DriverFrameworks-UserMode (UMDF device lifecycle).</summary>
    DriverFrameworks,

    /// <summary>Microsoft-Windows-USB-USBXHCI (xHCI controller / device resets).</summary>
    UsbXhci,

    /// <summary>Microsoft-Windows-Audio (engine state, glitches).</summary>
    Audio,

    /// <summary>Microsoft-Windows-WHEA-Logger (hardware error records).</summary>
    Whea,

    /// <summary>Microsoft-Windows-TPM-WMI (TPM/TBS provisioning and error events).</summary>
    TpmWmi,

    /// <summary>Microsoft-Windows-DxgKrnl (GPU scheduler; TDR / adapter reset).</summary>
    DxgKrnl,

    /// <summary>Microsoft-Windows-DeviceGuard (VBS / HVCI status changes).</summary>
    DeviceGuard,

    /// <summary>Microsoft-Windows-Kernel-Memory (memory pressure, low-memory conditions).</summary>
    KernelMemory
}

/// <summary>
/// One decoded ETW event handed to a monitor. The Etw project already resolves the QPC axis
/// (ARCHITECTURE §5), so <see cref="TimestampQpc"/> is authoritative and needs no conversion.
/// </summary>
/// <param name="TimestampQpc">QueryPerformanceCounter ticks on the shared axis.</param>
/// <param name="TimestampUtc">Wall-clock UTC for display only.</param>
/// <param name="Topic">Which logical group this event was delivered for.</param>
/// <param name="ProviderName">Full ETW provider name.</param>
/// <param name="EventId">Provider event id.</param>
/// <param name="TaskName">Decoded task name, or "" if the manifest gave none.</param>
/// <param name="OpcodeName">Decoded opcode name, or "".</param>
/// <param name="Level">ETW level (1 Critical .. 5 Verbose).</param>
/// <param name="FormattedMessage">Rendered message when the manifest supplies one.</param>
/// <param name="Payload">Flattened payload fields (name -&gt; string value).</param>
public sealed record EtwFeedEvent(
    long TimestampQpc,
    DateTime TimestampUtc,
    EtwFeedTopic Topic,
    string ProviderName,
    int EventId,
    string TaskName,
    string OpcodeName,
    int Level,
    string? FormattedMessage,
    IReadOnlyDictionary<string, string> Payload);
