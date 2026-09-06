using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace StutterDiag.Service.Ipc;

/// <summary>
/// Builds the <see cref="NamedPipeServerStream"/> instances the IPC server accepts on. The
/// service typically runs as <c>NT SERVICE\StutterDiag</c> or <c>LocalSystem</c>, while the GUI
/// runs non-elevated (ARCHITECTURE §3), so the pipe needs an explicit ACL that lets ordinary
/// authenticated users connect. SYSTEM and Administrators keep full control.
/// </summary>
public static class PipeStreamFactory
{
    /// <summary>
    /// Factory passed to <c>NamedPipeServer</c>. On Windows the pipe carries a
    /// <see cref="PipeSecurity"/> descriptor; off Windows (CI only) it falls back to a plain
    /// local pipe so the type still loads.
    /// </summary>
    public static NamedPipeServerStream Create(string pipeName)
    {
        if (!OperatingSystem.IsWindows())
            return CreatePlain(pipeName);

        return CreateSecured(pipeName);
    }

    private static NamedPipeServerStream CreatePlain(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateSecured(string pipeName)
    {
        var security = new PipeSecurity();

        // Authenticated users (the interactive GUI / CLI): connect + read + write, no ACL edits.
        var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        security.AddAccessRule(new PipeAccessRule(
            authenticatedUsers,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        // SYSTEM (the service itself) and local Administrators: full control.
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new PipeAccessRule(admins, PipeAccessRights.FullControl, AccessControlType.Allow));

        // NamedPipeServerStreamAcl.Create is the ACL-aware factory (System.IO.Pipes on net8.0-windows).
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }
}
