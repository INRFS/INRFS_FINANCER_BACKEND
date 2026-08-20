namespace INRFS.Financer.Infrastructure;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "INRFS.Financer.API";
    public string Audience { get; set; } = "INRFS.Financer.Client";
    public string Key { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;
}

public sealed class OtpOptions
{
    public int ExpiryMinutes { get; set; } = 5;
    public int MaximumAttempts { get; set; } = 5;
    public int MinimumResendSeconds { get; set; } = 60;
    public string? FixedDevelopmentCode { get; set; }
}

public sealed class AuthDeliveryOptions
{
    public string Provider { get; set; } = "Development";
    public string? WebhookUrl { get; set; }
    public string? ApiKey { get; set; }
    public string PasswordResetUrl { get; set; } = "http://localhost:5173/reset-password";
    public string? DevelopmentOutputPath { get; set; }
}

public sealed class StorageOptions
{
    public string RootPath { get; set; } = "App_Data/documents";
    public long MaximumFileSizeBytes { get; set; } = 10_485_760;
    public string[] AllowedContentTypes { get; set; } =
    ["application/pdf", "image/jpeg", "image/png"];
}

public sealed class SmsGatewayOptions
{
    public string Provider { get; set; } = "Disabled";
    public string? WebhookUrl { get; set; }
    public string? ApiKey { get; set; }
    public int PollIntervalSeconds { get; set; } = 10;
    public int BatchSize { get; set; } = 25;
}
