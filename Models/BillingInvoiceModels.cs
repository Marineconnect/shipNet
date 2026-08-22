namespace StarlinkDeviceManager.Models;

public sealed class BillingInvoiceIndexViewModel
{
    public BillingInvoiceSummaryViewModel Summary { get; set; } = new();
    public List<BillingInvoiceListItemViewModel> Items { get; set; } = [];
    public BillingInvoiceFilterViewModel Filter { get; set; } = new();
    public List<DeviceTenantOptionViewModel> Tenants { get; set; } = [];
    public List<BillingInvoiceDeviceOptionViewModel> Devices { get; set; } = [];
    public List<BillingInvoicePlanOptionViewModel> Plans { get; set; } = [];
    public List<string> InvoiceTypes { get; set; } = [];
    public List<string> InvoiceStatuses { get; set; } = [];
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public int TotalItems { get; set; }
    public bool IsTenantScoped { get; set; }
    public bool IsDeviceScoped { get; set; }
    public bool IsTransactionReupAdmin { get; set; }
    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalItems / (double)PageSize));
    public bool HasPreviousPage => CurrentPage > 1;
    public bool HasNextPage => CurrentPage < TotalPages;
    public int StartItem => TotalItems == 0 ? 0 : ((CurrentPage - 1) * PageSize) + 1;
    public int EndItem => Math.Min(TotalItems, CurrentPage * PageSize);
}

public sealed class BillingInvoicePageResult
{
    public List<BillingInvoiceListItemViewModel> Items { get; set; } = [];
    public BillingInvoiceSummaryViewModel Summary { get; set; } = new();
    public int CurrentPage { get; set; }
    public int PageSize { get; set; }
    public int TotalItems { get; set; }
}

public sealed class BillingInvoiceSummaryViewModel
{
    public decimal TotalInvoiceAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal PendingAmount { get; set; }
    public decimal TotalMargin { get; set; }
    public int PaidInvoiceCount { get; set; }
    public int PendingInvoiceCount { get; set; }
}

public sealed class BillingInvoiceFilterViewModel
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? BillingCycle { get; set; }
    public int? TenantId { get; set; }
    public int? DeviceId { get; set; }
    public string? Vessel { get; set; }
    public string? KitId { get; set; }
    public int? PricingPlanId { get; set; }
    public string? InvoiceType { get; set; }
    public string? InvoiceStatus { get; set; }
    public string? PaymentStatus { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? Search { get; set; }
    public string SortBy { get; set; } = "createdAt";
    public string SortDirection { get; set; } = "desc";
    public int? TenantIdScope { get; set; }
    public int? DeviceIdScope { get; set; }
}

public sealed class BillingInvoiceListItemViewModel
{
    public int InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string ReceiptNumber { get; set; } = string.Empty;
    public string PoNumber { get; set; } = string.Empty;
    public string InvoiceType { get; set; } = string.Empty;
    public int SubscriptionId { get; set; }
    public int TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public int DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceCode { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public string KitId { get; set; } = string.Empty;
    public int PricingPlanId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public DateTime UsageMonth { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public decimal DataGb { get; set; }
    public decimal BuyPrice { get; set; }
    public decimal SalePrice { get; set; }
    public decimal MarginAmount { get; set; }
    public decimal MarginPercent { get; set; }
    public decimal InvoiceAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal RefundAmount { get; set; }
    public decimal OutstandingAmount { get; set; }
    public string PaymentMethod { get; set; } = string.Empty;
    public DateTime? PaymentTime { get; set; }
    public string TransactionCode { get; set; } = string.Empty;
    public string PaymentDescription { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string BillingCycleDisplay => UsageMonth == default ? "-" : UsageMonth.ToString("MM/yyyy");
}

public sealed class BillingInvoiceDeviceOptionViewModel
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public string KitId { get; set; } = string.Empty;
    public string DisplayName => string.IsNullOrWhiteSpace(VesselName) ? DeviceName : $"{VesselName} - {DeviceName}";
}

public sealed class BillingInvoicePlanOptionViewModel
{
    public int Id { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public string PlanCode { get; set; } = string.Empty;
}
