using System.Reflection;
using StarlinkDeviceManager.Controllers;
using StarlinkDeviceManager.Models;

public class DevicePermissionsTests
{
    [Theory]
    [InlineData(ManagedUserType.Tenant, "admin", false)]
    [InlineData(ManagedUserType.Tenant, "tenant", false)]
    [InlineData(ManagedUserType.ShipAdmin, "admin", false)]
    [InlineData(ManagedUserType.Crew, "crew", false)]
    [InlineData(ManagedUserType.Admin, "system-admin", true)]
    public void DeviceAndSubscriptionPermissionsRespectRoles(string role, string username, bool expected)
    {
        var user = new AuthUserRecord { UserType = role, Username = username, TenantId = 1 };
        foreach (var method in new[] { "CanManageDevices", "CanCreateSubscriptions" })
        {
            var permission = typeof(DashboardController).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.Equal(expected, (bool)permission.Invoke(null, new object?[] { user })!);
            user.IsViewOnly = true;
            Assert.False((bool)permission.Invoke(null, new object?[] { user })!);
            user.IsViewOnly = false;
        }
    }
}
