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
        var relativeDirectory = DateTime.UtcNow.ToString("yyyyMMdd");
        var absoluteDirectory = Path.Combine(environment.ContentRootPath, rootPath, relativeDirectory);
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

    public async Task<TransactionReupStoredFile> SavePdfAsync(IFormFile file, string batchCode, int itemId, CancellationToken cancellationToken)
    {
        if (file.Length <= 0 || file.Length > maxBytes)
        {
            throw new InvalidOperationException($"PDF size must be greater than 0 and no more than {maxBytes / 1024 / 1024} MB.");
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension != ".pdf")
        {
            throw new InvalidOperationException("Only .pdf files are supported.");
        }

        var contentType = file.ContentType?.Trim() ?? string.Empty;
        if (!string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Uploaded file content type is not PDF.");
        }

        var originalFileName = Path.GetFileName(file.FileName);
        var safeBatchCode = SanitizePathPart(batchCode);
        var safeFileName = SanitizePathPart(string.IsNullOrWhiteSpace(originalFileName) ? $"reup-{itemId}.pdf" : originalFileName);
        var relativeDirectory = Path.Combine(
            "pdf",
            DateTime.UtcNow.Year.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DateTime.UtcNow.Month.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
            safeBatchCode,
            itemId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var storedFileName = $"{Path.GetFileNameWithoutExtension(safeFileName)}-{Guid.NewGuid():N}.pdf";
        var absoluteDirectory = Path.Combine(environment.ContentRootPath, rootPath, relativeDirectory);
        Directory.CreateDirectory(absoluteDirectory);
        var absolutePath = Path.Combine(absoluteDirectory, storedFileName);

        await using (var target = new FileStream(absolutePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await file.CopyToAsync(target, cancellationToken);
        }

        var magic = new byte[5];
        await using (var validate = File.OpenRead(absolutePath))
        {
            var read = await validate.ReadAsync(magic.AsMemory(0, magic.Length), cancellationToken);
            if (read < 5 || System.Text.Encoding.ASCII.GetString(magic) != "%PDF-")
            {
                File.Delete(absolutePath);
                throw new InvalidOperationException("Uploaded file is not a valid PDF.");
            }
        }

        await using var hashStream = File.OpenRead(absolutePath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken)).ToLowerInvariant();
        return new TransactionReupStoredFile(originalFileName, storedFileName, Path.Combine(relativeDirectory, storedFileName), file.Length, "application/pdf", extension, hash);
    }

    public Task<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, rootPath));
        var normalizedRelativePath = NormalizeStoredPath(relativePath);
        var candidate = Path.GetFullPath(Path.Combine(root, normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(candidate))
        {
            return Task.FromResult<Stream?>(null);
        }

        return Task.FromResult<Stream?>(new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    private static string NormalizeStoredPath(string relativePath)
    {
        var normalized = relativePath.TrimStart('/', '\\');
        const string legacyPrefix = "transaction-reup-imports";
        if (normalized.StartsWith(legacyPrefix + "/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(legacyPrefix + "\\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[(legacyPrefix.Length + 1)..];
        }

        return normalized;
    }

    private static string SanitizePathPart(string value)
    {
        var safe = new string((value ?? string.Empty).Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-').ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(safe) ? "file" : safe;
    }
}
