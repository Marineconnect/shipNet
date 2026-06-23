using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface ICurrencyExchangeService
{
    Task<CurrencyExchangePageResult> GetRatesAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<CurrencyExchangeRateFormViewModel?> GetRateByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<bool> IsRateInUseAsync(string fromCurrency, string toCurrency, DateTime effectiveDate, int? excludeId = null, CancellationToken cancellationToken = default);
    Task<int> CreateRateAsync(CurrencyExchangeRateFormViewModel model, int? userId, string username, CancellationToken cancellationToken = default);
    Task UpdateRateAsync(CurrencyExchangeRateFormViewModel model, int? userId, string username, CancellationToken cancellationToken = default);
    Task DeleteRateAsync(int id, int? userId, string username, CancellationToken cancellationToken = default);
    Task<CurrencyConversionResultViewModel?> ConvertAsync(CurrencyConversionFormViewModel model, CancellationToken cancellationToken = default);
}
