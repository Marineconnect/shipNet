using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace StarlinkDeviceManager.Models;

public class UserManagementIndexViewModel
{
    public List<UserListItemViewModel> Users { get; set; } = [];
    public UserManagementFormViewModel CreateForm { get; set; } = new();
    public UserManagementFormViewModel EditForm { get; set; } = new();
    public List<DeviceTenantOptionViewModel> Tenants { get; set; } = [];
    public bool OpenCreateModal { get; set; }
    public bool OpenEditModal { get; set; }
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalUsers { get; set; }
    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalUsers / (double)PageSize));
    public bool HasPreviousPage => CurrentPage > 1;
    public bool HasNextPage => CurrentPage < TotalPages;
    public bool IsTenantScoped { get; set; }
    public int? CurrentTenantId { get; set; }
    public string? CurrentTenantName { get; set; }
    public bool CanSelectTenant => !IsTenantScoped;
}

public class UserManagementPageResult
{
    public List<UserListItemViewModel> Users { get; set; } = [];
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalUsers { get; set; }
}

public class UserListItemViewModel
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Avatar { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? IdentificationNumber { get; set; }
    public string UserGroup { get; set; } = ManagedUserType.Admin;
    public int? TenantId { get; set; }
    public string? TenantName { get; set; }
    public DateTime? LastOnlineTime { get; set; }
    public DateTime? LastUpdatePassword { get; set; }

    public string DisplayNameOrUsername => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;
    public string UserGroupDisplay => ManagedUserType.ToDisplay(UserGroup);
    public string TenantDisplay => string.Equals(UserGroup, ManagedUserType.Tenant, StringComparison.OrdinalIgnoreCase)
        ? (string.IsNullOrWhiteSpace(TenantName) ? "-" : TenantName)
        : "-";
    public string LastOnlineTimeVietnam => FormatDateTime(LastOnlineTime);
    public string LastUpdatePasswordVietnam => FormatDateTime(LastUpdatePassword);

    private static string FormatDateTime(DateTime? value)
    {
        return value.HasValue
            ? value.Value.ToString("yyyy-MM-dd HH:mm:ss")
            : "-";
    }
}

public class UserManagementFormViewModel
{
    public int Id { get; set; }
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;

    [Required(ErrorMessage = "Username không được để trống")]
    [StringLength(50, ErrorMessage = "Username tối đa 50 ký tự")]
    [Display(Name = "Username")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "Tên hiển thị không được để trống")]
    [StringLength(250, ErrorMessage = "Tên hiển thị tối đa 250 ký tự")]
    [Display(Name = "Tên hiển thị")]
    public string DisplayName { get; set; } = string.Empty;

    [StringLength(50, ErrorMessage = "Phone tối đa 50 ký tự")]
    [Display(Name = "Phone")]
    public string? Phone { get; set; }

    [StringLength(50, ErrorMessage = "Email tối đa 50 ký tự")]
    [EmailAddress(ErrorMessage = "Email không đúng định dạng")]
    [Display(Name = "Email")]
    public string? Email { get; set; }

    [StringLength(50, ErrorMessage = "Identification Number tối đa 50 ký tự")]
    [Display(Name = "Identification Number")]
    public string? IdentificationNumber { get; set; }

    public string? ExistingLogoPath { get; set; }

    [Display(Name = "Logo")]
    public IFormFile? LogoFile { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn nhóm tài khoản")]
    [Display(Name = "Nhóm tài khoản")]
    public string UserGroup { get; set; } = ManagedUserType.Admin;

    [Display(Name = "Tenant")]
    public int? TenantId { get; set; }

    [StringLength(50)]
    public string Status { get; set; } = "active";

    [StringLength(100, ErrorMessage = "Mật khẩu tối đa 100 ký tự")]
    [DataType(DataType.Password)]
    [Display(Name = "Mật khẩu")]
    public string? Password { get; set; }
}

public static class ManagedUserType
{
    public const string Admin = "admin";
    public const string Tenant = "tenant";

    public static string NormalizeGroup(string? userGroup)
    {
        return string.Equals(userGroup, Tenant, StringComparison.OrdinalIgnoreCase)
            ? Tenant
            : Admin;
    }

    public static string Parse(string? rawUserType)
    {
        if (string.IsNullOrWhiteSpace(rawUserType))
        {
            return Admin;
        }

        var normalized = rawUserType.Trim();
        return string.Equals(normalized, Tenant, StringComparison.OrdinalIgnoreCase)
            ? Tenant
            : Admin;
    }

    public static string ToDisplay(string? userGroup)
    {
        return NormalizeGroup(userGroup) == Tenant
            ? "Tenant"
            : "Tài khoản quản trị";
    }
}
