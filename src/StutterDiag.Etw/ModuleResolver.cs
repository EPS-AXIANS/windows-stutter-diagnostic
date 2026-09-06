using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace StutterDiag.Etw;

/// <summary>Result of mapping a routine address back to the driver / image that owns it.</summary>
public readonly record struct ModuleResolution(string Name, string? Version)
{
    /// <summary>Returned when the address maps to no known image.</summary>
    public static readonly ModuleResolution Unresolved = new("unresolved", null);
}

/// <summary>
/// Maintains an address→image map built from ETW <c>ImageLoad</c> / <c>ImageDCStart</c> events
/// (kernel drivers plus per-process user images) and resolves a DPC/ISR routine address to a
/// driver file name (and, best-effort, its file version).
/// </summary>
/// <remarks>
/// Reads happen on the ETW processing worker thread and must be allocation-light: kernel
/// lookups binary-search an immutable snapshot array that is swapped under a lock only when
/// images load/unload (rare). File-version strings are read lazily from disk on first hit and
/// cached, so the hot path never touches the file system.
/// </remarks>
public sealed class ModuleResolver
{
    private readonly struct Range
    {
        public readonly ulong Start;
        public readonly ulong End;         // exclusive
        public readonly string Name;
        public Range(ulong start, ulong end, string name) { Start = start; End = end; Name = name; }
    }

    private readonly object _gate = new();

    // Kernel-mode images: single global space, sorted by Start. Read via the volatile snapshot.
    private readonly List<Range> _kernel = new(512);
    private volatile Range[] _kernelSnapshot = Array.Empty<Range>();

    // Per-process user images: pid -> sorted ranges. Lower frequency use (frametime / process attrib).
    private readonly ConcurrentDictionary<int, Range[]> _perProcess = new();
    private readonly Dictionary<int, List<Range>> _perProcessMutable = new();

    // filename -> file version, resolved lazily off the hot path.
    private readonly ConcurrentDictionary<string, string?> _versionCache = new(StringComparer.OrdinalIgnoreCase);

    // x64 canonical kernel space starts here; anything at/above this we treat as a driver.
    private const ulong KernelSpaceFloor = 0xFFFF_0800_0000_0000UL;

    public int KernelModuleCount => _kernelSnapshot.Length;

    /// <summary>Register an image. <paramref name="processId"/> ≤ 4 or a kernel-space base ⇒ kernel driver.</summary>
    public void AddImage(ulong imageBase, long imageSize, string fileName, int processId)
    {
        if (imageBase == 0 || imageSize <= 0 || string.IsNullOrEmpty(fileName)) return;
        var name = FileNameOnly(fileName);
        var range = new Range(imageBase, imageBase + (ulong)imageSize, name);

        bool kernel = processId <= 4 || imageBase >= KernelSpaceFloor;
        lock (_gate)
        {
            if (kernel)
            {
                InsertSorted(_kernel, range);
                _kernelSnapshot = _kernel.ToArray();
            }
            else
            {
                if (!_perProcessMutable.TryGetValue(processId, out var list))
                    _perProcessMutable[processId] = list = new List<Range>(64);
                InsertSorted(list, range);
                _perProcess[processId] = list.ToArray();
            }
        }
    }

    /// <summary>Drop an image (ImageUnload) or, with <paramref name="imageBase"/> = 0, a whole exited process.</summary>
    public void RemoveImage(ulong imageBase, int processId)
    {
        lock (_gate)
        {
            if (processId > 4 && imageBase == 0)
            {
                _perProcessMutable.Remove(processId);
                _perProcess.TryRemove(processId, out _);
                return;
            }

            if (processId <= 4 || imageBase >= KernelSpaceFloor)
            {
                _kernel.RemoveAll(r => r.Start == imageBase);
                _kernelSnapshot = _kernel.ToArray();
            }
            else if (_perProcessMutable.TryGetValue(processId, out var list))
            {
                list.RemoveAll(r => r.Start == imageBase);
                _perProcess[processId] = list.ToArray();
            }
        }
    }

    /// <summary>Resolve a kernel routine address (DPC/ISR handler).</summary>
    public ModuleResolution Resolve(ulong address)
    {
        var hit = Find(_kernelSnapshot, address);
        return hit is null ? ModuleResolution.Unresolved : new ModuleResolution(hit, VersionOf(hit));
    }

    /// <summary>Resolve a user-mode address in a specific process, falling back to the kernel map.</summary>
    public ModuleResolution Resolve(ulong address, int processId)
    {
        if (_perProcess.TryGetValue(processId, out var snap))
        {
            var hit = Find(snap, address);
            if (hit is not null) return new ModuleResolution(hit, VersionOf(hit));
        }
        return Resolve(address);
    }

    // --- helpers -------------------------------------------------------------

    private static void InsertSorted(List<Range> list, Range r)
    {
        int i = list.BinarySearch(r, RangeStartComparer.Instance);
        if (i < 0) i = ~i;
        // Replace an exact-base duplicate (image reloaded at same base) rather than stacking.
        if (i < list.Count && list[i].Start == r.Start) list[i] = r;
        else list.Insert(i, r);
    }

    private static string? Find(Range[] snapshot, ulong address)
    {
        int lo = 0, hi = snapshot.Length - 1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            ref readonly var r = ref snapshot[mid];
            if (address < r.Start) hi = mid - 1;
            else if (address >= r.End) lo = mid + 1;
            else return r.Name;
        }
        return null;
    }

    private string? VersionOf(string fileName)
        => _versionCache.GetOrAdd(fileName, static fn =>
        {
            try
            {
                // Best-effort: driver name only, so probe the standard driver directory.
                var path = System.IO.Path.Combine(Environment.SystemDirectory, "drivers", fn);
                if (!System.IO.File.Exists(path))
                    path = System.IO.Path.Combine(Environment.SystemDirectory, fn);
                return System.IO.File.Exists(path)
                    ? FileVersionInfo.GetVersionInfo(path).FileVersion
                    : null;
            }
            catch
            {
                return null;
            }
        });

    private static string FileNameOnly(string path)
    {
        int slash = path.LastIndexOfAny(s_slashes);
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static readonly char[] s_slashes = { '\\', '/' };

    private sealed class RangeStartComparer : IComparer<Range>
    {
        public static readonly RangeStartComparer Instance = new();
        public int Compare(Range x, Range y) => x.Start.CompareTo(y.Start);
    }
}
