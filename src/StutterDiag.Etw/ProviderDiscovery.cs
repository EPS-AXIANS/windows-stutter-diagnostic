using System.Collections.Generic;
using System.Linq;
using Microsoft.Diagnostics.Tracing.Session;

namespace StutterDiag.Etw;

/// <summary>
/// Enumerates the ETW providers registered on this machine so the user-mode session only
/// tries to enable providers that actually exist. Enabling a missing provider is not fatal
/// (ETW just records nothing), but discovery lets the report state plainly which data
/// sources were available and which were not.
/// </summary>
public sealed class ProviderDiscovery
{
    // Case-insensitive: manifest provider names are matched loosely ("Microsoft-Windows-DxgKrnl").
    private readonly HashSet<string> _names = new(System.StringComparer.OrdinalIgnoreCase);

    public ProviderDiscovery()
    {
        Refresh();
    }

    /// <summary>All manifest-registered provider names discovered on the machine.</summary>
    public IReadOnlyCollection<string> AllProviders => _names;

    /// <summary>Re-enumerate. Cheap enough to call at session start.</summary>
    public void Refresh()
    {
        _names.Clear();
        // TraceEventProviders.GetPublishedProviders() -> IEnumerable<Guid> of manifest providers.
        // TraceEventProviders.GetProviderName(Guid) -> friendly name.
        // NOTE(build): confirm both method names/signatures against TraceEvent 3.1.16
        //              (namespace Microsoft.Diagnostics.Tracing.Session.TraceEventProviders).
        try
        {
            foreach (var guid in TraceEventProviders.GetPublishedProviders())
            {
                var name = TraceEventProviders.GetProviderName(guid);
                if (!string.IsNullOrWhiteSpace(name))
                    _names.Add(name);
            }
        }
        catch
        {
            // Leave _names empty; IsAvailable then falls back to a name->guid probe.
        }
    }

    /// <summary>
    /// True if <paramref name="providerName"/> is registered. Falls back to a direct
    /// name→GUID lookup when the published-provider enumeration came back empty.
    /// </summary>
    public bool IsAvailable(string providerName)
    {
        if (_names.Contains(providerName))
            return true;

        // Fallback: TraceEventProviders.GetProviderGuidByName returns Guid.Empty when unknown.
        // NOTE(build): confirm this helper exists in 3.1.16; if not, drop this branch.
        try
        {
            return TraceEventProviders.GetProviderGuidByName(providerName) != System.Guid.Empty;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The subset of <paramref name="wanted"/> that is NOT registered on this machine.</summary>
    public IReadOnlyList<string> MissingFrom(IEnumerable<string> wanted)
        => wanted.Where(w => !IsAvailable(w)).ToList();
}
