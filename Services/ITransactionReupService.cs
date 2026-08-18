using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface ITransactionReupService
{
    Task<IReadOnlyList<TransactionReupBatchViewModel>> GetBatchesAsync(CancellationToken cancellationToken);
    Task<TransactionReupDetailsViewModel?> GetDetailsAsync(int batchId, CancellationToken cancellationToken);
    Task<TransactionReupItemViewModel?> GetItemAsync(int itemId, CancellationToken cancellationToken);
    Task<TransactionReupImportResult> ImportAsync(TransactionReupImportViewModel model, AuthUserRecord user, CancellationToken cancellationToken);
    Task<TransactionReupSelectionResult> CreateFromTransactionSelectionAsync(
        TransactionReupSelectionRequest request,
        AuthUserRecord user,
        int? allowedTenantId,
        int? allowedDeviceId,
        CancellationToken cancellationToken);
    Task RetryFailedAsync(int batchId, AuthUserRecord user, CancellationToken cancellationToken);
    Task RetryItemAsync(int itemId, AuthUserRecord user, CancellationToken cancellationToken);
    Task<bool> RecordWorkerResultAsync(TransactionReupWorkerResultRequest request, CancellationToken cancellationToken);
    Task<string?> GetOriginalFilePathAsync(int batchId, CancellationToken cancellationToken);
}
