namespace StarlinkDeviceManager.Models;

public static class BillingPermissions
{
    public static bool CanManage(AuthUserRecord? user) =>
        user is not null && !user.IsViewOnly &&
        !user.IsTenantUser && !user.IsShipAdmin && !user.IsCrew &&
        (user.IsAdmin || string.Equals(user.Username?.Trim(), "admin", StringComparison.OrdinalIgnoreCase));
}
