using System.ComponentModel.DataAnnotations;

namespace StarlinkDeviceManager.Models;

public class PricingPlanIndexViewModel
{
    public List<PricingPlanListItemViewModel> Plans { get; set; } = [];
    public List<TenantPricingListItemViewModel> TenantPrices { get; set; } = [];
    public List<DeviceTenantOptionViewModel> TenantOptions { get; set; } = [];
    public List<PricingPlanOptionViewModel> PricingPlanOptions { get; set; } = [];
    public PricingPlanFormViewModel CreateForm { get; set; } = new();
    public PricingPlanFormViewModel EditForm { get; set; } = new();
    public TenantPricingFormViewModel TenantPriceCreateForm { get; set; } = new();
    public TenantPricingFormViewModel TenantPriceEditForm { get; set; } = new();
    public bool OpenCreateModal { get; set; }
    public bool OpenEditModal { get; set; }
    public bool OpenTenantPriceCreateModal { get; set; }
    public bool OpenTenantPriceEditModal { get; set; }
    public string ActiveTab { get; set; } = "product";
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalPlans { get; set; }
    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalPlans / (double)PageSize));
    public bool HasPreviousPage => CurrentPage > 1;
    public bool HasNextPage => CurrentPage < TotalPages;
    public int TenantPricingCurrentPage { get; set; } = 1;
    public int TenantPricingPageSize { get; set; } = 10;
    public int TotalTenantPrices { get; set; }
    public int? TenantPricingTenantId { get; set; }
    public string TenantPricingSearch { get; set; } = string.Empty;
    public int TenantPricingTotalPages => TenantPricingPageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalTenantPrices / (double)TenantPricingPageSize));
    public bool TenantPricingHasPreviousPage => TenantPricingCurrentPage > 1;
    public bool TenantPricingHasNextPage => TenantPricingCurrentPage < TenantPricingTotalPages;
}

public class PricingPlanPageResult
{
    public List<PricingPlanListItemViewModel> Plans { get; set; } = [];
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalPlans { get; set; }
}

