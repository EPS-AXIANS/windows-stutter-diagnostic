using FluentAssertions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Monitors.Stutter;
using Xunit;

namespace StutterDiag.Monitors.Tests;

/// <summary>
/// Drives <see cref="HeartbeatStutterDetector"/> through its real test seam: the public nested
/// <see cref="HeartbeatStutterDetector.IProbeClock"/> plus the <c>internal</c>
/// <see cref="HeartbeatStutterDetector.Measure"/> (InternalsVisibleTo is set in
/// StutterDiag.Monitors.csproj). The probe loop / thread pool / <c>timeBeginPeriod</c> are never
/// started, so the test is fully deterministic.
/// </summary>
public sealed class HeartbeatStutterDetectorTests
{
    /// <summary>A probe clock whose "sleep" advances virtual time by a fixed real amount.</summary>
    private sealed class FakeProbeClock : HeartbeatStutterDetector.IProbeClock
    {
        private readonly double _actualWakeMs;
        private long _now;

        public FakeProbeClock(double actualWakeMs) => _actualWakeMs = actualWakeMs;

        public long Frequency => 1_000;              // 1 tick == 1 ms
        public long GetTimestamp() => _now;
        public void SleepMs(int ms) => _now += (long)Math.Round(_actualWakeMs);
    }

    private static HeartbeatStutterDetector New(FakeProbeClock probe, out List<MetricSample> samples)
    {
        var opt = new HeartbeatOptions();          // ProbeIntervalMs = 10, MinReportMs = 40
        var thresholds = new StutterThresholdOptions();
        var detector = new HeartbeatStutterDetector(opt, thresholds, new QpcClock(), probe);

        var captured = new List<MetricSample>();
        detector.SampleCaptured += (_, s) => captured.Add(s);
        samples = captured;
        return detector;
    }

    [Fact]
    public void A_wake_delay_at_or_above_MinReportMs_produces_exactly_one_candidate()
    {
        const int interval = 10;
        const double wake = 70.0; // >= MinReportMs (40); overshoot = 60 ms
        var detector = New(new FakeProbeClock(wake), out _);

        GC.Collect(); // settle ambient gen-0 pressure before the GC-delta window in Measure
        bool isCandidate = detector.Measure(probeId: 0, intervalMs: interval, out var candidate);

        isCandidate.Should().BeTrue();
        candidate.Should().NotBeNull();
        candidate!.Detector.Should().Be(DetectorKind.Heartbeat);
        candidate.EstimatedDurationMs.Should().BeApproximately(wake - interval, 0.5);
        candidate.TimestampQpc.Should().Be((long)wake); // virtual clock after the sleep
    }

    [Fact]
    public void A_sub_threshold_wake_delay_produces_no_candidate()
    {
        var detector = New(new FakeProbeClock(actualWakeMs: 20.0), out _); // < MinReportMs (40)

        GC.Collect();
        bool isCandidate = detector.Measure(probeId: 0, intervalMs: 10, out var candidate);

        isCandidate.Should().BeFalse();
        candidate.Should().BeNull();
    }

    [Fact]
    public void Every_probe_emits_a_sched_wakedelay_ms_sample()
    {
        var detector = New(new FakeProbeClock(actualWakeMs: 12.0), out var samples);

        GC.Collect();
        detector.Measure(probeId: 2, intervalMs: 10, out _);

        samples.Should().ContainSingle();
        samples[0].Metric.Should().Be("sched.wakedelay.ms");
        samples[0].Value.Should().BeApproximately(12.0, 0.5);
        samples[0].Instance.Should().StartWith("2"); // "<probeId>", or "<probeId>:gc" when a GC overlapped
    }

    [Fact]
    public void The_detector_reports_its_kind_and_that_it_has_no_high_resolution_mode()
    {
        var detector = New(new FakeProbeClock(0), out _);
        detector.Kind.Should().Be(DetectorKind.Heartbeat);
        detector.SupportsHighResolutionMode.Should().BeFalse();
        detector.Name.Should().Be("Heartbeat");
    }
}
