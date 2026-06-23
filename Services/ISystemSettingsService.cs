using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface ISystemSettingsService
{
    Task<List<SystemSettingViewModel>> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<SystemSettingFormViewModel?> GetSettingByIdAsync(int id, CancellationToken cancellationToken = default);
    Task UpdateSettingAsync(SystemSettingFormViewModel model, int? userId, string username, CancellationToken cancellationToken = default);
    Task<Dictionary<string, string>> GetSettingsByCodesAsync(IEnumerable<string> settingCodes, CancellationToken cancellationToken = default);
}
