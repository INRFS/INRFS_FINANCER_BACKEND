using System.Net.Http.Json;
using INRFS.Financer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace INRFS.Financer.Infrastructure;

public interface ISmsGateway
{
    bool IsEnabled { get; }
    Task<string> SendAsync(string destination, string message, string messageType, CancellationToken ct);
}

public sealed class WebhookSmsGateway(HttpClient client, IOptions<SmsGatewayOptions> options) : ISmsGateway
{
    private readonly SmsGatewayOptions _options = options.Value;
    public bool IsEnabled => _options.Provider.Equals("Webhook", StringComparison.OrdinalIgnoreCase);

    public async Task<string> SendAsync(string destination, string message, string messageType, CancellationToken ct)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(_options.WebhookUrl))
            throw new InvalidOperationException("SMS webhook provider is not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.WebhookUrl)
        {
            Content = JsonContent.Create(new { destination, message, messageType }),
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            request.Headers.TryAddWithoutValidation("X-API-Key", _options.ApiKey);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var receipt = await response.Content.ReadFromJsonAsync<SmsGatewayReceipt>(cancellationToken: ct);
        if (!string.IsNullOrWhiteSpace(receipt?.ProviderReference)) return receipt.ProviderReference;
        if (response.Headers.TryGetValues("X-Provider-Reference", out var values))
            return values.FirstOrDefault() ?? Guid.NewGuid().ToString("N");
        return Guid.NewGuid().ToString("N");
    }

    private sealed record SmsGatewayReceipt(string? ProviderReference);
}

public sealed class SmsDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ISmsGateway gateway,
    IOptions<SmsGatewayOptions> options,
    ILogger<SmsDeliveryWorker> logger
) : BackgroundService
{
    private readonly SmsGatewayOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!gateway.IsEnabled)
        {
            logger.LogInformation("SMS delivery worker is disabled; queued records will remain available for a configured provider.");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DispatchBatchAsync(stoppingToken); }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(error, "SMS delivery batch failed.");
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, _options.PollIntervalSeconds)), stoppingToken);
        }
    }

    private async Task DispatchBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinancerDbContext>();
        var deliveries = await db.SmsDeliveries
            .Where(item => item.Status == "Queued")
            .OrderBy(item => item.CreatedAt)
            .Take(Math.Clamp(_options.BatchSize, 1, 100))
            .ToListAsync(ct);
        foreach (var delivery in deliveries)
        {
            var customer = delivery.CustomerId.HasValue ? await db.Customers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == delivery.CustomerId, ct) : null;
            var notification = delivery.NotificationId.HasValue ? await db.Notifications.AsNoTracking().SingleOrDefaultAsync(item => item.Id == delivery.NotificationId, ct) : null;
            if (customer is null)
            {
                delivery.Status = "Failed";
                continue;
            }
            var message = notification?.Message ?? "Payment reminder: please review your outstanding INRFS loan payment.";
            try
            {
                delivery.ProviderReference = await gateway.SendAsync(customer.Phone, message, delivery.MessageType, ct);
                delivery.Status = "Delivered";
                delivery.DeliveredAt = DateTimeOffset.UtcNow;
            }
            catch (Exception error)
            {
                delivery.Status = "Failed";
                logger.LogWarning(error, "SMS delivery {DeliveryId} failed.", delivery.Id);
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
