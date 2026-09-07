namespace StutterDiag.Core.Config;

/// <summary>Clamps out-of-range configuration values and returns human-readable warnings.</summary>
public static class ConfigValidator
{
    public static IReadOnlyList<string> Validate(AppConfig c)
    {
        var w = new List<string>();

        c.StutterThresholds.MicroStutterMs = Clamp(c.StutterThresholds.MicroStutterMs, 5, 5000, "StutterThresholds.MicroStutterMs", w);
        c.StutterThresholds.MajorMs = Clamp(c.StutterThresholds.MajorMs, 10, 10000, "StutterThresholds.MajorMs", w);
        c.StutterThresholds.SevereMs = Clamp(c.StutterThresholds.SevereMs, 20, 20000, "StutterThresholds.SevereMs", w);
        c.StutterThresholds.CriticalMs = Clamp(c.StutterThresholds.CriticalMs, 50, 60000, "StutterThresholds.CriticalMs", w);

        if (!(c.StutterThresholds.MicroStutterMs < c.StutterThresholds.MajorMs
              && c.StutterThresholds.MajorMs < c.StutterThresholds.SevereMs
              && c.StutterThresholds.SevereMs < c.StutterThresholds.CriticalMs))
        {
            w.Add("StutterThresholds must be strictly increasing (micro < major < severe < critical); reverting to defaults.");
            c.StutterThresholds = new StutterThresholdOptions();
        }

        c.Correlation.PreRollSeconds = Clamp(c.Correlation.PreRollSeconds, 0.5, 60, "Correlation.PreRollSeconds", w);
        c.Correlation.PostRollSeconds = Clamp(c.Correlation.PostRollSeconds, 0.5, 60, "Correlation.PostRollSeconds", w);
        c.Correlation.HighProximityMs = Clamp(c.Correlation.HighProximityMs, 1, 1000, "Correlation.HighProximityMs", w);
        c.Correlation.MediumProximityMs = Clamp(c.Correlation.MediumProximityMs, 10, 5000, "Correlation.MediumProximityMs", w);
        c.Correlation.BaselineWindowMinutes = Clamp(c.Correlation.BaselineWindowMinutes, 1, 240, "Correlation.BaselineWindowMinutes", w);
        c.Correlation.MadK = Clamp(c.Correlation.MadK, 1.0, 20.0, "Correlation.MadK", w);
        c.Correlation.BaseRateWindowSeconds = Clamp(c.Correlation.BaseRateWindowSeconds, 1, 120, "Correlation.BaseRateWindowSeconds", w);

        c.Heartbeat.ProbeIntervalMs = ClampInt(c.Heartbeat.ProbeIntervalMs, 1, 100, "Heartbeat.ProbeIntervalMs", w);
        c.Heartbeat.ProbeCount = ClampInt(c.Heartbeat.ProbeCount, 1, 8, "Heartbeat.ProbeCount", w);
        c.Heartbeat.TimerResolutionMs = ClampInt(c.Heartbeat.TimerResolutionMs, 1, 16, "Heartbeat.TimerResolutionMs", w);
        c.Heartbeat.AggregateWindowMs = ClampInt(c.Heartbeat.AggregateWindowMs, 1, 200, "Heartbeat.AggregateWindowMs", w);
        c.Heartbeat.MinReportMs = Clamp(c.Heartbeat.MinReportMs, 5, 2000, "Heartbeat.MinReportMs", w);

        c.HighRes.WindowSeconds = Clamp(c.HighRes.WindowSeconds, 1, 120, "HighRes.WindowSeconds", w);
        c.HighRes.RingBufferSeconds = Clamp(c.HighRes.RingBufferSeconds, 10, 600, "HighRes.RingBufferSeconds", w);

        c.Etw.BufferSizeKb = ClampInt(c.Etw.BufferSizeKb, 32, 1024, "Etw.BufferSizeKb", w);
        c.Etw.BufferCount = ClampInt(c.Etw.BufferCount, 4, 512, "Etw.BufferCount", w);
        c.Etw.FlushSeconds = Clamp(c.Etw.FlushSeconds, 0.25, 10, "Etw.FlushSeconds", w);

        c.Sampling.PerfCounterHz = Clamp(c.Sampling.PerfCounterHz, 0.2, 10, "Sampling.PerfCounterHz", w);
        c.Sampling.ProcessSnapshotSeconds = Clamp(c.Sampling.ProcessSnapshotSeconds, 0.5, 60, "Sampling.ProcessSnapshotSeconds", w);
        c.Sampling.TopProcessCount = ClampInt(c.Sampling.TopProcessCount, 3, 200, "Sampling.TopProcessCount", w);

        c.Retention.Days = ClampInt(c.Retention.Days, 1, 3650, "Retention.Days", w);
        c.Retention.MaxDbSizeMb = ClampInt(c.Retention.MaxDbSizeMb, 64, 262144, "Retention.MaxDbSizeMb", w);

        if (string.IsNullOrWhiteSpace(c.Service.IpcPipeName))
        {
            w.Add("Service.IpcPipeName was empty; reverting to 'StutterDiag.Service'.");
            c.Service.IpcPipeName = "StutterDiag.Service";
        }

        return w;
    }

    // Config members are properties, so these return the clamped value instead of taking `ref`.
    private static double Clamp(double v, double lo, double hi, string name, List<string> w)
    {
        if (v < lo) { w.Add($"{name} {v} < {lo}; clamped."); return lo; }
        if (v > hi) { w.Add($"{name} {v} > {hi}; clamped."); return hi; }
        return v;
    }

    private static int ClampInt(int v, int lo, int hi, string name, List<string> w)
    {
        if (v < lo) { w.Add($"{name} {v} < {lo}; clamped."); return lo; }
        if (v > hi) { w.Add($"{name} {v} > {hi}; clamped."); return hi; }
        return v;
    }
}
