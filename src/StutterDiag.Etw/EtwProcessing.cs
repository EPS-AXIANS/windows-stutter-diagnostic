using System;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;

namespace StutterDiag.Etw;

// ---------------------------------------------------------------------------
// Observation structs. Small, immutable, allocation-free to move around. Every
// timestamp is already on the shared QPC axis (see EtwTimebase).
// ---------------------------------------------------------------------------

/// <summary>A single executed DPC or ISR, with its owning driver already resolved.</summary>
public readonly record struct DpcIsrObservation(
    long TimestampQpc, bool IsInterrupt, ulong Routine, string Driver, string? DriverVersion,
    double DurationMs, int Cpu);

/// <summary>A thread becoming runnable (Dispatcher/ReadyThread). Needs high-resolution keywords.</summary>
public readonly record struct ReadyThreadObservation(
    long TimestampQpc, int AwakenedThreadId, int AwakenedProcessId, int Cpu);

/// <summary>A CPU switching to a new thread (CSwitch). Needs high-resolution keywords.</summary>
public readonly record struct ContextSwitchObservation(
    long TimestampQpc, int NewThreadId, int NewProcessId, int NewThreadPriority, int Cpu);

/// <summary>A completed physical disk I/O with measured service time.</summary>
public readonly record struct DiskIoObservation(
    long TimestampQpc, bool IsWrite, string FileName, int ProcessId, string ProcessName,
    long TransferBytes, double LatencyMs, int DiskNumber);

/// <summary>A hard page fault (backed by disk), attributed to a process.</summary>
public readonly record struct HardFaultObservation(
    long TimestampQpc, string FileName, int ProcessId, string ProcessName, long ByteCount, double ElapsedMs);

/// <summary>
/// The kernel event stream the Monitors project consumes. Implemented by <see cref="EtwProcessing"/>;
/// injected into detectors so they never touch <c>TraceEvent</c> directly.
/// </summary>
public interface IEtwKernelStream
{
    MonitorHealth Health { get; }
    bool HighResolutionActive { get; }

    /// <summary>Records the OS/channel dropped since start (channel is drop-oldest when full).</summary>
    long DroppedRecordCount { get; }

    long ProcessedRecordCount { get; }

    event EventHandler<MonitorHealth>? HealthChanged;

    event EventHandler<DpcIsrObservation>? DpcIsrObserved;
    event EventHandler<ReadyThreadObservation>? ReadyThreadObserved;
    event EventHandler<ContextSwitchObservation>? ContextSwitchObserved;
    event EventHandler<DiskIoObservation>? DiskIoObserved;
    event EventHandler<HardFaultObservation>? HardFaultObserved;
}

/// <summary>
/// Converts a <c>TraceEvent</c> timestamp onto the application's QPC axis.
/// </summary>
/// <remarks>
/// TraceEvent's raw hardware counter (<c>TraceEvent.TimeStampQPC</c>) is the SAME counter as
/// <see cref="QpcClock.GetTimestamp"/> / <c>Stopwatch.GetTimestamp()</c> on Windows, so the
/// exact mapping is simply <c>return e.TimeStampQPC;</c>. Because that member's visibility is
/// not guaranteed across TraceEvent versions, we instead anchor once to the first event's
/// <c>TimeStampRelativeMSec</c> and add elapsed-since-anchor from then on: lock-free after the
/// first call, and accurate to a fixed sub-millisecond offset that does not affect windowing.
/// </remarks>
public sealed class EtwTimebase
{
    private readonly QpcClock _clock;
    private readonly object _gate = new();
    private volatile bool _anchored;
    private long _anchorQpc;
    private double _anchorRelMSec;

    public EtwTimebase(QpcClock clock) => _clock = clock;

    public long ToQpc(double relativeMSec)
    {
        if (!_anchored)
        {
            lock (_gate)
            {
                if (!_anchored)
                {
                    _anchorQpc = _clock.GetTimestamp();
                    _anchorRelMSec = relativeMSec;
                    _anchored = true;
                }
            }
        }
        return _anchorQpc + _clock.MsToTicks(relativeMSec - _anchorRelMSec);
    }