public class PricingPlanImportResult
{
    public int CreatedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int SkippedCount { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<PricingPlanFormViewModel> Plans { get; set; } = [];
}

public class TenantPricingPageResult
{
    public List<TenantPricingListItemViewModel> Prices { get; set; } = [];
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalPrices { get; set; }
}

public class TenantPricingImportResult
{
    public int CreatedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int DeviceCreatedCount { get; set; }
    public int DeviceUpdatedCount { get; set; }
    public int DeviceSkippedCount { get; set; }
    public int SkippedCount { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<TenantPricingImportRow> Prices { get; set; } = [];
}

public class TenantPricingDevicePreviewResult
{
    public List<TenantPricingDeviceTenantViewModel> Tenants { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}

public class TenantPricingDeviceTenantViewModel
{
    public int TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public int ImportedPlanCount { get; set; }
    public List<TenantPricingDeviceItemViewModel> Devices { get; set; } = [];
}

public class TenantPricingDeviceItemViewModel
{
    public int DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceCode { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public int ExistingPlanCount { get; set; }
}

public class TenantPricingImportRow
{
    public int TenantId { get; set; }
    public string TenantKey { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public string PlanCode { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public decimal ResellerPrice { get; set; }
    public decimal FinalPrice { get; set; }
    public decimal ResellerOverChargePrice { get; set; }
    public decimal FinalOverChargePrice { get; set; }
}

public class PricingPlanOptionViewModel
{
    public int Id { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public string PlanCode { get; set; } = string.Empty;
    public decimal BaseData { get; set; }
    public decimal CostPrice { get; set; }
    public decimal ResellerPrice { get; set; }
    public decimal FinalPrice { get; set; }
    public decimal CostOverChargePrice { get; set; }
    public decimal ResellerOverChargePrice { get; set; }
    public decimal FinalOverChargePrice { get; set; }
    public string DisplayName => $"{PlanName} ({PlanCode})";
}

public class PricingPlanListItemViewModel
{
    public int Id { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public string PlanCode { get; set; } = string.Empty;
    public decimal CostPrice { get; set; }
    public decimal ResellerPrice { get; set; }
    public decimal FinalPrice { get; set; }
    public decimal BaseData { get; set; }
    public decimal CostOverChargePrice { get; set; }
    public decimal ResellerOverChargePrice { get; set; }
    public decimal FinalOverChargePrice { get; set; }
    public string Status { get; set; } = "active";
    public DateTime? UpdatedDate { get; set; }
    public string? UpdatedBy { get; set; }

    public string StatusDisplay => string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase)
        ? "Active"
        : "Inactive";

    public string UpdatedDateDisplay => UpdatedDate.HasValue
        ? UpdatedDate.Value.ToString("dd/MM/yyyy HH:mm:ss")
        : "-";
}

public class PricingPlanFormViewModel
{
    public int Id { get; set; }
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;

    [Required(ErrorMessage = "Vui lòng nhập tên gói.")]
    [StringLength(250, ErrorMessage = "Tên gói tối đa 250 ký tự.")]
    [Display(Name = "Tên gói")]
    public string PlanName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập mã gói.")]
    [StringLength(100, ErrorMessage = "Mã gói tối đa 100 ký tự.")]
    [RegularExpression(@"^[A-Za-z0-9._-]+$", ErrorMessage = "Mã gói chỉ gồm chữ, số, dấu chấm, gạch ngang hoặc gạch dưới.")]
    [Display(Name = "Mã gói")]
    public string PlanCode { get; set; } = string.Empty;

    [Range(0, 999999999999, ErrorMessage = "Giá Cost KVH phải lớn hơn hoặc bằng 0.")]
    [Display(Name = "Giá Cost KVH")]
    public decimal CostPrice { get; set; }

    [Range(0, 999999999999, ErrorMessage = "Đơn giá đại lý phải lớn hơn hoặc bằng 0.")]
    [Display(Name = "Đơn giá đại lý ($)")]
    public decimal ResellerPrice { get; set; }

    [Range(0, 999999999999, ErrorMessage = "Đơn giá bán phải lớn hơn hoặc bằng 0.")]
    [Display(Name = "Đơn giá bán ra ($)")]
    public decimal FinalPrice { get; set; }

    [Range(0, 999999999999, ErrorMessage = "Dung lượng phải lớn hơn hoặc bằng 0.")]
    [Display(Name = "Dung lượng")]
    public decimal BaseData { get; set; }

    [Range(0, 999999999999, ErrorMessage = "Giá Cost Overcharge KVH phải lớn hơn hoặc bằng 0.")]
    [Display(Name = "Giá Cost Overcharge KVH")]
    public decimal CostOverChargePrice { get; set; }

    [Range(0, 999999999999, ErrorMessage = "Giá mua thêm cho đại lý phải lớn hơn hoặc bằng 0.")]
    [Display(Name = "Giá mua thêm đại lý ($)")]
    public decimal ResellerOverChargePrice { get; set; }

    [Range(0, 999999999999, ErrorMessage = "Giá mua thêm bán ra phải lớn hơn hoặc bằng 0.")]
    [Display(Name = "Giá mua thêm bán ra ($)")]
    public decimal FinalOverChargePrice { get; set; }

    [Required]
    [StringLength(50)]
    [Display(Name = "Trạng thái")]
    public string Status { get; set; } = "active";
}

public class TenantPricingListItemViewModel
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public int PricingPlanId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public string PlanCode { get; set; } = string.Empty;
    public decimal BaseData { get; set; }
    public decimal ResellerPrice { get; set; }
    public decimal FinalPrice { get; set; }
    public decimal ResellerOverChargePrice { get; set; }
    public decimal FinalOverChargePrice { get; set; }
    public DateTime? UpdatedDate { get; set; }
    public string? UpdatedBy { get; set; }

    public string UpdatedDateDisplay => UpdatedDate.HasValue
        ? UpdatedDate.Value.ToString("dd/MM/yyyy HH:mm:ss")
        : "-";
}

public class TenantPricingFormViewModel
{
    public int Id { get; set; }
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;

    [Range(1, int.MaxValue, ErrorMessage = "Vui lòng chọn tenant.")]
    public int TenantId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Vui lòng chọn gói giá.")]
    public int PricingPlanId { get; set; }

    [Range(0, 999999999999, ErrorMessage = "Đơn giá đại lý phải lớn hơn hoặc bằng 0.")]
    public decimal ResellerPrice { get; set; }

    [Range(0, 999999999999, ErrorMessage = "Đơn giá bán phải lớn hơn hoặc bằng 0.")]
    public decimal FinalPrice { get; set; }

    [Range(0, 999999999999, ErrorMessage = "Giá mua thêm cho đại lý phải lớn hơn hoặc bằng 0.")]
    public decimal ResellerOverChargePrice { get; set; }

    [Range(0, 999999999999, ErrorMessage = "Giá mua thêm bán ra phải lớn hơn hoặc bằng 0.")]
    public decimal FinalOverChargePrice { get; set; }
}
