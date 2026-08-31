using System.ComponentModel.DataAnnotations;

namespace StarlinkDeviceManager.Models;

public class MonthlySubscriptionIndexViewModel
{
    public List<MonthlySubscriptionListItemViewModel> Subscriptions { get; set; } = [];
    public MonthlySubscriptionSummaryViewModel Summary { get; set; } = new();
    public List<DeviceTenantOptionViewModel> Tenants { get; set; } = [];
    public List<SubscriptionDeviceOptionViewModel> Devices { get; set; } = [];
    public List<SubscriptionPlanOptionViewModel> Plans { get; set; } = [];
    public CreateMonthlySubscriptionViewModel CreateForm { get; set; } = new();
    public MonthlySubscriptionFilterViewModel Filter { get; set; } = new();
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalItems { get; set; }
    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalItems / (double)PageSize));
    public bool HasPreviousPage => CurrentPage > 1;
    public bool HasNextPage => CurrentPage < TotalPages;
    public bool IsTenantScoped { get; set; }
    public bool CanManageSubscriptions { get; set; } = true;
    public bool CanCreateSubscriptions { get; set; } = true;
    public bool OpenCreateModal { get; set; }
}

public class MonthlySubscriptionFilterViewModel
{
    public int? TenantId { get; set; }
    public int? DeviceId { get; set; }
    public int? PricingPlanId { get; set; }
    public string? KitId { get; set; }
    public string? Status { get; set; }
    public string? InvoiceStatus { get; set; }
    public DateTime? MonthFrom { get; set; }
    public DateTime? MonthTo { get; set; }
    public DateTime? NextBillingFrom { get; set; }
    public DateTime? NextBillingTo { get; set; }
    public DateTime? InvoicePaidFrom { get; set; }
    public DateTime? InvoicePaidTo { get; set; }
}

public class MonthlySubscriptionSummaryViewModel
{
    public int TotalSubscriptions { get; set; }
    public decimal TotalTopUpAmount { get; set; }
    public decimal TotalInvoiceAmount { get; set; }
    public decimal TotalPaid { get; set; }
}

public class MonthlySubscriptionPageResult
{
    public List<MonthlySubscriptionListItemViewModel> Subscriptions { get; set; } = [];
    public MonthlySubscriptionSummaryViewModel Summary { get; set; } = new();
    public int CurrentPage { get; set; }
    public int PageSize { get; set; }
    public int TotalItems { get; set; }
}

public class MonthlySubscriptionListItemViewModel
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int DeviceId { get; set; }
    public int PricingPlanId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public string KitId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string SubscriptionType { get; set; } = string.Empty;
    public decimal DataLimitGb { get; set; }
    public decimal BasePlanPrice { get; set; }
    public int SubscriptionDays { get; set; }
    public decimal SubscriptionPrice { get; set; }
    public decimal OverChargePrice { get; set; }
    public decimal TotalTopUpGb { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public DateTime? NextBillingDate { get; set; }
    public decimal TotalInvoiceAmount { get; set; }
    public decimal TotalPaid { get; set; }
    public string InvoiceStatus { get; set; } = string.Empty;
    public string SubscriptionPeriodDisplay => $"{StartDate:dd/MM/yyyy} to {EndDate:dd/MM/yyyy}";
    public string NextBillingDateDisplay => NextBillingDate.HasValue ? NextBillingDate.Value.ToString("dd/MM/yyyy") : "-";
}

public class MonthlySubscriptionDetailViewModel
{
    public MonthlySubscriptionListItemViewModel Subscription { get; set; } = new();
    public MonthlySubscriptionInvoiceSummaryViewModel InvoiceSummary { get; set; } = new();
    public List<SubscriptionInvoiceViewModel> Invoices { get; set; } = [];
    public List<NinePayQrSessionHistoryViewModel> QrSessions { get; set; } = [];
    public CreateSubscriptionInvoiceViewModel CreateInvoiceForm { get; set; } = new();
    public UpdateSubscriptionInvoiceViewModel UpdateInvoiceForm { get; set; } = new();
    public UpdateMonthlySubscriptionBillingViewModel UpdateBillingForm { get; set; } = new();
    public bool CanManageSubscriptions { get; set; } = true;
    public bool CanViewQrSessions { get; set; }
    public bool CanViewIntegrationLogs { get; set; }
    public bool CanEditBilling { get; set; }
    public string BillingEditBlockedReason { get; set; } = string.Empty;
    public string DefaultInvoicePoNumber { get; set; } = string.Empty;
}

