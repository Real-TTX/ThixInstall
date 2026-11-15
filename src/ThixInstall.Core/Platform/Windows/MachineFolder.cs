using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ThixInstall.Core.Infrastructure;

namespace ThixInstall.Core.Platform.Windows;

/// <summary>
/// The agent's folder in %ProgramData% holds the device token (protected for the machine: any local account could
/// unprotect it). So only SYSTEM and the administrators may open the folder; the inherited rights of ProgramData go.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MachineFolder
{
    public static void Secure(string folder)
    {
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            }

            new DirectoryInfo(folder).SetAccessControl(security);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or InvalidOperationException or PrivilegeNotHeldException)
        {
            Log.Warning($"The rights of {folder} could not be set: {exception.Message}");
        }
    }
}
