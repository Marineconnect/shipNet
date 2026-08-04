using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace StarlinkDeviceManager.Services;

public sealed class TransactionReupFileStorage(IConfiguration configuration, IWebHostEnvironment environment) : ITransactionReupFileStorage
{
    private readonly string rootPath = configuration["TransactionReupStorage:RootPath"] ?? "App_Data/transaction-reup-imports";
    private readonly long maxBytes = Math.Max(1, configuration.GetValue("TransactionReupStorage:MaxFileSizeMb", 20)) * 1024L * 1024L;

    public async Task<TransactionReupStoredFile> SaveAsync(IFormFile file, string batchCode, CancellationToken cancellationToken)
    {
        if (file.Length <= 0 || file.Length > maxBytes)
        {
            throw new InvalidOperationException($"File size must be greater than 0 and no more than {maxBytes / 1024 / 1024} MB.");
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not ".csv" and not ".xlsx")
        {
            throw new InvalidOperationException("Only .csv and .xlsx files are supported.");
        }

        var originalFileName = Path.GetFileName(file.FileName);
        var storedFileName = $"{batchCode}{extension}";
        var relativeDirectory = Path.Combine("transaction-reup-imports", DateTime.UtcNow.ToString("yyyyMMdd"));
        var absoluteDirectory = Path.Combine(environment.ContentRootPath, rootPath, DateTime.UtcNow.ToString("yyyyMMdd"));
        Directory.CreateDirectory(absoluteDirectory);
        var absolutePath = Path.Combine(absoluteDirectory, storedFileName);

        await using (var target = new FileStream(absolutePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await file.CopyToAsync(target, cancellationToken);
        }

        await using var hashStream = File.OpenRead(absolutePath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken)).ToLowerInvariant();
        return new TransactionReupStoredFile(originalFileName, storedFileName, Path.Combine(relativeDirectory, storedFileName), file.Length, file.ContentType ?? string.Empty, extension, hash);
    }

    public Task<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, rootPath));
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(candidate))
        {
            return Task.FromResult<Stream?>(null);
        }

        return Task.FromResult<Stream?>(new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read));
    }
}
