using System;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using StutterDiag.Core.Config;
using StutterDiag.Core.Model;

namespace StutterDiag.Etw;

/// <summary>Outcome of a runtime keyword change on a live kernel session.</summary>
public enum KeywordChangeResult
{
    /// <summary>Applied in place; the session kept running and subscriptions are intact.</summary>
    Applied,

    /// <summary>The API rejected an in-place change; caller must invoke <see cref="EtwKernelSession.Restart"/>.</summary>
    RestartRequired,

    /// <summary>No session (not elevated); nothing to do.</summary>
    Unavailable
}

/// <summary>
/// Owns the real-time NT kernel logger session (<c>StutterDiag-Kernel</c>). Enables the
/// always-on low-overhead keywords (DPC, ISR, image load, process/thread, hard faults,
/// disk I/O) and can layer the heavier high-resolution keyword set (context switch,
/// dispatcher/ready-thread, sampled profile) on and off around a trigger window.
/// </summary>
/// <remarks>
/// Requires administrator / <c>SeSystemProfilePrivilege</c>. If the session cannot be
/// created, <see cref="Health"/> becomes <see cref="HealthStatus.Unavailable"/> and every
/// method becomes a no-op — construction never throws.
/// </remarks>
public sealed class EtwKernelSession : IDisposable
{
    public const string SessionName = "StutterDiag-Kernel";

    // NOTE(build): the TraceEvent enum member for DPCs is spelled "DeferedProcedureCalls"
    //              (single 'r') in Microsoft.Diagnostics.Tracing.Parsers.KernelTraceEventParser.Keywords.
    /// <summary>Low-overhead keywords kept enabled for the whole session (typically &lt; 0.5% CPU).</summary>
    public const KernelTraceEventParser.Keywords AlwaysOnKeywords =
        KernelTraceEventParser.Keywords.Process
        | KernelTraceEventParser.Keywords.Thread
        | KernelTraceEventParser.Keywords.ImageLoad
        | KernelTraceEventParser.Keywords.DeferedProcedureCalls
        | KernelTraceEventParser.Keywords.Interrupt
        | KernelTraceEventParser.Keywords.MemoryHardFaults
        | KernelTraceEventParser.Keywords.DiskIO
        | KernelTraceEventParser.Keywords.DiskFileIO;

    /// <summary>Heavier keywords enabled only inside a high-resolution window (can be 1–3%+ CPU under load).</summary>
    public const KernelTraceEventParser.Keywords HighResolutionKeywords =
        KernelTraceEventParser.Keywords.ContextSwitch
        | KernelTraceEventParser.Keywords.Dispatcher      // ReadyThread events
        | KernelTraceEventParser.Keywords.Profile;        // sampled-profile stacks

    private readonly int _bufferSizeMb;
    private TraceEventSession? _session;
    private bool _highResPending;

    public EtwKernelSession(EtwOptions options)
    {
        // Total buffer pool. TraceEventSession in 3.1.16 exposes BufferSizeMB (total); the
        // per-buffer quantum / count from config are folded into the total here.
        _bufferSizeMb = Math.Max(4, (int)Math.Ceiling(options.BufferSizeKb * (double)options.BufferCount / 1024.0));

        try
        {
            // Fast pre-check so we can give a precise Health note without catching an exception.
            if (TraceEventSession.IsElevated() != true)
            {
                Health = MonitorHealth.Unavailable("kernel ETW requires administrator");
                return;
            }

            _session = new TraceEventSession(SessionName)
            {
                StopOnDispose = true,       // tear the OS session down with us
                BufferSizeMB = _bufferSizeMb,
            };
        }
        catch (Exception ex)
        {
            // Not admin, or a stale session we cannot take over. Degrade, never throw.
            _session = null;
            Health = MonitorHealth.Unavailable("kernel ETW requires administrator");
            LastError = ex;
        }
    }

