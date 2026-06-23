namespace StarlinkDeviceManager.Services;

public interface ITelegramNotificationService
{
    Task SendMessageAsync(string message, CancellationToken cancellationToken = default);
}
