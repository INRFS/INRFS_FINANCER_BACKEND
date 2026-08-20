using INRFS.Financer.Application;
using INRFS.Financer.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace INRFS.Financer.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var connection =
            configuration.GetConnectionString("DefaultConnection")
            ?? "Data Source=App_Data/inrfs-financer.db";
        services.AddDbContext<FinancerDbContext>(options =>
        {
            if (connection.Contains("Host=", StringComparison.OrdinalIgnoreCase))
                options.UseNpgsql(
                    connection,
                    postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory")
                );
            else
                options.UseSqlite(connection);
        });
        services.Configure<JwtOptions>(configuration.GetSection("Jwt"));
        services.Configure<OtpOptions>(configuration.GetSection("Otp"));
        services.Configure<AuthDeliveryOptions>(configuration.GetSection("AuthDelivery"));
        services.Configure<StorageOptions>(configuration.GetSection("Storage"));
        services.Configure<SmsGatewayOptions>(configuration.GetSection("SmsGateway"));
        services.AddScoped<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddHttpClient<IAuthMessageSender, AuthMessageSender>();
        services.AddHttpClient<ISmsGateway, WebhookSmsGateway>();
        services.AddHostedService<SmsDeliveryWorker>();
        services.AddHostedService<NotificationReminderWorker>();
        services.AddHostedService<MonthlyBillingClosingWorker>();
        services.AddScoped<IPlatformService, PlatformService>();
        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<DatabaseInitializer>();
        return services;
    }
}