    /// <summary>Never worse than <see cref="HealthStatus.Unavailable"/>; upgraded to Ok once started.</summary>
    public MonitorHealth Health { get; private set; } = MonitorHealth.Ok;

    public Exception? LastError { get; private set; }

    /// <summary>True when a usable OS session exists.</summary>
    public bool IsAvailable => _session is not null;

    /// <summary>The underlying session (null when unavailable). Consumers read <c>Session.Source</c>.</summary>
    public TraceEventSession? Session => _session;

    public bool HighResolutionActive { get; private set; }

    /// <summary>Raised after <see cref="Restart"/> recreates the session so consumers can re-bind parsers.</summary>
    public event EventHandler? SessionRecreated;

    public event EventHandler<MonitorHealth>? HealthChanged;

    /// <summary>Enable the always-on keyword set. Returns false when unavailable (no throw).</summary>
    public bool Start()
    {
        if (_session is null) return false;
        try
        {
            _session.EnableKernelProvider(AlwaysOnKeywords);
            SetHealth(MonitorHealth.Ok);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex;
            SetHealth(MonitorHealth.Failed($"EnableKernelProvider failed: {ex.Message}"));
            return false;
        }
    }

    /// <summary>Add the high-resolution keywords to the running session.</summary>
    public KeywordChangeResult EnableHighResolution()
    {
        if (_session is null) return KeywordChangeResult.Unavailable;
        if (HighResolutionActive) return KeywordChangeResult.Applied;
        try
        {
            // On Win8+ the kernel provider keyword mask can be updated live. TraceEvent 3.1.16
            // MAY still reject a second EnableKernelProvider call on the same session.
            // NOTE(build): verify. If it throws, we fall through to RestartRequired and the
            //              HighResWindowController must call Restart().
            _session.EnableKernelProvider(AlwaysOnKeywords | HighResolutionKeywords);
            HighResolutionActive = true;
            return KeywordChangeResult.Applied;
        }
        catch (Exception ex)
        {
            LastError = ex;
            _highResPending = true;
            return KeywordChangeResult.RestartRequired;
        }
    }

    /// <summary>Drop the high-resolution keywords from the running session.</summary>
    public KeywordChangeResult DisableHighResolution()
    {
        if (_session is null) return KeywordChangeResult.Unavailable;
        if (!HighResolutionActive && !_highResPending) return KeywordChangeResult.Applied;
        try
        {
            _session.EnableKernelProvider(AlwaysOnKeywords);
            HighResolutionActive = false;
            _highResPending = false;
            return KeywordChangeResult.Applied;
        }
        catch (Exception ex)
        {
            LastError = ex;
            _highResPending = false;
            return KeywordChangeResult.RestartRequired;
        }
    }

    /// <summary>
    /// Tear down and recreate the OS session with the currently desired keyword mask, then
    /// raise <see cref="SessionRecreated"/>. Used only when an in-place keyword change was
    /// rejected. Consumers must re-subscribe their parser callbacks and restart processing.
    /// </summary>
    public void Restart()
    {
        if (_session is null) return;
        bool wantHighRes = HighResolutionActive || _highResPending;

        try { _session.Dispose(); } catch { /* best effort */ }

        _session = new TraceEventSession(SessionName)
        {
            StopOnDispose = true,
            BufferSizeMB = _bufferSizeMb,
        };

        var keywords = AlwaysOnKeywords | (wantHighRes ? HighResolutionKeywords : KernelTraceEventParser.Keywords.None);
        _session.EnableKernelProvider(keywords);
        HighResolutionActive = wantHighRes;
        _highResPending = false;

        SessionRecreated?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        try { _session?.Stop(); } catch { /* already gone */ }
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { /* ignore */ }
        _session = null;
    }

    private void SetHealth(MonitorHealth h)
    {
        if (h == Health) return;
        Health = h;
        HealthChanged?.Invoke(this, h);
    }
}
