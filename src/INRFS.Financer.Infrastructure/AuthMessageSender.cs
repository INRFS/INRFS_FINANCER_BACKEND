using System.Net.Http.Json;
using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace INRFS.Financer.Infrastructure;

public interface IAuthMessageSender
{
    Task SendOtpAsync(string destination, string code, string purpose, CancellationToken ct);
    Task SendPasswordResetAsync(string destination, string token, CancellationToken ct);
    Task SendWelcomeCredentialsAsync(string destination, string userId, string password, CancellationToken ct);
}

public sealed class AuthMessageSender(
    HttpClient httpClient,
    IOptions<AuthDeliveryOptions> options,
    IHostEnvironment environment,
    ILogger<AuthMessageSender> logger
) : IAuthMessageSender
{
    private readonly AuthDeliveryOptions _options = options.Value;

    public Task SendOtpAsync(string destination, string code, string purpose, CancellationToken ct) =>
        SendAsync(destination, "Otp", new { code, purpose }, ct);

    public Task SendPasswordResetAsync(string destination, string token, CancellationToken ct) =>
        SendAsync(destination, "PasswordReset", new { token, resetUrl = _options.PasswordResetUrl }, ct);

    public Task SendWelcomeCredentialsAsync(string destination, string userId, string password, CancellationToken ct) =>
        SendAsync(destination, "WelcomeCredentials", new { userId, password }, ct);

    private async Task SendAsync(string destination, string type, object payload, CancellationToken ct)
    {
        if (_options.Provider.Equals("Smtp", StringComparison.OrdinalIgnoreCase))
        {
            await SendSmtpAsync(destination, type, payload, ct);
            return;
        }
        if (_options.Provider.Equals("Development", StringComparison.OrdinalIgnoreCase))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("AuthDelivery must use a production provider outside Development.");
            logger.LogWarning("Development auth delivery to {Destination}: {Type} {Payload}", destination, type, payload);
            if (!string.IsNullOrWhiteSpace(_options.DevelopmentOutputPath))
            {
                var fullPath = Path.GetFullPath(_options.DevelopmentOutputPath);
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                await File.AppendAllTextAsync(fullPath, JsonSerializer.Serialize(new { destination, type, payload, createdAt = DateTimeOffset.UtcNow }) + Environment.NewLine, ct);
            }
            return;
        }

        if (!_options.Provider.Equals("Webhook", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(_options.WebhookUrl))
            throw new InvalidOperationException("AuthDelivery provider configuration is invalid.");

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.WebhookUrl)
        {
            Content = JsonContent.Create(new { destination, type, payload }),
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            request.Headers.TryAddWithoutValidation("X-API-Key", _options.ApiKey);
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task SendSmtpAsync(string destination, string type, object payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.SmtpHost) || string.IsNullOrWhiteSpace(_options.SmtpFrom))
            throw new InvalidOperationException("SMTP auth delivery requires SmtpHost and SmtpFrom.");

        var (subject, body) = type switch
        {
            "Otp" => ("Your INRFS verification code", $"Your INRFS verification code is {Read(payload, "code")}. It expires soon."),
            "PasswordReset" => ("Reset your INRFS password", $"Open this secure link to reset your INRFS password: {BuildResetLink(payload)}\n\nThis link expires in 15 minutes. If you did not request it, you can ignore this email."),
            "WelcomeCredentials" => ("Welcome to INRFS", $"Your INRFS account is ready. User ID: {Read(payload, "userId")}. Temporary password: {Read(payload, "password")}"),
            _ => ("INRFS notification", JsonSerializer.Serialize(payload))
        };

        using var message = new MailMessage
        {
            From = new MailAddress(_options.SmtpFrom, _options.SmtpFromName),
            Subject = subject,
            Body = body,
            IsBodyHtml = false,
        };
        message.To.Add(destination);
        using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
        {
            EnableSsl = _options.SmtpEnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        if (!string.IsNullOrWhiteSpace(_options.SmtpUsername))
            client.Credentials = new NetworkCredential(_options.SmtpUsername, _options.SmtpPassword);
        ct.ThrowIfCancellationRequested();
        await client.SendMailAsync(message, ct);
    }

    private static string Read(object payload, string property) =>
        payload.GetType().GetProperty(property)?.GetValue(payload)?.ToString() ?? "";

    private string BuildResetLink(object payload)
    {
        var separator = _options.PasswordResetUrl.Contains('?') ? "&" : "?";
        return $"{_options.PasswordResetUrl}{separator}token={Uri.EscapeDataString(Read(payload, "token"))}";
    }
}
