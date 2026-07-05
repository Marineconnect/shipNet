using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace StarlinkDeviceManager.Models;

public class TenantIndexViewModel
{
    public List<TenantListItemViewModel> Tenants { get; set; } = [];
    public TenantFormViewModel CreateForm { get; set; } = new();
    public TenantFormViewModel EditForm { get; set; } = new();
    public bool OpenCreateModal { get; set; }
    public bool OpenEditModal { get; set; }
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalTenants { get; set; }
    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalTenants / (double)PageSize));
    public bool HasPreviousPage => CurrentPage > 1;
    public bool HasNextPage => CurrentPage < TotalPages;
    public bool IsTenantScoped { get; set; }
    public bool CanManageTenant { get; set; } = true;
    public bool CanCreateTenant => CanManageTenant && !IsTenantScoped;
    public bool CanDeleteTenant => CanManageTenant && !IsTenantScoped;
}

public class TenantPageResult
{
    public List<TenantListItemViewModel> Tenants { get; set; } = [];
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalTenants { get; set; }
}

public class TenantListItemViewModel
{
    public int Id { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Description { get; set; }
    public string? Logo { get; set; }
    public string? Address { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedDate { get; set; }
    public string? UpdatedBy { get; set; }

    public string DescriptionDisplay =>
        string.IsNullOrWhiteSpace(Description)
            ? "-"
            : Description.Length > 120
                ? $"{Description[..117]}..."
                : Description;

    public string UpdatedDateVietnam =>
        UpdatedDate.HasValue
            ? UpdatedDate.Value.ToString("yyyy-MM-dd HH:mm:ss")
            : "-";
}

public class TenantFormViewModel
{
    public int Id { get; set; }
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;

    [Required(ErrorMessage = "Tên tenant không được để trống")]
    [StringLength(250, ErrorMessage = "Tên tenant tối đa 250 ký tự")]
    [Display(Name = "Tên tenant")]
    public string TenantName { get; set; } = string.Empty;

    [StringLength(350, ErrorMessage = "Email tối đa 350 ký tự")]
    [EmailAddress(ErrorMessage = "Email không đúng định dạng")]
    [Display(Name = "Email")]
    public string? Email { get; set; }

    [StringLength(50, ErrorMessage = "Phone tối đa 50 ký tự")]
    [Display(Name = "Phone")]
    public string? Phone { get; set; }

    [StringLength(1000, ErrorMessage = "Mô tả tối đa 1000 ký tự")]
    [Display(Name = "Mô tả")]
    public string? Description { get; set; }

    [StringLength(550, ErrorMessage = "Địa chỉ tối đa 550 ký tự")]
    [Display(Name = "Địa chỉ")]
    public string? Address { get; set; }

    public string? ExistingLogoPath { get; set; }

    [Display(Name = "Logo")]
    public IFormFile? LogoFile { get; set; }
}
