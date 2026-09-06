using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Monitors.Common;

namespace StutterDiag.Monitors.Process;

/// <summary>
/// Whole-system process sampling. A cheap full snapshot comes from
/// <c>NtQuerySystemInformation(SystemProcessInformation)</c> (CPU kernel/user deltas, IO byte
/// deltas, thread/handle counts, page-fault delta); <see cref="System.Diagnostics.Process"/>
/// is the fallback if the native call is unavailable. Command lines and user names are never
/// read (ARCHITECTURE §10 / <see cref="PrivacyOptions"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessMonitor : MonitorBase, IProcessMonitor
{
    private readonly AppConfig _config;
    private readonly object _sync = new();

    private readonly Dictionary<int, PrevSample> _prev = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _nativeFailedOnce;

    /// <summary>Raised for every periodic snapshot so the orchestrator can persist it. Not part of <see cref="IProcessMonitor"/>.</summary>
    public event EventHandler<ProcessSnapshot>? SnapshotCaptured;

    public ProcessMonitor(AppConfig config, QpcClock clock) : base("Process", clock)
    {
        _config = config;
    }

    public override Task StartAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            SetHealth(MonitorHealth.Unavailable("process monitor requires Windows"));
            return Task.CompletedTask;
        }

        try
        {
            // Prime the baseline so the first periodic snapshot already has CPU deltas.
            _ = Capture(SnapshotTrigger.Periodic, publish: false);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;
            _loop = Task.Run(() => PeriodicLoopAsync(token));
            SetHealth(_nativeFailedOnce
                ? MonitorHealth.Degraded("NtQuerySystemInformation unavailable; using System.Diagnostics.Process (no IO byte / page-fault deltas)")
                : MonitorHealth.Ok);
        }
        catch (Exception ex)
        {
            SetHealth(MonitorHealth.Failed(ex.Message));
        }
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* cancellation */ }
        }
        _loop = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task PeriodicLoopAsync(CancellationToken ct)
    {
        double seconds = _config.Sampling.ProcessSnapshotSeconds <= 0 ? 2 : _config.Sampling.ProcessSnapshotSeconds;
        var period = TimeSpan.FromSeconds(seconds);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(period, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var snap = Capture(SnapshotTrigger.Periodic, publish: true);
                SnapshotCaptured?.Invoke(this, snap);
            }
            catch (Exception ex)
            {
                SetHealth(MonitorHealth.Degraded($"periodic snapshot failed: {ex.GetType().Name}"));
            }
        }
    }

    /// <summary>High-resolution on-demand capture. Periodic trigger returns the top-N by CPU; all others return every process.</summary>
    public ProcessSnapshot CaptureSnapshot(SnapshotTrigger trigger) => Capture(trigger, publish: false);

    private ProcessSnapshot Capture(SnapshotTrigger trigger, bool publish)
    {
        lock (_sync)
        {
            long qpc = Clock.GetTimestamp();
            DateTime utc = Clock.QpcToUtc(qpc);

            List<RawProc> raw;
            try
            {
                raw = _nativeFailedOnce ? ReadViaProcess() : ReadViaNtQuery();
            }
            catch (Exception)
            {
                _nativeFailedOnce = true;
                raw = ReadViaProcess();
            }

            var rows = BuildRows(raw, qpc);

            // Refresh the baseline for the next delta.
            _prev.Clear();
            foreach (var p in raw)
            {
                _prev[p.Pid] = new PrevSample(p.CpuTime100ns, p.ReadBytes, p.WriteBytes, p.PageFaultCount, qpc,
                    rows.TryGetValue(p.Pid, out var r) ? r.CpuPercent : 0);
            }

            IReadOnlyList<ProcessSnapshotRow> ordered = rows.Values
                .OrderByDescending(r => r.CpuPercent)
                .ThenByDescending(r => r.WorkingSetBytes)
                .ToList();

            if (trigger == SnapshotTrigger.Periodic && _config.Sampling.TopProcessCount > 0)
                ordered = ordered.Take(_config.Sampling.TopProcessCount).ToList();

            var snapshot = new ProcessSnapshot(qpc, utc, trigger, ordered);

            if (publish)
            {
                RaiseEvent(NewEvent(EventCategory.Other,
                    $"Process snapshot ({trigger}): {ordered.Count} rows.",
                    EventSeverity.Verbose, qpc));
            }
            return snapshot;
        }
    }

    private static bool RecentlyStarted(long createFileTimeUtc, DateTime nowUtc)
    {
        if (createFileTimeUtc is <= 0 or > 2_600_000_000_000_000_000L) return false;
        try
        {
            double age = (nowUtc - DateTime.FromFileTimeUtc(createFileTimeUtc)).TotalSeconds;
            return age is > 0 and < 10;
        }
        catch
        {
            return false;
        }
    }

    private Dictionary<int, ProcessSnapshotRow> BuildRows(List<RawProc> raw, long qpc)
    {
        int ncpu = Math.Max(1, Environment.ProcessorCount);
        var result = new Dictionary<int, ProcessSnapshotRow>(raw.Count);

        foreach (var p in raw)
        {
            double cpuPct = 0, readPerSec = 0, writePerSec = 0;
            long faultsDelta = 0;
            bool spike = false;

            if (_prev.TryGetValue(p.Pid, out var prev))
            {
                double wall = (qpc - prev.Qpc) / (double)Clock.Frequency;
                if (wall > 0.0005)
                {
                    double cpuSec = Math.Max(0, p.CpuTime100ns - prev.CpuTime100ns) * 1e-7;
                    cpuPct = Math.Clamp(cpuSec / (wall * ncpu) * 100.0, 0, 100);
                    readPerSec = Math.Max(0, p.ReadBytes - prev.ReadBytes) / wall;
                    writePerSec = Math.Max(0, p.WriteBytes - prev.WriteBytes) / wall;
                    faultsDelta = Math.Max(0, p.PageFaultCount - prev.PageFaultCount);

                    // "Spike": CPU% at least doubled AND rose by >= 20 points versus the prior sample.
                    spike = prev.CpuPercent >= 0.5 && cpuPct >= 2 * prev.CpuPercent && (cpuPct - prev.CpuPercent) >= 20;
                }
            }

            bool recentlyStarted = RecentlyStarted(p.CreateTime100ns, Clock.QpcToUtc(qpc));

            result[p.Pid] = new ProcessSnapshotRow(
                Pid: p.Pid,
                Name: p.Name,
                CpuPercent: Math.Round(cpuPct, 2),
                WorkingSetBytes: p.WorkingSet,
                PrivateBytes: p.PrivateBytes,
                ReadBytesPerSec: (long)readPerSec,
                WriteBytesPerSec: (long)writePerSec,
                ThreadCount: p.ThreadCount,
                HandleCount: p.HandleCount,
                PageFaultsDelta: faultsDelta,
                KernelTimeMs: Math.Round(p.KernelTime100ns / 10_000.0, 1),
                UserTimeMs: Math.Round(p.UserTime100ns / 10_000.0, 1),
                RecentlyStarted: recentlyStarted,
                ActivitySpike: spike);
        }
        return result;
    }

    // ---------------------------------------------------------------------
    // NtQuerySystemInformation(SystemProcessInformation) — x64 SYSTEM_PROCESS_INFORMATION.
    // Offsets verified against phnt / ProcessHacker; re-verify at build time (flagged in report).
    // ---------------------------------------------------------------------
    private const int OFF_NextEntryOffset = 0x00;
    private const int OFF_NumberOfThreads = 0x04;
    private const int OFF_CreateTime = 0x20;
    private const int OFF_UserTime = 0x28;
    private const int OFF_KernelTime = 0x30;
    private const int OFF_ImageNameLength = 0x38;   // USHORT, bytes
    private const int OFF_ImageNameBuffer = 0x40;   // PWSTR
    private const int OFF_UniqueProcessId = 0x50;   // HANDLE
    private const int OFF_HandleCount = 0x60;       // ULONG
    private const int OFF_PageFaultCount = 0x80;    // ULONG
    private const int OFF_WorkingSetSize = 0x90;    // SIZE_T
    private const int OFF_PrivatePageCount = 0xC8;  // SIZE_T
    private const int OFF_ReadTransferCount = 0xE8; // ULONGLONG
    private const int OFF_WriteTransferCount = 0xF0;// ULONGLONG

    private List<RawProc> ReadViaNtQuery()
    {
        int len = 1 << 20; // 1 MiB initial
        IntPtr buffer = Marshal.AllocHGlobal(len);
        try
        {
            uint status;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                status = NativeMethods.NtQuerySystemInformation(
                    NativeMethods.SystemProcessInformation, buffer, len, out int needed);

                if (status == NativeMethods.STATUS_SUCCESS) return Parse(buffer, len);

                if (status == NativeMethods.STATUS_INFO_LENGTH_MISMATCH || status == NativeMethods.STATUS_BUFFER_TOO_SMALL)
                {
                    Marshal.FreeHGlobal(buffer);
                    len = Math.Max(needed + 64 * 1024, len * 2);
                    buffer = Marshal.AllocHGlobal(len);
                    continue;
                }
                break; // any other NTSTATUS -> fall back
            }
            _nativeFailedOnce = true;
            return ReadViaProcess();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static List<RawProc> Parse(IntPtr buffer, int bufferLength)
    {
        var list = new List<RawProc>(256);
        long baseAddr = buffer.ToInt64();
        int offset = 0;
        const int entryFixedSize = 0x100; // fields we read live below this; guard against a corrupt chain

        for (int guard = 0; guard < 100_000; guard++)
        {
            if (offset < 0 || offset + entryFixedSize > bufferLength) break;
            IntPtr entry = new(baseAddr + offset);

            int next = Marshal.ReadInt32(entry, OFF_NextEntryOffset);
            int threads = Marshal.ReadInt32(entry, OFF_NumberOfThreads);
            long createTime = Marshal.ReadInt64(entry, OFF_CreateTime);
            long userTime = Marshal.ReadInt64(entry, OFF_UserTime);
            long kernelTime = Marshal.ReadInt64(entry, OFF_KernelTime);
            short nameLen = Marshal.ReadInt16(entry, OFF_ImageNameLength);
            IntPtr namePtr = Marshal.ReadIntPtr(entry, OFF_ImageNameBuffer);
            int pid = (int)Marshal.ReadIntPtr(entry, OFF_UniqueProcessId).ToInt64();
            int handles = Marshal.ReadInt32(entry, OFF_HandleCount);
            uint faults = unchecked((uint)Marshal.ReadInt32(entry, OFF_PageFaultCount));
            long ws = Marshal.ReadInt64(entry, OFF_WorkingSetSize);
            long priv = Marshal.ReadInt64(entry, OFF_PrivatePageCount);
            long readBytes = Marshal.ReadInt64(entry, OFF_ReadTransferCount);
            long writeBytes = Marshal.ReadInt64(entry, OFF_WriteTransferCount);

            string name = nameLen > 0 && namePtr != IntPtr.Zero
                ? Marshal.PtrToStringUni(namePtr, nameLen / 2) ?? ""
                : pid == 0 ? "Idle" : pid == 4 ? "System" : $"pid_{pid}";

            list.Add(new RawProc(
                Pid: pid,
                Name: name,
                ThreadCount: threads,
                HandleCount: handles,
                CreateTime100ns: createTime,
                UserTime100ns: userTime,
                KernelTime100ns: kernelTime,
                CpuTime100ns: userTime + kernelTime,
                WorkingSet: ws,
                PrivateBytes: priv,
                PageFaultCount: faults,
                ReadBytes: readBytes,
                WriteBytes: writeBytes));

            if (next == 0) break;
            offset += next;
        }
        return list;
    }

    private List<RawProc> ReadViaProcess()
    {
        var list = new List<RawProc>(256);
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    long user = 0, kernel = 0, create = 0;
                    try { user = (long)(p.UserProcessorTime.TotalMilliseconds * 10_000.0); } catch { }
                    try { kernel = (long)(p.PrivilegedProcessorTime.TotalMilliseconds * 10_000.0); } catch { }
                    try { create = p.StartTime.ToUniversalTime().ToFileTimeUtc(); } catch { }

                    list.Add(new RawProc(
                        Pid: p.Id,
                        Name: SafeName(p),
                        ThreadCount: SafeThreadCount(p),
                        HandleCount: SafeHandleCount(p),
                        CreateTime100ns: create,
                        UserTime100ns: user,
                        KernelTime100ns: kernel,
                        CpuTime100ns: user + kernel,
                        WorkingSet: SafeLong(() => p.WorkingSet64),
                        PrivateBytes: SafeLong(() => p.PrivateMemorySize64),
                        PageFaultCount: 0,
                        ReadBytes: 0,
                        WriteBytes: 0));
                }
                catch
                {
                    // Process exited between enumeration and inspection.
                }
            }
        }
        return list;
    }

    private static string SafeName(System.Diagnostics.Process p)
    {
        try { return p.ProcessName; } catch { return $"pid_{p.Id}"; }
    }

    private static int SafeThreadCount(System.Diagnostics.Process p)
    {
        try { return p.Threads.Count; } catch { return 0; }
    }

    private static int SafeHandleCount(System.Diagnostics.Process p)
    {
        try { return p.HandleCount; } catch { return 0; }
    }

    private static long SafeLong(Func<long> f)
    {
        try { return f(); } catch { return 0; }
    }

    private readonly record struct PrevSample(
        long CpuTime100ns, long ReadBytes, long WriteBytes, uint PageFaultCount, long Qpc, double CpuPercent);

    private readonly record struct RawProc(
        int Pid, string Name, int ThreadCount, int HandleCount,
        long CreateTime100ns, long UserTime100ns, long KernelTime100ns, long CpuTime100ns,
        long WorkingSet, long PrivateBytes, uint PageFaultCount, long ReadBytes, long WriteBytes);

    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