    public long ToQpc(TraceEvent e) => ToQpc(e.TimeStampRelativeMSec);
}

/// <summary>
/// The shared ETW consumer. Runs <c>Source.Process()</c> for the kernel session (and,
/// optionally, the user session) on dedicated threads. The hot per-event callbacks do no
/// allocation: they parse into an <see cref="EtwRecord"/> value and hand it to a bounded,
/// drop-oldest <see cref="Channel{T}"/>. A single worker task drains the channel, resolves
/// driver names off the hot path and raises the CLR events on <see cref="IEtwKernelStream"/>.
/// </summary>
public sealed class EtwProcessing : IEtwKernelStream, IAsyncDisposable
{
    private readonly EtwKernelSession _kernel;
    private readonly EtwUserSession? _user;
    private readonly ModuleResolver _resolver;
    private readonly EtwTimebase _timebase;
    private readonly Channel<EtwRecord> _channel;

    private CancellationTokenSource? _cts;
    private Task? _worker;
    private Thread? _kernelThread;
    private Thread? _userThread;

    private long _dropped;
    private long _processed;

    public EtwProcessing(
        EtwKernelSession kernel,
        ModuleResolver resolver,
        QpcClock clock,
        EtwUserSession? user = null,
        int channelCapacity = 1 << 16)
    {
        _kernel = kernel;
        _user = user;
        _resolver = resolver;
        _timebase = new EtwTimebase(clock);

        _channel = Channel.CreateBounded<EtwRecord>(
            new BoundedChannelOptions(channelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,   // kernel + user pump threads both write
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false,
            },
            // .NET 7+ overload: called for each item discarded when the channel is full.
            _ => Interlocked.Increment(ref _dropped));

        _kernel.HealthChanged += (_, h) => HealthChanged?.Invoke(this, h);
        _kernel.SessionRecreated += (_, _) => Rebind();
    }

    public MonitorHealth Health => _kernel.Health;
    public bool HighResolutionActive => _kernel.HighResolutionActive;

    /// <summary>
    /// The QPC-axis converter this consumer anchors. Share it with user-session monitors
    /// (Frametime, GPU packet-gap) so every ETW source maps onto exactly the same axis offset.
    /// </summary>
    public EtwTimebase Timebase => _timebase;
    public long DroppedRecordCount => Interlocked.Read(ref _dropped);
    public long ProcessedRecordCount => Interlocked.Read(ref _processed);

    public event EventHandler<MonitorHealth>? HealthChanged;
    public event EventHandler<DpcIsrObservation>? DpcIsrObserved;
    public event EventHandler<ReadyThreadObservation>? ReadyThreadObserved;
    public event EventHandler<ContextSwitchObservation>? ContextSwitchObserved;
    public event EventHandler<DiskIoObservation>? DiskIoObserved;
    public event EventHandler<HardFaultObservation>? HardFaultObserved;

    public Task StartAsync(CancellationToken ct)
    {
        if (!_kernel.IsAvailable && _user is not { IsAvailable: true })
            return Task.CompletedTask;   // Health already reports Unavailable.

        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => WorkerLoopAsync(_cts.Token), CancellationToken.None);

        if (_kernel.IsAvailable)
        {
            Bind();
            _kernelThread = StartPump("SD-ETW-Kernel", () => _kernel.Session?.Source.Process());
        }

