using StutterDiag.Core.Correlation;
using StutterDiag.Core.Diagnostics;
using StutterDiag.Core.Model;
using StutterDiag.Reporting;

namespace StutterDiag.Reporting.Tests;

/// <summary>
/// Hand-builds a small but complete <see cref="ReportModel"/> / <see cref="SessionReport"/> tree
/// so the generators can be exercised without an <c>IEventStore</c>. Timestamps are fixed for
/// deterministic output.
/// </summary>
internal static class TestModel
{
    public const long Freq = 10_000_000;
    public static readonly DateTime GeneratedUtc = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Started = new(2026, 9, 6, 9, 0, 0, DateTimeKind.Utc);

    private sealed class FakeBaseRates : IBaseRateProvider
    {
        private readonly Dictionary<string, double> _r;
        public FakeBaseRates(Dictionary<string, double> r) => _r = r;
        public double GetBaseRate(string signalType) => _r.TryGetValue(signalType, out var v) ? v : 0.0;
    }

    public static IReadOnlyList<DiagnosticFinding> Findings()
    {
        var stutters = new[]
        {
            new Stutter { Id = 1 }, new Stutter { Id = 2 }, new Stutter { Id = 3 },
        };
        var correlations = new[]
        {
            new StutterCorrelation(1, SignalCatalog.TpmTbs, 5, CorrelationScore.High, 0.2, "overlapping the stutter interval"),
            new StutterCorrelation(2, SignalCatalog.TpmTbs, 30, CorrelationScore.Medium, 0.2, "as close as 30 ms"),
            new StutterCorrelation(1, SignalCatalog.GpuDriver, 120, CorrelationScore.Medium, 0.0, "GPU driver activity near the stutter"),
        };
        return new DiagnosticHypothesisEngine().Analyze(
            stutters, correlations,
            new FakeBaseRates(new Dictionary<string, double> { [SignalCatalog.TpmTbs] = 0.2 }),
            alwaysReport: new[] { SignalCatalog.Whea }); // WHEA never co-occurs -> a NO EVIDENCE finding
    }

    public static IReadOnlyList<CorrelationRankingRow> Ranking() => new[]
    {
        new CorrelationRankingRow(
            Rank: 1, SignalType: SignalCatalog.TpmTbs, DisplayName: SignalCatalog.DisplayName(SignalCatalog.TpmTbs),
            CorrelatedStutters: 2, TotalStutters: 3, Score: CorrelationScore.High,
            ScoreLabel: "[HIGH]", ScoreClass: "high", BaseRatePercent: 20.0, Lift: 3.3),
        new CorrelationRankingRow(
            Rank: 2, SignalType: SignalCatalog.GpuDriver, DisplayName: SignalCatalog.DisplayName(SignalCatalog.GpuDriver),
            CorrelatedStutters: 1, TotalStutters: 3, Score: CorrelationScore.Medium,
            ScoreLabel: "[MEDIUM]", ScoreClass: "medium", BaseRatePercent: 0.0, Lift: double.PositiveInfinity),
    };

    private static StutterDetail Detail(int index, long id, StutterSeverity severity, bool withTpm)
    {
        var possible = new List<PossibleCorrelation>();
        if (withTpm)
        {
            possible.Add(new PossibleCorrelation(
                Label: "[HIGH]", ScoreClass: "high", SignalType: SignalCatalog.TpmTbs,
                DisplayName: SignalCatalog.DisplayName(SignalCatalog.TpmTbs), Score: CorrelationScore.High,
                ProximityMs: 5, BaseRate: 0.2, Detail: "TPM / TBS events overlapping the stutter interval"));
        }

        return new StutterDetail
        {
            Id = id,
            Index = index,
            TimestampQpc = 1_000_000_000 + id * 5_000_000,
            TimestampUtc = Started.AddMinutes(index),
            DurationMs = 40 + index * 50,
            Severity = severity,
            Detector = DetectorKind.Heartbeat,
            Confidence = DetectionConfidence.Medium,
            CorroboratedBy = index == 1 ? new[] { DetectorKind.Heartbeat, DetectorKind.Frametime } : Array.Empty<DetectorKind>(),
            UserMarked = false,
            TpmEventCount = withTpm ? 1 : 0,
            CpuAvgPct = 30.0 + index,
            CpuPeakPct = 55.0 + index,
            GpuAvgPct = null,
            DiskLatencySpikeMs = index == 2 ? 14.0 : (double?)null,
            DpcPeakMs = index == 1 ? 3.1 : (double?)null,
            DpcPeakDriver = index == 1 ? "nvlddmkm.sys" : null,
            Whea = false,
            KernelPower = false,
            TopProcesses = new[] { new TopProcess("game.exe", 4321, 42.0, 1_048_576) },
            PossibleCorrelations = possible,
            WindowFromQpc = 1_000_000_000 + id * 5_000_000 - Freq,
            WindowToQpc = 1_000_000_000 + id * 5_000_000 + Freq,
        };
    }

