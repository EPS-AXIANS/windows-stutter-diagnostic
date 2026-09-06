using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace StutterDiag.Service.Interop;

/// <summary>
/// Thin P/Invoke wrapper over the LSA policy API used at install time to grant the service
/// account its account rights / privileges (<c>SeSystemProfilePrivilege</c>,
/// <c>SeDebugPrivilege</c>, <c>SeServiceLogonRight</c>) — the managed BCL exposes no equivalent.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class LsaPrivileges
{
    private const int POLICY_CREATE_ACCOUNT = 0x00000010;
    private const int POLICY_LOOKUP_NAMES = 0x00000800;

    /// <summary>Grant each named privilege / logon right to <paramref name="account"/>. Throws on failure.</summary>
    public static void Grant(SecurityIdentifier account, params string[] privileges)
    {
        var sid = new byte[account.BinaryLength];
        account.GetBinaryForm(sid, 0);

        var attributes = new LSA_OBJECT_ATTRIBUTES { Length = Marshal.SizeOf<LSA_OBJECT_ATTRIBUTES>() };
        uint status = LsaOpenPolicy(IntPtr.Zero, ref attributes, POLICY_CREATE_ACCOUNT | POLICY_LOOKUP_NAMES, out IntPtr policy);
        if (status != 0)
            throw new Win32Exception(LsaNtStatusToWinError(status), "LsaOpenPolicy failed");

        var rights = new LSA_UNICODE_STRING[privileges.Length];
        try
        {
            for (int i = 0; i < privileges.Length; i++) rights[i] = MakeLsaString(privileges[i]);
            status = LsaAddAccountRights(policy, sid, rights, rights.Length);
            if (status != 0)
                throw new Win32Exception(LsaNtStatusToWinError(status), "LsaAddAccountRights failed");
        }
        finally
        {
            foreach (var r in rights)
                if (r.Buffer != IntPtr.Zero) Marshal.FreeHGlobal(r.Buffer);
            LsaClose(policy);
        }
    }

    private static LSA_UNICODE_STRING MakeLsaString(string value) => new()
    {
        Buffer = Marshal.StringToHGlobalUni(value),
        Length = (ushort)(value.Length * 2),
        MaximumLength = (ushort)((value.Length + 1) * 2),
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct LSA_UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LSA_OBJECT_ATTRIBUTES
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public int Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint LsaOpenPolicy(
        IntPtr systemName, ref LSA_OBJECT_ATTRIBUTES objectAttributes, int desiredAccess, out IntPtr policyHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint LsaAddAccountRights(
        IntPtr policyHandle, byte[] accountSid, LSA_UNICODE_STRING[] userRights, int countOfRights);

    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr policyHandle);

    [DllImport("advapi32.dll")]
    private static extern int LsaNtStatusToWinError(uint status);
}
