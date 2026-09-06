namespace StutterDiag.Core.Model;

/// <summary>
/// One numeric time-series reading. <paramref name="Metric"/> uses a dotted convention:
/// <list type="bullet">
///   <item><c>cpu.total.pct</c>, <c>cpu.core.pct</c>, <c>cpu.freq.mhz</c>, <c>cpu.freq.effective.mhz</c></item>
///   <item><c>cpu.cstate.c1.pct</c>, <c>cpu.parked</c></item>
///   <item><c>gpu.engine.pct</c>, <c>gpu.vram.usedmb</c>, <c>gpu.freq.mhz</c> (vendor SDK, else absent)</item>
///   <item><c>disk.read.latency.ms</c>, <c>disk.write.latency.ms</c>, <c>disk.queue</c>, <c>disk.iops</c>, <c>disk.bytespersec</c></item>
///   <item><c>mem.available.mb</c>, <c>mem.committed.mb</c>, <c>mem.commitlimit.mb</c>, <c>mem.hardfaults.persec</c>, <c>mem.pagefaults.persec</c></item>
///   <item><c>sched.wakedelay.ms</c> (heartbeat), <c>dpc.total.ms</c>, <c>isr.total.ms</c></item>
/// </list>
/// <paramref name="Instance"/> is <c>""</c> for system-wide, otherwise a core index, device id or engine name.
/// </summary>
public sealed record MetricSample(long TimestampQpc, string Metric, string Instance, double Value);
