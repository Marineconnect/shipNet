using Microsoft.Extensions.Options;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public sealed class LocalInvoicePdfStorage(
    IOptions<InvoicePdfStorageOptions> options,
    IWebHostEnvironment environment) : IInvoicePdfStorage
{
    private readonly InvoicePdfStorageOptions settings = options.Value;

    public async Task<string> SaveAsync(Stream source, string storageKey, CancellationToken cancellationToken = default)
    {
        var root = ResolveRootPath();
        var fullPath = ResolveSafePath(root, storageKey);
        var directory = Path.GetDirectoryName(fullPath) ?? root;
        Directory.CreateDirectory(directory);

        var tempDirectory = Path.Combine(root, ".tmp");
        Directory.CreateDirectory(tempDirectory);
        var tempPath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var destination = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            File.Move(tempPath, fullPath);
            return storageKey.Replace('\\', '/');
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var root = ResolveRootPath();
        var fullPath = ResolveSafePath(root, storageKey);
        Stream? stream = File.Exists(fullPath)
            ? new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan)
            : null;
        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var root = ResolveRootPath();
        var fullPath = ResolveSafePath(root, storageKey);
        return Task.FromResult(File.Exists(fullPath));
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var root = ResolveRootPath();
        var fullPath = ResolveSafePath(root, storageKey);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        return Task.CompletedTask;
    }

    private string ResolveRootPath()
    {
        var root = settings.RootPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("InvoicePdfStorage:RootPath is required.");
        }

        var fullRoot = Path.GetFullPath(root);
        var contentRoot = Path.GetFullPath(environment.ContentRootPath);
        var webRoot = string.IsNullOrWhiteSpace(environment.WebRootPath)
            ? string.Empty
            : Path.GetFullPath(environment.WebRootPath);

        if (IsSameOrChild(fullRoot, contentRoot) || (!string.IsNullOrWhiteSpace(webRoot) && IsSameOrChild(fullRoot, webRoot)))
        {
            throw new InvalidOperationException("Invoice PDF storage root must be outside the application source and web root.");
        }

        Directory.CreateDirectory(fullRoot);
        return fullRoot;
    }

    private static string ResolveSafePath(string root, string storageKey)
    {
        var normalizedKey = (storageKey ?? string.Empty).Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalizedKey) || normalizedKey.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invalid invoice PDF storage key.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, normalizedKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSameOrChild(fullPath, root))
        {
            throw new InvalidOperationException("Invalid invoice PDF storage path.");
        }

        return fullPath;
    }

    private static bool IsSameOrChild(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
