using System.Net;

namespace StarlinkDeviceManager.Services;

public class TelegramNotificationService(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<TelegramNotificationService> logger) : ITelegramNotificationService
{
    public async Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        var section = configuration.GetSection("Telegram");
        if (!section.GetValue("Enabled", false))
        {
            return;
        }

        var botToken = section["BotToken"]?.Trim();
        var chatIds = GetChatIds(section);
        if (string.IsNullOrWhiteSpace(botToken) || chatIds.Count == 0)
        {
            logger.LogWarning("Telegram notification is enabled but BotToken or ChatIds is not configured.");
            return;
        }

        var client = httpClientFactory.CreateClient();
        var url = $"https://api.telegram.org/bot{WebUtility.UrlEncode(botToken)}/sendMessage";

        foreach (var chatId in chatIds)
        {
            try
            {
                var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["chat_id"] = chatId,
                    ["text"] = message,
                    ["parse_mode"] = "HTML",
                    ["disable_web_page_preview"] = "true"
                });

                using var response = await client.PostAsync(url, content, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                    logger.LogWarning("Telegram notification to chat {ChatId} failed with status {StatusCode}: {Response}", chatId, (int)response.StatusCode, responseText);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Telegram notification to chat {ChatId} failed.", chatId);
            }
        }
    }

    private static List<string> GetChatIds(IConfigurationSection section)
    {
        var chatIds = new List<string>();

        foreach (var child in section.GetSection("ChatIds").GetChildren())
        {
            AddChatIds(chatIds, child.Value);
        }

        AddChatIds(chatIds, section["ChatId"]);

        return chatIds
            .Where(chatId => !string.IsNullOrWhiteSpace(chatId))
            .Select(chatId => chatId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddChatIds(List<string> chatIds, string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return;
        }

        chatIds.AddRange(rawValue.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
