using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace StutterDiag.Monitors.Common;

/// <summary>
/// P/Invoke surface for the telemetry monitors. Every entry point is read-only with respect
/// to system state: process enumeration, OS version, TBS device info, power status and
/// firmware variable reads. Nothing here changes TPM, Secure Boot, VBS or power policy.
/// All members are guarded by <see cref="OperatingSystem.IsWindows"/> at their call sites.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    // ---------------------------------------------------------------------
    // ntdll — process snapshot + real OS version
    // ---------------------------------------------------------------------

    /// <summary>SystemProcessInformation. Fill a buffer with a chain of SYSTEM_PROCESS_INFORMATION.</summary>
    internal const int SystemProcessInformation = 5;

    internal const uint STATUS_SUCCESS = 0x00000000;
    internal const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
    internal const uint STATUS_BUFFER_TOO_SMALL = 0xC0000023;

    [DllImport("ntdll.dll")]
    internal static extern uint NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll")]
    internal static extern uint RtlGetVersion(ref RTL_OSVERSIONINFOEXW versionInformation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct RTL_OSVERSIONINFOEXW
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion;
        public uint dwMinorVersion;
        public uint dwBuildNumber;
        public uint dwPlatformId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;

        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;
    }

    // ---------------------------------------------------------------------
    // tbs.dll — TPM Base Services device info (read-only)
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>TBS_RESULT Tbsi_GetDeviceInfo(UINT32 Size, PVOID Info)</c>. Info points at
    /// <see cref="TPM_DEVICE_INFO"/>. Returns 0 (TBS_SUCCESS) on success. This call does not
    /// open a TBS context and cannot change TPM state.
    /// </summary>
    [DllImport("tbs.dll", ExactSpelling = true)]
    internal static extern uint Tbsi_GetDeviceInfo(uint size, ref TPM_DEVICE_INFO info);

    internal const uint TBS_SUCCESS = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct TPM_DEVICE_INFO
    {
        public uint structVersion;
        public uint tpmVersion;        // TPM_VERSION_12 = 1, TPM_VERSION_20 = 2
        public uint tpmInterfaceType;  // TPM_IFTYPE_* — see TpmMonitor.MapInterfaceType (verify against tbs.h)
        public uint tpmImpRevision;
    }

    // ---------------------------------------------------------------------
    // kernel32 — power status + firmware variable read
    // ---------------------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 0 offline, 1 online, 255 unknown
        public byte BatteryFlag;         // bitmask, 255 unknown
        public byte BatteryLifePercent;  // 0..100, 255 unknown
        public byte SystemStatusFlag;    // 1 = battery saver on
        public int BatteryLifeTime;      // seconds, -1 unknown
        public int BatteryFullLifeTime;  // seconds, -1 unknown
    }

    /// <summary>Read-only firmware variable probe used as a Secure Boot fallback. Never writes.</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint GetFirmwareEnvironmentVariableW(
        string name,
        string guid,
        byte[] buffer,
        uint size);

    // ---------------------------------------------------------------------
    // powrprof.dll — active power scheme (read-only)
    // ---------------------------------------------------------------------

    [DllImport("powrprof.dll")]
    internal static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    /// <summary>Friendly name of a power scheme. <c>PowerReadFriendlyName</c>.</summary>
    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PowerReadFriendlyName(
        IntPtr rootPowerKey,
        IntPtr schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid,
        IntPtr powerSettingGuid,
        byte[]? buffer,
        ref uint bufferSize);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr hMem);

    // ---------------------------------------------------------------------
    // user32 — power setting notifications (optional; needs a message pump / service handle)
    // ---------------------------------------------------------------------

    internal const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x0;
    internal const int DEVICE_NOTIFY_SERVICE_HANDLE = 0x1;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    // GUID_ACDC_POWER_SOURCE {5D3E9A59-E9D5-4B00-A6BD-FF34FF516548}
    internal static Guid GUID_ACDC_POWER_SOURCE = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");

    // GUID_POWERSCHEME_PERSONALITY {245D8541-3943-4422-B025-13A784F679B7}
    internal static Guid GUID_POWERSCHEME_PERSONALITY = new("245D8541-3943-4422-B025-13A784F679B7");

    // GUID_MONITOR_POWER_ON {02731015-4510-4526-99E6-E5A17EBD1AEA}
    internal static Guid GUID_MONITOR_POWER_ON = new("02731015-4510-4526-99E6-E5A17EBD1AEA");
}
