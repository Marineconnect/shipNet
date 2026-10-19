using StarlinkDeviceManager.Models;

public class BillingPermissionsTests
{
    [Theory]
    [InlineData(ManagedUserType.Tenant)]
    [InlineData(ManagedUserType.ShipAdmin)]
    [InlineData(ManagedUserType.Crew)]
    public void ScopedRolesCannotManageEvenWithAdminUsername(string type)
    {
        Assert.False(BillingPermissions.CanManage(new AuthUserRecord { UserType = type, Username = "admin", TenantId = 1, DeviceId = 1 }));
    }

    [Fact]
    public void SystemAdminCanManage()
    {
        Assert.True(BillingPermissions.CanManage(new AuthUserRecord { UserType = ManagedUserType.Admin }));
    }

    [Fact]
    public void ViewOnlyAndMissingUsersCannotManage()
    {
        Assert.False(BillingPermissions.CanManage(null));
        Assert.False(BillingPermissions.CanManage(new AuthUserRecord { UserType = ManagedUserType.Admin, IsViewOnly = true }));
    }
}
