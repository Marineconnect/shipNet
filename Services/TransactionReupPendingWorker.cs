namespace StarlinkDeviceManager.Services;

public sealed class TransactionReupPendingWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<TransactionReupPendingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = Math.Clamp(configuration.GetValue("TransactionReup:PendingWorkerIntervalSeconds", 15), 5, 300);
        var delay = TimeSpan.FromSeconds(intervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ITransactionReupService>();
                var processed = await service.ProcessPendingAsync(stoppingToken);
                if (processed > 0)
                {
                    logger.LogInformation("Transaction Reup pending worker processed {Count} item(s).", processed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (InvalidOperationException exception)
            {
                logger.LogDebug(exception, "Transaction Reup pending worker skipped this cycle.");
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Transaction Reup pending worker failed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}
