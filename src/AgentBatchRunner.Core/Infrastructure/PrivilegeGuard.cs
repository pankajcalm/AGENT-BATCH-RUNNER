using System.Security.Principal;

namespace AgentBatchRunner.Infrastructure;

public static class PrivilegeGuard
{
    public static bool IsElevated()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        return string.Equals(Environment.UserName, "root", StringComparison.OrdinalIgnoreCase);
    }
}
