using System.ComponentModel.DataAnnotations;

namespace StarlinkDeviceManager.Models;

public class CurrencyExchangeIndexViewModel
{
    public List<CurrencyExchangeRateViewModel> Rates { get; set; } = [];
    public CurrencyExchangeRateFormViewModel CreateForm { get; set; } = new();
    public CurrencyExchangeRateFormViewModel EditForm { get; set; } = new();
    public CurrencyConversionFormViewModel ConversionForm { get; set; } = new();
    public CurrencyConversionResultViewModel? ConversionResult { get; set; }
    public bool OpenCreateModal { get; set; }
    public bool OpenEditModal { get; set; }
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalRates { get; set; }
    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalRates / (double)PageSize));
    public bool HasPreviousPage => CurrentPage > 1;
    public bool HasNextPage => CurrentPage < TotalPages;
}

public class CurrencyExchangePageResult
{
    public List<CurrencyExchangeRateViewModel> Rates { get; set; } = [];
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalRates { get; set; }
}

public class CurrencyExchangeRateViewModel
{
    public int Id { get; set; }
    public string FromCurrency { get; set; } = string.Empty;
    public string ToCurrency { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public DateTime EffectiveDate { get; set; }
    public string Status { get; set; } = "active";
    public DateTime? UpdatedDate { get; set; }
    public string? UpdatedBy { get; set; }

    public string PairDisplay => $"{FromCurrency} -> {ToCurrency}";
    public string RateDisplay => Rate.ToString("#,##0.####");
    public string EffectiveDateDisplay => EffectiveDate.ToString("dd/MM/yyyy");
    public string UpdatedDateDisplay => UpdatedDate.HasValue ? UpdatedDate.Value.ToString("dd/MM/yyyy HH:mm") : "-";
}

public class CurrencyExchangeRateFormViewModel
{
    public int Id { get; set; }
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;

    [Required(ErrorMessage = "Vui lòng nhập đồng tiền nguồn.")]
    [StringLength(10, ErrorMessage = "Mã tiền tệ tối đa 10 ký tự.")]
    [RegularExpression(@"^[A-Za-z]{3,10}$", ErrorMessage = "Mã tiền tệ chỉ gồm chữ cái, ví dụ USD hoặc VND.")]
    public string FromCurrency { get; set; } = "USD";

    [Required(ErrorMessage = "Vui lòng nhập đồng tiền đích.")]
    [StringLength(10, ErrorMessage = "Mã tiền tệ tối đa 10 ký tự.")]
    [RegularExpression(@"^[A-Za-z]{3,10}$", ErrorMessage = "Mã tiền tệ chỉ gồm chữ cái, ví dụ USD hoặc VND.")]
    public string ToCurrency { get; set; } = "VND";

    [Range(0.0001, 999999999999, ErrorMessage = "Tỷ giá phải lớn hơn 0.")]
    public decimal Rate { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn ngày bắt đầu áp dụng.")]
    public DateTime EffectiveDate { get; set; } = DateTime.Today;

    [Required]
    [StringLength(50)]
    public string Status { get; set; } = "active";
}

public class CurrencyConversionFormViewModel
{
    [Range(0.0001, 999999999999, ErrorMessage = "Số tiền phải lớn hơn 0.")]
    public decimal Amount { get; set; } = 1;

    [Required]
    [StringLength(10)]
    public string FromCurrency { get; set; } = "USD";

    [Required]
    [StringLength(10)]
    public string ToCurrency { get; set; } = "VND";

    [Required]
    public DateTime ConversionDate { get; set; } = DateTime.Today;
}

public class CurrencyConversionResultViewModel
{
    public decimal Amount { get; set; }
    public string FromCurrency { get; set; } = string.Empty;
    public string ToCurrency { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public DateTime EffectiveDate { get; set; }
    public decimal ConvertedAmount { get; set; }

    public string RateDisplay => Rate.ToString("#,##0.####");
    public string AmountDisplay => Amount.ToString("#,##0.##");
    public string ConvertedAmountDisplay => ConvertedAmount.ToString("#,##0.##");
    public string EffectiveDateDisplay => EffectiveDate.ToString("dd/MM/yyyy");
}
