using Microsoft.AspNetCore.Http;

namespace StarlinkDeviceManager.Services;

public interface IKitExportService
{
    Task<byte[]> ProcessSlkTemplateAsync(IFormFile importFile, CancellationToken cancellationToken = default);
}