    public static SessionReport Session(string label = "fTPM run")
    {
        var ranking = Ranking();
        var counts = new AggregateCounts(
            MonitoringDuration: TimeSpan.FromHours(3),
            StuttersTotal: 3,
            StuttersPerHour: 1.0,
            MicroStutters: 1, MajorStutters: 1, SevereStutters: 1, CriticalStutters: 0,
            MeanDurationMs: 140.0, P95DurationMs: 240.0,
            TpmTbsEvents: 4, WheaEvents: 0,
            DpcSpikeCount: 2, DistinctDriverAnomalies: 1,
            MeanCpuPct: 31.5, MeanGpuPct: 12.0, StorageLatencyP95Ms: 9.5,
            PowerStateChangeCount: 3,
            TopCorrelationSignals: ranking);

        var summary = new SummaryBlock(
            MonitoringDuration: "3h 0m", StuttersDetected: 3, MajorStutters: 2,
            TpmRelatedEvents: 4, WheaEvents: 0, DpcSpikes: 2, DriverAnomalies: 1);

        return new SessionReport
        {
            Session = new MonitoringSession
            {
                Id = label == "fTPM run" ? 11 : 12,
                StartedUtc = Started,
                EndedUtc = Started.AddHours(3),
                Label = label,
                TpmTypeInferred = TpmType.Firmware,
                TpmBasis = "manufacturer id 'AMD' is a CPU/SoC vendor; no discrete device on the LPC/SPI bus",
                Mode = "Standard",
            },
            Summary = summary,
            Counts = counts,
            CorrelationRanking = ranking,
            Stutters = new[]
            {
                Detail(1, 101, StutterSeverity.Micro, withTpm: true),
                Detail(2, 102, StutterSeverity.Severe, withTpm: false),
            },
            Findings = Findings(),
            Timeline = new[]
            {
                new TimelineEntry(1_000_000_000, Started.AddMinutes(1), TimelineBuilder.KindStutter, "Micro stutter 90.0 ms (Heartbeat)", 90.0, "Micro"),
                new TimelineEntry(1_010_000_000, Started.AddMinutes(2), TimelineBuilder.KindDpcSpike, "DPC peak 3.1 ms in nvlddmkm.sys (12 calls)", 3.1, "Warning"),
            },
            Whea = new[]
            {
                new WheaRow(Started.AddMinutes(90), 1_050_000_000, "Pcie", "Corrected", "A corrected PCIe error was recorded.", null),
            },
            Events = Array.Empty<MonitorEvent>(),
            Drivers = Array.Empty<DriverInfo>(),
            Health = new HealthSummary(Array.Empty<MonitorHealthRow>(), 0, "No lost-event counters were recorded; 0 is a lower bound."),
            System = null,
            SystemView = null,
        };
    }

    public static ReportModel Build(ReportFormat format = ReportFormat.Html, bool compare = false)
    {
        var primary = Session("fTPM run");
        SessionReport? secondary = compare ? Session("dTPM run") : null;
        SessionComparison? comparison = compare ? new SessionComparer().Compare(primary, secondary!) : null;

        return new ReportModel
        {
            Meta = new ReportMeta(
                GeneratedUtc: GeneratedUtc,
                ToolVersion: "0.1.0",
                Format: format,
                CompareMode: compare,
                QpcFrequency: Freq,
                Redaction: new RedactionOptions(),
                RedactionNotes: Array.Empty<string>()),
            Primary = primary,
            Secondary = secondary,
            Comparison = comparison,
        };
    }
}
