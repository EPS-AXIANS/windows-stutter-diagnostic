using StutterDiag.Core.Config;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;

namespace StutterDiag.Core.Correlation;

/// <summary>
/// Merges <see cref="StutterCandidate"/>s from the several detectors into one
/// <see cref="Stutter"/> per real hitch: candidates within <see cref="MergeWindowMs"/> of
/// each other collapse, the longest duration wins, severity comes from the configured
/// thresholds, and every distinct detector that saw it is listed in
/// <see cref="Stutter.CorroboratedBy"/>.
/// </summary>
public sealed class StutterAggregator
{
    public const double MergeWindowMs = 30.0;

    private readonly QpcClock _clock;
    private readonly StutterThresholdOptions _thresholds;
    private readonly long _mergeTicks;
    private readonly object _gate = new();

    private readonly List<StutterCandidate> _cluster = new();
    private long _clusterStartQpc;

    public StutterAggregator(QpcClock clock, StutterThresholdOptions thresholds)
    {
        _clock = clock;
        _thresholds = thresholds;
        _mergeTicks = clock.MsToTicks(MergeWindowMs);
    }

    /// <summary>Raised once a cluster is finalized. <c>SessionId</c>/<c>Id</c> are still 0.</summary>
    public event EventHandler<Stutter>? StutterAggregated;

    public void Add(StutterCandidate candidate)
    {
        Stutter? finalized = null;
        lock (_gate)
        {
            if (_cluster.Count == 0)
            {
                _cluster.Add(candidate);
                _clusterStartQpc = candidate.TimestampQpc;
            }
            else if (candidate.TimestampQpc - _clusterStartQpc <= _mergeTicks)
            {
                _cluster.Add(candidate);
            }
            else
            {
                finalized = Finalize();
                _cluster.Add(candidate);
                _clusterStartQpc = candidate.TimestampQpc;
            }
        }
        if (finalized is not null) StutterAggregated?.Invoke(this, finalized);
    }

    /// <summary>Call on a timer; finalizes a cluster once the merge window has elapsed with no new candidate.</summary>
    public void Flush(long nowQpc)
    {
        Stutter? finalized = null;
        lock (_gate)
        {
            if (_cluster.Count > 0 && nowQpc - _clusterStartQpc > _mergeTicks)
                finalized = Finalize();
        }
        if (finalized is not null) StutterAggregated?.Invoke(this, finalized);
    }

    private Stutter Finalize()
    {
        var lead = _cluster.MaxBy(c => c.EstimatedDurationMs)!;
        var detectors = _cluster.Select(c => c.Detector).Distinct().ToArray();
        bool userMarked = detectors.Contains(DetectorKind.UserMarked);

        // A lone low-confidence heartbeat hit with no corroboration stays low confidence.
        DetectionConfidence confidence = lead.Confidence;
        if (detectors.Length >= 2 && confidence < DetectionConfidence.Medium)
            confidence = DetectionConfidence.Medium;
        if (detectors.Length >= 3)
            confidence = DetectionConfidence.High;
        if (userMarked)
            confidence = DetectionConfidence.High;

        var stutter = new Stutter
        {
            TimestampQpc = lead.TimestampQpc,
            TimestampUtc = _clock.QpcToUtc(lead.TimestampQpc),
            DurationMs = lead.EstimatedDurationMs,
            Severity = Classify(lead.EstimatedDurationMs),
            Detector = lead.Detector,
            Confidence = confidence,
            CorroboratedBy = detectors,
            UserMarked = userMarked
        };

        _cluster.Clear();
        return stutter;
    }

    public StutterSeverity Classify(double durationMs)
    {
        if (durationMs >= _thresholds.CriticalMs) return StutterSeverity.Critical;
        if (durationMs >= _thresholds.SevereMs) return StutterSeverity.Severe;
        if (durationMs >= _thresholds.MajorMs) return StutterSeverity.Major;
        return StutterSeverity.Micro;
    }
}