        if (_user is { IsAvailable: true })
        {
            // User-session dynamic-provider callbacks are registered by the individual monitors
            // (Frametime, GpuPacketGap, StorPort disk) on _user.Session.Source.Dynamic before this.
            _userThread = StartPump("SD-ETW-User", () => _user!.Session?.Source.Process());
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        try { _kernel.Session?.Source.StopProcessing(); } catch { /* already stopped */ }
        try { _user?.Session?.Source.StopProcessing(); } catch { /* already stopped */ }

        _channel.Writer.TryComplete();

        if (_worker is not null)
        {
            try { await _worker.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch { /* timeout / cancel */ }
        }

        _kernelThread?.Join(2000);
        _userThread?.Join(2000);
        _cts?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _cts?.Dispose();
    }

    // --- binding -----------------------------------------------------------

    private void Bind()
    {
        var k = _kernel.Session?.Source.Kernel;
        if (k is null) return;

        // NOTE(build): confirm these KernelTraceEventParser event names against TraceEvent 3.1.16:
        //   PerfInfoDPC (DPCTraceData), PerfInfoISR (ISRTraceData), ThreadCSwitch (CSwitchTraceData),
        //   DispatcherReadyThread (DispatcherReadyThreadTraceData), ImageLoad/ImageDCStart/ImageUnload
        //   (ImageLoadTraceData), DiskIORead/DiskIOWrite (DiskIOTraceData),
        //   MemoryHardFault (MemoryHardFaultTraceData).
        k.PerfInfoDPC += OnDpc;
        k.PerfInfoISR += OnIsr;
        k.ThreadCSwitch += OnCSwitch;
        k.DispatcherReadyThread += OnReadyThread;
        k.ImageLoad += OnImage;
        k.ImageDCStart += OnImage;
        k.ImageUnload += OnImageUnload;
        k.DiskIORead += OnDiskRead;
        k.DiskIOWrite += OnDiskWrite;
        k.MemoryHardFault += OnHardFault;
    }

    /// <summary>Re-bind parser callbacks and restart the kernel pump after a session recreate.</summary>
    private void Rebind()
    {
        try { _kernelThread?.Join(2000); } catch { /* ignore */ }
        Bind();
        _kernelThread = StartPump("SD-ETW-Kernel", () => _kernel.Session?.Source.Process());
    }

    private static Thread StartPump(string name, Action body)
    {
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception) { /* session stopped / disposed — expected on shutdown */ }
        })
        {
            IsBackground = true,
            Name = name,
        };
        t.Start();
        return t;
    }

    // --- hot callbacks (NO allocation for DPC/ISR/CSwitch/ReadyThread) -----

    private void OnDpc(DPCTraceData d) => _channel.Writer.TryWrite(new EtwRecord(
        EtwRecordKind.Dpc, _timebase.ToQpc(d.TimeStampRelativeMSec),
        unchecked((ulong)d.Routine), d.ElapsedTimeMSec, 0, 0, 0, d.ProcessorNumber, 0, null, null));

    private void OnIsr(ISRTraceData d) => _channel.Writer.TryWrite(new EtwRecord(
        EtwRecordKind.Isr, _timebase.ToQpc(d.TimeStampRelativeMSec),
        unchecked((ulong)d.Routine), d.ElapsedTimeMSec, 0, 0, 0, d.ProcessorNumber, 0, null, null));

    private void OnCSwitch(CSwitchTraceData d) => _channel.Writer.TryWrite(new EtwRecord(
        EtwRecordKind.ContextSwitch, _timebase.ToQpc(d.TimeStampRelativeMSec),
        0, 0, 0, d.NewThreadID, d.NewProcessID, d.ProcessorNumber, d.NewThreadPriority, null, null));

    private void OnReadyThread(DispatcherReadyThreadTraceData d) => _channel.Writer.TryWrite(new EtwRecord(
        EtwRecordKind.ReadyThread, _timebase.ToQpc(d.TimeStampRelativeMSec),
        0, 0, 0, d.AwakenedThreadID, d.AwakenedProcessID, d.ProcessorNumber, 0, null, null));

    // Disk / hard-fault carry a file name; the FileName/ProcessName getters allocate a string.
    // Accepted: these events are orders of magnitude rarer than DPC/ISR (LIMITATIONS.md §3).
    private void OnDiskRead(DiskIOTraceData d) => WriteDisk(d, isWrite: false);
    private void OnDiskWrite(DiskIOTraceData d) => WriteDisk(d, isWrite: true);

    private void WriteDisk(DiskIOTraceData d, bool isWrite) => _channel.Writer.TryWrite(new EtwRecord(
        isWrite ? EtwRecordKind.DiskWrite : EtwRecordKind.DiskRead,
        _timebase.ToQpc(d.TimeStampRelativeMSec),
        0, d.ElapsedTimeMSec, d.TransferSize, d.ProcessID, 0, 0, d.DiskNumber, d.FileName, d.ProcessName));

    private void OnHardFault(MemoryHardFaultTraceData d) => _channel.Writer.TryWrite(new EtwRecord(
        EtwRecordKind.HardFault, _timebase.ToQpc(d.TimeStampRelativeMSec),
        0, d.ElapsedTimeMSec, d.ByteCount, d.ProcessID, 0, 0, 0, d.FileName, d.ProcessName));

    private void OnImage(ImageLoadTraceData d)
        => _resolver.AddImage(d.ImageBase, d.ImageSize, d.FileName, d.ProcessID);

    private void OnImageUnload(ImageLoadTraceData d)
        => _resolver.RemoveImage(d.ImageBase, d.ProcessID);

    // --- worker (allowed to allocate; off the ETW callback thread) --------

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var r in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _processed);
                switch (r.Kind)
                {
                    case EtwRecordKind.Dpc:
                    case EtwRecordKind.Isr:
                    {
                        var mod = _resolver.Resolve(r.Addr);
                        DpcIsrObserved?.Invoke(this, new DpcIsrObservation(
                            r.Qpc, r.Kind == EtwRecordKind.Isr, r.Addr, mod.Name, mod.Version, r.D1, r.I3));
                        break;
                    }
                    case EtwRecordKind.ReadyThread:
                        ReadyThreadObserved?.Invoke(this, new ReadyThreadObservation(r.Qpc, r.I1, r.I2, r.I3));
                        break;
                    case EtwRecordKind.ContextSwitch:
                        ContextSwitchObserved?.Invoke(this, new ContextSwitchObservation(r.Qpc, r.I1, r.I2, r.I4, r.I3));
                        break;
                    case EtwRecordKind.DiskRead:
                    case EtwRecordKind.DiskWrite:
                        DiskIoObserved?.Invoke(this, new DiskIoObservation(
                            r.Qpc, r.Kind == EtwRecordKind.DiskWrite, r.S1 ?? "", r.I1, r.S2 ?? "",
                            r.L1, r.D1, r.I4));
                        break;
                    case EtwRecordKind.HardFault:
                        HardFaultObserved?.Invoke(this, new HardFaultObservation(
                            r.Qpc, r.S1 ?? "", r.I1, r.S2 ?? "", r.L1, r.D1));
                        break;
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    // --- channel payload -------------------------------------------------

    internal enum EtwRecordKind : byte { Dpc, Isr, ReadyThread, ContextSwitch, DiskRead, DiskWrite, HardFault }

    /// <summary>Flat value carried through the bounded channel. One shape for every event kind.</summary>
    internal readonly struct EtwRecord
    {
        public readonly EtwRecordKind Kind;
        public readonly long Qpc;
        public readonly ulong Addr;   // routine address
        public readonly double D1;    // duration / latency ms
        public readonly long L1;      // bytes
        public readonly int I1;       // pid / awakened tid / new tid
        public readonly int I2;       // awakened pid / new pid
        public readonly int I3;       // cpu
        public readonly int I4;       // priority / disk number
        public readonly string? S1;   // file name
        public readonly string? S2;   // process name

        public EtwRecord(EtwRecordKind kind, long qpc, ulong addr, double d1, long l1,
            int i1, int i2, int i3, int i4, string? s1, string? s2)
        {
            Kind = kind; Qpc = qpc; Addr = addr; D1 = d1; L1 = l1;
            I1 = i1; I2 = i2; I3 = i3; I4 = i4; S1 = s1; S2 = s2;
        }
    }
}
