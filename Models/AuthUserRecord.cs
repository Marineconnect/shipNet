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
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? IdentificationNumber { get; set; }
    public DateTime? LastOnlineTime { get; set; }
    public string? IPAccess { get; set; }
    public DateTime? LastUpdatePassword { get; set; }

    public bool IsTenantUser =>
        string.Equals(UserType?.Trim(), ManagedUserType.Tenant, StringComparison.OrdinalIgnoreCase);

    public bool HasTenantScope => TenantId.HasValue && TenantId.Value > 0;
}
