using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public class InvoiceRabbitMqPublisher(
    IOptionsMonitor<InvoiceRabbitMqOptions> options,
    ILogger<InvoiceRabbitMqPublisher> logger) : IInvoiceRabbitMqPublisher, IDisposable
{
    private static readonly JsonSerializerOptions EventJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly SemaphoreSlim channelLock = new(1, 1);
    private IConnection? connection;
    private IModel? channel;
    private bool disposed;

    public string GetConfigurationSummary()
    {
        var settings = ResolveSettings();
        return $"Enabled={settings.Enabled}; Host={settings.HostName}:{settings.Port}; VHost={settings.VirtualHost}; SSL={settings.UseSsl}; Exchange={ValueOrDefault(settings.ExchangeName)}; Queue={ValueOrDefault(settings.QueueName)}; RoutingKey={ValueOrDefault(settings.RoutingKey)}; DeclareExchange={settings.DeclareExchange}; DeclareQueue={settings.DeclareQueue}; BindQueue={settings.BindQueue}; Confirms={settings.PublisherConfirms}; RetryCount={settings.RetryCount}";
    }

    public async Task<InvoiceRabbitMqPublishResult> PublishInvoiceAsync(InvoiceRabbitMqPublishRequest request, CancellationToken cancellationToken = default)
    {
        var result = new InvoiceRabbitMqPublishResult();
        var messageId = Guid.NewGuid().ToString();

        void AddLog(string message)
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} | {message}";
            result.Logs.Add(line);
            logger.LogInformation("Invoice RabbitMQ test: {Message}", message);
        }

        try
        {
            AddLog("STEP 1 - Validate invoice JSON payload.");
            using var jsonDocument = JsonDocument.Parse(request.InvoiceJson);
            var payload = JsonSerializer.Serialize(jsonDocument.RootElement, EventJsonOptions);
            var settings = ResolveSettings();
            var routingKey = ResolveRoutingKey(settings, request.RoutingKeyOverride);

            AddLog($"STEP 2 - Publish raw invoice JSON. Host={settings.HostName}:{settings.Port}; VHost={settings.VirtualHost}; Queue={settings.QueueName}; RoutingKey={routingKey}; MessageId={messageId}.");
            await PublishPayloadWithRetryAsync(
                settings,
                payload,
                messageId,
                messageId,
                routingKey,
                request.Username,
                request.UserId,
                result.Logs,
                cancellationToken);

            result.Success = true;
            result.MessageId = messageId;
            result.Message = $"Published invoice test message successfully. MessageId={messageId}.";
            AddLog(result.Message);
        }
        catch (JsonException exception)
        {
            result.Success = false;
            result.Message = $"Invoice JSON is invalid: {exception.Message}";
            AddLog($"FAILED - {result.Message}");
            logger.LogWarning(exception, "Invoice RabbitMQ test failed because payload JSON is invalid.");
        }
        catch (Exception exception)
        {
            result.Success = false;
            result.Message = $"Cannot publish invoice message: {exception.GetBaseException().Message}";
            AddLog($"FAILED - {result.Message}");
            logger.LogError(exception, "Invoice RabbitMQ test failed.");
        }

        return result;
    }

    public async Task<InvoiceRabbitMqPublishResult> PublishInvoiceGenerateEventAsync(InvoiceGenerateEvent invoiceEvent, CancellationToken cancellationToken = default)
    {
        var result = new InvoiceRabbitMqPublishResult
        {
            MessageId = invoiceEvent.EventId
        };

        var settings = ResolveSettings();
        var payload = JsonSerializer.Serialize(invoiceEvent, EventJsonOptions);
        var routingKey = ResolveRoutingKey(settings, string.Empty);

        try
        {
            await PublishPayloadWithRetryAsync(
                settings,
                payload,
                invoiceEvent.EventId,
                invoiceEvent.InvoiceId.ToString(),
                routingKey,
                "system",
                null,
                result.Logs,
                cancellationToken);

            result.Success = true;
            result.Message = $"Published invoice.generate event. invoiceId={invoiceEvent.InvoiceId}; eventId={invoiceEvent.EventId}; queue={settings.QueueName}; publishedAt={DateTimeOffset.Now:O}.";
            logger.LogInformation(
                "Published invoice.generate event. InvoiceId={InvoiceId}; EventId={EventId}; Queue={Queue}; PublishedAt={PublishedAt}.",
                invoiceEvent.InvoiceId,
                invoiceEvent.EventId,
                settings.QueueName,
                DateTimeOffset.Now);
        }
        catch (Exception exception)
        {
            result.Success = false;
            result.Message = $"Failed to publish invoice.generate event. invoiceId={invoiceEvent.InvoiceId}; eventId={invoiceEvent.EventId}; queue={settings.QueueName}; reason={exception.GetBaseException().Message}.";
            logger.LogError(
                exception,
                "Failed to publish invoice.generate event. InvoiceId={InvoiceId}; EventId={EventId}; Host={Host}; Port={Port}; VHost={VirtualHost}; Queue={Queue}; Reason={Reason}.",
                invoiceEvent.InvoiceId,
                invoiceEvent.EventId,
                settings.HostName,
                settings.Port,
                settings.VirtualHost,
                settings.QueueName,
                exception.GetBaseException().Message);
        }

        return result;
    }

    private async Task PublishPayloadWithRetryAsync(
        InvoiceRabbitMqOptions settings,
        string payload,
        string messageId,
        string correlationId,
        string routingKey,
        string requestedBy,
        int? requestedUserId,
        List<string> logs,
        CancellationToken cancellationToken)
    {
        ValidateSettings(settings);

        var retryCount = Math.Max(1, settings.RetryCount);
        var baseDelay = Math.Max(100, settings.RetryBaseDelayMilliseconds);
        Exception? lastException = null;
        var stopwatch = Stopwatch.StartNew();

        for (var attempt = 1; attempt <= retryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                AddLog(logs, $"Attempt {attempt}/{retryCount}: connect/publish to RabbitMQ. Host={settings.HostName}; Port={settings.Port}; VHost={settings.VirtualHost}; Queue={settings.QueueName}; RoutingKey={routingKey}.");
                var publishDiagnostics = await PublishPayloadOnceAsync(settings, payload, messageId, correlationId, routingKey, requestedBy, requestedUserId, cancellationToken);
                AddLog(logs, $"Attempt {attempt}/{retryCount}: publish succeeded. Elapsed={stopwatch.ElapsedMilliseconds}ms.");
                AddLog(logs, publishDiagnostics);
                return;
            }
            catch (Exception exception) when (attempt < retryCount)
            {
                lastException = exception;
                ResetChannel();

                var delay = TimeSpan.FromMilliseconds(baseDelay * Math.Pow(2, attempt - 1));
                AddLog(logs, $"Attempt {attempt}/{retryCount}: publish failed. Host={settings.HostName}; Port={settings.Port}; VHost={settings.VirtualHost}; Queue={settings.QueueName}; Reason={exception.GetBaseException().Message}; NextRetryIn={delay.TotalMilliseconds:0}ms.");
                logger.LogWarning(
                    exception,
                    "RabbitMQ invoice publish failed on attempt {Attempt}/{RetryCount}. Host={Host}; Port={Port}; VHost={VirtualHost}; Queue={Queue}; NextRetryInMs={NextRetryInMs}.",
                    attempt,
                    retryCount,
                    settings.HostName,
                    settings.Port,
                    settings.VirtualHost,
                    settings.QueueName,
                    delay.TotalMilliseconds);

                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception exception)
            {
                lastException = exception;
                ResetChannel();
                AddLog(logs, $"Attempt {attempt}/{retryCount}: publish failed permanently. Host={settings.HostName}; Port={settings.Port}; VHost={settings.VirtualHost}; Queue={settings.QueueName}; Reason={exception.GetBaseException().Message}.");
                break;
            }
        }

        throw lastException ?? new InvalidOperationException("RabbitMQ invoice publish failed.");
    }

    private async Task<string> PublishPayloadOnceAsync(
        InvoiceRabbitMqOptions settings,
        string payload,
        string messageId,
        string correlationId,
        string routingKey,
        string requestedBy,
        int? requestedUserId,
        CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(payload);

        await channelLock.WaitAsync(cancellationToken);
        try
        {
            var activeChannel = EnsureChannel(settings);
            var properties = activeChannel.CreateBasicProperties();
            properties.ContentType = "application/json";
            properties.ContentEncoding = "utf-8";
            properties.MessageId = messageId;
            properties.CorrelationId = correlationId;
            properties.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            properties.Type = "invoice.generate";
            properties.AppId = "shipNet";
            properties.Headers = new Dictionary<string, object>
            {
                ["x-requested-by"] = requestedBy,
                ["x-requested-user-id"] = requestedUserId?.ToString() ?? string.Empty
            };

            if (settings.PersistentMessages)
            {
                properties.Persistent = true;
            }

            activeChannel.BasicPublish(
                settings.ExchangeName?.Trim() ?? string.Empty,
                routingKey,
                mandatory: true,
                basicProperties: properties,
                body: body);

            if (settings.PublisherConfirms)
            {
                var confirmed = activeChannel.WaitForConfirms(TimeSpan.FromSeconds(Math.Max(1, settings.ConnectionTimeoutSeconds)));
                if (!confirmed)
                {
                    throw new TimeoutException("RabbitMQ did not confirm the published invoice message within the configured timeout.");
                }
            }

            if (!string.IsNullOrWhiteSpace(settings.QueueName))
            {
                var queueState = activeChannel.QueueDeclarePassive(settings.QueueName.Trim());
                return $"Queue state after publish. Queue={settings.QueueName}; MessageCount={queueState.MessageCount}; ConsumerCount={queueState.ConsumerCount}.";
            }

            return "Queue state after publish was not checked because QueueName is empty.";
        }
        finally
        {
            channelLock.Release();
        }
    }

    private IModel EnsureChannel(InvoiceRabbitMqOptions settings)
    {
        if (connection?.IsOpen != true)
        {
            connection?.Dispose();
            connection = CreateConnection(settings);
        }

        if (channel?.IsOpen == true)
        {
            return channel;
        }

        channel?.Dispose();
        channel = connection.CreateModel();

        if (settings.PublisherConfirms)
        {
            channel.ConfirmSelect();
        }

        if (settings.DeclareExchange && !string.IsNullOrWhiteSpace(settings.ExchangeName))
        {
            channel.ExchangeDeclare(settings.ExchangeName.Trim(), settings.ExchangeType, durable: settings.Durable, autoDelete: false);
        }

        if (settings.DeclareQueue && !string.IsNullOrWhiteSpace(settings.QueueName))
        {
            channel.QueueDeclare(settings.QueueName.Trim(), durable: settings.Durable, exclusive: false, autoDelete: false);
        }

        if (settings.BindQueue && !string.IsNullOrWhiteSpace(settings.QueueName) && !string.IsNullOrWhiteSpace(settings.ExchangeName))
        {
            channel.QueueBind(settings.QueueName.Trim(), settings.ExchangeName.Trim(), ResolveRoutingKey(settings, string.Empty));
        }

        return channel;
    }

    private static IConnection CreateConnection(InvoiceRabbitMqOptions settings)
    {
        var factory = new ConnectionFactory
        {
            HostName = settings.HostName.Trim(),
            Port = settings.Port,
            VirtualHost = NormalizeVirtualHost(settings.VirtualHost),
            UserName = settings.UserName,
            Password = settings.Password,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(Math.Max(1, settings.ConnectionTimeoutSeconds)),
            AutomaticRecoveryEnabled = true,
            ClientProvidedName = $"shipNet-invoice-publisher-{Environment.MachineName}"
        };

        if (settings.UseSsl)
        {
            factory.Ssl.Enabled = true;
            factory.Ssl.ServerName = settings.HostName.Trim();
        }

        return factory.CreateConnection();
    }

    private InvoiceRabbitMqOptions ResolveSettings()
    {
        var settings = Clone(options.CurrentValue);
        ApplyRabbitMqUrl(settings, Environment.GetEnvironmentVariable("RABBITMQ_URL"));
        ApplyEnvironment(settings);
        return settings;
    }

    private static InvoiceRabbitMqOptions Clone(InvoiceRabbitMqOptions source)
    {
        return new InvoiceRabbitMqOptions
        {
            Enabled = source.Enabled,
            HostName = source.HostName,
            Port = source.Port,
            VirtualHost = source.VirtualHost,
            UserName = source.UserName,
            Password = source.Password,
            UseSsl = source.UseSsl,
            ExchangeName = source.ExchangeName,
            ExchangeType = source.ExchangeType,
            QueueName = source.QueueName,
            RoutingKey = source.RoutingKey,
            DeclareExchange = source.DeclareExchange,
            DeclareQueue = source.DeclareQueue,
            BindQueue = source.BindQueue,
            Durable = source.Durable,
            PersistentMessages = source.PersistentMessages,
            PublisherConfirms = source.PublisherConfirms,
            ConnectionTimeoutSeconds = source.ConnectionTimeoutSeconds,
            RetryCount = source.RetryCount,
            RetryBaseDelayMilliseconds = source.RetryBaseDelayMilliseconds
        };
    }

    private static void ApplyRabbitMqUrl(InvoiceRabbitMqOptions settings, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("RABBITMQ_URL is not a valid AMQP URL.");
        }

        settings.HostName = uri.Host;
        settings.Port = uri.Port > 0 ? uri.Port : 5672;
        settings.UseSsl = string.Equals(uri.Scheme, "amqps", StringComparison.OrdinalIgnoreCase);

        var userInfo = uri.UserInfo.Split(':', 2);
        if (userInfo.Length > 0 && !string.IsNullOrWhiteSpace(userInfo[0]))
        {
            settings.UserName = Uri.UnescapeDataString(userInfo[0]);
        }

        if (userInfo.Length > 1)
        {
            settings.Password = Uri.UnescapeDataString(userInfo[1]);
        }

        if (!string.IsNullOrWhiteSpace(uri.AbsolutePath) && uri.AbsolutePath != "/")
        {
            settings.VirtualHost = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        }
    }

    private static void ApplyEnvironment(InvoiceRabbitMqOptions settings)
    {
        settings.HostName = GetEnv("RABBITMQ_HOST", settings.HostName);
        settings.UserName = GetEnv("RABBITMQ_USERNAME", settings.UserName);
        settings.Password = GetEnv("RABBITMQ_PASSWORD", settings.Password);
        settings.VirtualHost = GetEnv("RABBITMQ_VHOST", settings.VirtualHost);
        settings.QueueName = GetEnv("RABBITMQ_QUEUE", settings.QueueName);
        settings.RoutingKey = GetEnv("RABBITMQ_QUEUE", settings.RoutingKey);

        if (int.TryParse(Environment.GetEnvironmentVariable("RABBITMQ_PORT"), out var port))
        {
            settings.Port = port;
        }
    }

    private static void ValidateSettings(InvoiceRabbitMqOptions settings)
    {
        if (!settings.Enabled)
        {
            throw new InvalidOperationException("InvoiceRabbitMq.Enabled is false. Enable it before publishing invoice PDF messages.");
        }

        if (string.IsNullOrWhiteSpace(settings.HostName))
        {
            throw new InvalidOperationException("RabbitMQ host is required.");
        }

        if (settings.Port <= 0)
        {
            throw new InvalidOperationException("RabbitMQ port must be greater than 0.");
        }

        if (string.IsNullOrWhiteSpace(settings.UserName))
        {
            throw new InvalidOperationException("RabbitMQ username is required.");
        }

        if (string.IsNullOrWhiteSpace(settings.Password))
        {
            throw new InvalidOperationException("RabbitMQ password is required. Set RABBITMQ_PASSWORD as a secret environment variable.");
        }

        if (string.IsNullOrWhiteSpace(settings.QueueName))
        {
            throw new InvalidOperationException("RabbitMQ queue is required.");
        }
    }

    private static string ResolveRoutingKey(InvoiceRabbitMqOptions settings, string routingKeyOverride)
    {
        if (string.IsNullOrWhiteSpace(settings.ExchangeName))
        {
            var queueName = settings.QueueName.Trim();
            if (!string.IsNullOrWhiteSpace(routingKeyOverride) &&
                !string.Equals(routingKeyOverride.Trim(), queueName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Default RabbitMQ exchange requires routing key to match queue name '{queueName}'. Leave the override empty or use '{queueName}'.");
            }

            return queueName;
        }

        if (!string.IsNullOrWhiteSpace(routingKeyOverride))
        {
            return routingKeyOverride.Trim();
        }

        if (!string.IsNullOrWhiteSpace(settings.RoutingKey))
        {
            return settings.RoutingKey.Trim();
        }

        return settings.QueueName.Trim();
    }

    private void ResetChannel()
    {
        try
        {
            channel?.Dispose();
        }
        catch (AlreadyClosedException)
        {
        }

        channel = null;

        if (connection?.IsOpen != true)
        {
            connection?.Dispose();
            connection = null;
        }
    }

    private static void AddLog(List<string> logs, string message)
    {
        logs.Add($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} | {message}");
    }

    private static string GetEnv(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string NormalizeVirtualHost(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "/";
        }

        return value == "/" ? "/" : value.Trim().TrimStart('/');
    }

    private static string ValueOrDefault(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(default exchange)" : value;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        channel?.Dispose();
        connection?.Dispose();
        channelLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
