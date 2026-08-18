using Microsoft.AspNetCore.Http;

namespace StarlinkDeviceManager.Services;

public interface ITransactionReupFileStorage
{
    Task<TransactionReupStoredFile> SaveAsync(IFormFile file, string batchCode, CancellationToken cancellationToken);
    Task<TransactionReupStoredFile> SavePdfAsync(IFormFile file, string batchCode, int itemId, CancellationToken cancellationToken);
    Task<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken);
}

public sealed record TransactionReupStoredFile(
    string OriginalFileName,
    string StoredFileName,
    string RelativePath,
    long Size,
    string ContentType,
    string Extension,
    string Sha256);