public class MonthlySubscriptionInvoiceSummaryViewModel
{
    public string Type { get; set; } = "SUBSCRIPTION";
    public decimal TotalAmount { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal TotalRefund { get; set; }
    public string Status { get; set; } = "pending";
}

public class SubscriptionInvoiceViewModel
{
    public int Id { get; set; }
    public int SubscriptionId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string ReceiptNumber { get; set; } = string.Empty;
    public string PoNumber { get; set; } = string.Empty;
    public string InvoiceType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal DataGb { get; set; }
    public decimal CostPrice { get; set; }
    public decimal BuyPrice { get; set; }
    public decimal SalePrice { get; set; }
    public decimal MarginAmount { get; set; }
    public decimal Amount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal RefundAmount { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool IsPaidByBankTransfer { get; set; }
    public InvoicePdfFileViewModel PdfFile { get; set; } = new();
}

public class NinePayQrSessionHistoryViewModel
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public string Channel { get; set; } = "9pay";
    public DateTime ExpiresAt { get; set; }
    public decimal HoursRemaining { get; set; }
    public decimal InvoiceAmountVnd { get; set; }
    public string BankAccountNo { get; set; } = string.Empty;
    public string TransferContent { get; set; } = string.Empty;
    public decimal TransferFeeVnd { get; set; }
    public string ProviderRef { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string IpnPaymentNo { get; set; } = string.Empty;
    public string IpnProcessStatus { get; set; } = string.Empty;
    public DateTime? IpnReceivedAt { get; set; }
    public decimal SessionTotalVnd { get; set; }
    public bool IsGrouped { get; set; }
}

public class SubscriptionDeviceOptionViewModel
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public string KitId { get; set; } = string.Empty;
    public bool HasCurrentMonthSubscription { get; set; }
    public string CurrentMonthSubscriptionStatus { get; set; } = string.Empty;
    public string DisplayName => string.IsNullOrWhiteSpace(VesselName) ? DeviceName : VesselName;
}

public class SubscriptionPlanOptionViewModel
{
    public int DeviceId { get; set; }
    public int PricingPlanId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public string PlanCode { get; set; } = string.Empty;
    public decimal DataLimitGb { get; set; }
    public decimal ResellerPrice { get; set; }
    public decimal FinalPrice { get; set; }
    public decimal ResellerOverChargePrice { get; set; }
    public decimal FinalOverChargePrice { get; set; }
}

public class CreateMonthlySubscriptionViewModel
{
    [Range(1, int.MaxValue)]
    public int TenantId { get; set; }

    [Range(1, int.MaxValue)]
    public int DeviceId { get; set; }

    [Range(1, int.MaxValue)]
    public int PricingPlanId { get; set; }

    [Required]
    [StringLength(50)]
    public string SubscriptionType { get; set; } = "Personal";

    [Required]
    public DateTime UsageMonth { get; set; }

    [Required]
    public DateTime StartDate { get; set; }

    [Required]
    public DateTime EndDate { get; set; }

    [Required]
    public DateTime NextBillingDate { get; set; }
}

public class CreateSubscriptionInvoiceViewModel
{
    public int SubscriptionId { get; set; }

    [Required]
    [StringLength(50)]
    public string InvoiceType { get; set; } = "OVERCHARGE";

    [Range(0, 999999999999)]
    public decimal DataGb { get; set; }

    [Range(0, 999999999999)]
    public decimal Amount { get; set; }

    [StringLength(500)]
    public string Description { get; set; } = string.Empty;
}

public class UpdateSubscriptionInvoiceViewModel
{
    public int InvoiceId { get; set; }
    public int SubscriptionId { get; set; }

    [Required]
    [StringLength(100)]
    public string InvoiceNumber { get; set; } = string.Empty;

    [Range(0, 999999999999)]
    public decimal Amount { get; set; }

    [Range(0, 999999999999)]
    public decimal RefundAmount { get; set; }

    [StringLength(50)]
    public string Status { get; set; } = "pending";

    public DateTime? CompletedAt { get; set; }

    public bool ResumeKvh { get; set; }

    public string OperationCorrelationId { get; set; } = string.Empty;
}

public sealed class SubscriptionInvoiceUpdateResult
{
    public int InvoiceId { get; set; }
    public int SubscriptionId { get; set; }
    public int DeviceId { get; set; }
    public int TenantId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string OldStatus { get; set; } = string.Empty;
    public string NewStatus { get; set; } = string.Empty;
    public bool StatusChanged => !string.Equals(OldStatus, NewStatus, StringComparison.OrdinalIgnoreCase);
    public bool BecamePaid => StatusChanged && NewStatus.Equals("paid", StringComparison.OrdinalIgnoreCase);
}

public class UpdateMonthlySubscriptionStatusViewModel
{
    public int SubscriptionId { get; set; }

    [StringLength(50)]
    public string Status { get; set; } = "active";
}

public class UpdateMonthlySubscriptionBillingViewModel
{
    public int SubscriptionId { get; set; }

    [Required]
    public DateTime UsageMonth { get; set; }

    [Required]
    public DateTime StartDate { get; set; }

    [Required]
    public DateTime EndDate { get; set; }

    [Required]
    public DateTime NextBillingDate { get; set; }

    [Range(0, 999999999999)]
    public decimal BasePlanPrice { get; set; }

    [Range(0, 999999999999)]
    public decimal OverChargePrice { get; set; }
}
