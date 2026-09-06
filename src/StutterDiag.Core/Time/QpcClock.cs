using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace StutterDiag.Core.Time;

/// <summary>
/// The single monotonic time axis for the whole application. All events, samples and ETW
/// records are timestamped in QueryPerformanceCounter ticks and only ever ordered/windowed
/// on that axis. Wall-clock UTC is derived from an anchor pair captured at construction and
/// periodically re-anchored by the service (clock drift over multi-day runs is real).
/// </summary>
/// <remarks>
/// On Windows, <see cref="Stopwatch.GetTimestamp"/> is QPC, so it is used directly. On other
/// platforms (CI, unit tests) it falls back to <see cref="Stopwatch"/> semantics as well —
/// the class stays usable, only the "this is literally ETW's QPC" guarantee is Windows-only.
/// </remarks>
public sealed class QpcClock
{
    private readonly object _gate = new();
    private long _anchorQpc;
    private DateTime _anchorUtc;

    public QpcClock()
    {
        Reanchor();
    }

    /// <summary>Ticks per second on the QPC axis.</summary>
    public long Frequency { get; } = Stopwatch.Frequency;

    /// <summary>Current value of the monotonic counter, in QPC ticks.</summary>
    public long GetTimestamp() => Stopwatch.GetTimestamp();

    /// <summary>Re-capture the QPC↔UTC anchor. The service calls this every few minutes.</summary>
    public void Reanchor()
    {
        lock (_gate)
        {
            // Capture as close together as possible; take QPC on both sides of the wall read.
            long a = Stopwatch.GetTimestamp();
            DateTime utc = DateTime.UtcNow;
            long b = Stopwatch.GetTimestamp();
            _anchorQpc = a + (b - a) / 2;
            _anchorUtc = utc;
        }
    }

    public DateTime QpcToUtc(long qpc)
    {
        lock (_gate)
        {
            double seconds = (qpc - _anchorQpc) / (double)Frequency;
            return _anchorUtc.AddSeconds(seconds);
        }
    }

    public long UtcToQpc(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Local) utc = utc.ToUniversalTime();
        else if (utc.Kind == DateTimeKind.Unspecified) utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);

        lock (_gate)
        {
            double seconds = (utc - _anchorUtc).TotalSeconds;
            return _anchorQpc + (long)(seconds * Frequency);
        }
    }

    public double TicksToMs(long ticks) => ticks * 1000.0 / Frequency;

    public long MsToTicks(double ms) => (long)(ms / 1000.0 * Frequency);

    public DateTime UtcNow => QpcToUtc(GetTimestamp());

    // --- Raw QPC P/Invoke, exposed for components that need the exact Win32 value
    //     (e.g. cross-checking a TraceEvent QPC). Falls back to Stopwatch off-Windows. ---

    public static long QueryPerformanceCounterRaw()
    {
        if (OperatingSystem.IsWindows() && NativeQpc.QueryPerformanceCounter(out long v))
            return v;
        return Stopwatch.GetTimestamp();
    }

    public static long QueryPerformanceFrequencyRaw()
    {
        if (OperatingSystem.IsWindows() && NativeQpc.QueryPerformanceFrequency(out long f))
            return f;
        return Stopwatch.Frequency;
    }

    private static class NativeQpc
    {
        [SupportedOSPlatform("windows")]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryPerformanceCounter(out long value);

        [SupportedOSPlatform("windows")]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryPerformanceFrequency(out long value);
    }
}
