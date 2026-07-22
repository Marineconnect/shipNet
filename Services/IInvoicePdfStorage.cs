namespace StarlinkDeviceManager.Services;

public interface IInvoicePdfStorage
{
    Task<string> SaveAsync(Stream source, string storageKey, CancellationToken cancellationToken = default);
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default);
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default);
}
