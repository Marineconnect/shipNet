namespace StarlinkDeviceManager.Models;

public class AuthUserRecord
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Avatar { get; set; }
    public string? UserType { get; set; }
    public int? TenantId { get; set; }
    public int? DeviceId { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? IdentificationNumber { get; set; }
    public DateTime? LastOnlineTime { get; set; }
    public string? IPAccess { get; set; }
    public DateTime? LastUpdatePassword { get; set; }
    public bool IsViewOnly { get; set; }
    public bool CanManageTransactions { get; set; }

    public bool IsAdmin =>
        string.Equals(UserType?.Trim(), ManagedUserType.Admin, StringComparison.OrdinalIgnoreCase);

    public bool IsTenantUser =>
        string.Equals(UserType?.Trim(), ManagedUserType.Tenant, StringComparison.OrdinalIgnoreCase);

    public bool IsShipAdmin =>
        string.Equals(UserType?.Trim(), ManagedUserType.ShipAdmin, StringComparison.OrdinalIgnoreCase);

    public bool IsCrew =>
        string.Equals(UserType?.Trim(), ManagedUserType.Crew, StringComparison.OrdinalIgnoreCase);

    public bool CanManageShipUsers => !IsCrew;

    public bool HasTenantScope => TenantId.HasValue && TenantId.Value > 0;
}
