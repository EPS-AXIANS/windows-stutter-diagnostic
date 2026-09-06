namespace StutterDiag.Core.Config;

/// <summary>Clamps out-of-range configuration values and returns human-readable warnings.</summary>
public static class ConfigValidator
{
    public static IReadOnlyList<string> Validate(AppConfig c)
    {
        var w = new List<string>();

        Clamp(ref c.StutterThresholds.MicroStutterMs, 5, 5000, "StutterThresholds.MicroStutterMs", w);
        Clamp(ref c.StutterThresholds.MajorMs, 10, 10000, "StutterThresholds.MajorMs", w);
        Clamp(ref c.StutterThresholds.SevereMs, 20, 20000, "StutterThresholds.SevereMs", w);
        Clamp(ref c.StutterThresholds.CriticalMs, 50, 60000, "StutterThresholds.CriticalMs", w);

        if (!(c.StutterThresholds.MicroStutterMs < c.StutterThresholds.MajorMs
              && c.StutterThresholds.MajorMs < c.StutterThresholds.SevereMs
              && c.StutterThresholds.SevereMs < c.StutterThresholds.CriticalMs))
        {
            w.Add("StutterThresholds must be strictly increasing (micro < major < severe < critical); reverting to defaults.");
            c.StutterThresholds = new StutterThresholdOptions();
        }

        Clamp(ref c.Correlation.PreRollSeconds, 0.5, 60, "Correlation.PreRollSeconds", w);
        Clamp(ref c.Correlation.PostRollSeconds, 0.5, 60, "Correlation.PostRollSeconds", w);
        Clamp(ref c.Correlation.HighProximityMs, 1, 1000, "Correlation.HighProximityMs", w);
        Clamp(ref c.Correlation.MediumProximityMs, 10, 5000, "Correlation.MediumProximityMs", w);
        Clamp(ref c.Correlation.BaselineWindowMinutes, 1, 240, "Correlation.BaselineWindowMinutes", w);
        Clamp(ref c.Correlation.MadK, 1.0, 20.0, "Correlation.MadK", w);
        Clamp(ref c.Correlation.BaseRateWindowSeconds, 1, 120, "Correlation.BaseRateWindowSeconds", w);

        ClampInt(ref c.Heartbeat.ProbeIntervalMs, 1, 100, "Heartbeat.ProbeIntervalMs", w);
        ClampInt(ref c.Heartbeat.ProbeCount, 1, 8, "Heartbeat.ProbeCount", w);
        ClampInt(ref c.Heartbeat.TimerResolutionMs, 1, 16, "Heartbeat.TimerResolutionMs", w);
        ClampInt(ref c.Heartbeat.AggregateWindowMs, 1, 200, "Heartbeat.AggregateWindowMs", w);
        Clamp(ref c.Heartbeat.MinReportMs, 5, 2000, "Heartbeat.MinReportMs", w);

        Clamp(ref c.HighRes.WindowSeconds, 1, 120, "HighRes.WindowSeconds", w);
        Clamp(ref c.HighRes.RingBufferSeconds, 10, 600, "HighRes.RingBufferSeconds", w);

        ClampInt(ref c.Etw.BufferSizeKb, 32, 1024, "Etw.BufferSizeKb", w);
        ClampInt(ref c.Etw.BufferCount, 4, 512, "Etw.BufferCount", w);
        Clamp(ref c.Etw.FlushSeconds, 0.25, 10, "Etw.FlushSeconds", w);

        Clamp(ref c.Sampling.PerfCounterHz, 0.2, 10, "Sampling.PerfCounterHz", w);
        Clamp(ref c.Sampling.ProcessSnapshotSeconds, 0.5, 60, "Sampling.ProcessSnapshotSeconds", w);
        ClampInt(ref c.Sampling.TopProcessCount, 3, 200, "Sampling.TopProcessCount", w);

        ClampInt(ref c.Retention.Days, 1, 3650, "Retention.Days", w);
        ClampInt(ref c.Retention.MaxDbSizeMb, 64, 262144, "Retention.MaxDbSizeMb", w);

        if (string.IsNullOrWhiteSpace(c.Service.IpcPipeName))
        {
            w.Add("Service.IpcPipeName was empty; reverting to 'StutterDiag.Service'.");
            c.Service.IpcPipeName = "StutterDiag.Service";
        }

        return w;
    }

    private static void Clamp(ref double v, double lo, double hi, string name, List<string> w)
    {
        if (v < lo) { w.Add($"{name} {v} < {lo}; clamped."); v = lo; }
        else if (v > hi) { w.Add($"{name} {v} > {hi}; clamped."); v = hi; }
    }

    private static void ClampInt(ref int v, int lo, int hi, string name, List<string> w)
    {
        if (v < lo) { w.Add($"{name} {v} < {lo}; clamped."); v = lo; }
        else if (v > hi) { w.Add($"{name} {v} > {hi}; clamped."); v = hi; }
    }
}
