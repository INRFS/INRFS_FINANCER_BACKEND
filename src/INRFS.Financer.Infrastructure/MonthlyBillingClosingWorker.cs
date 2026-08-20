using INRFS.Financer.Application;
using INRFS.Financer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace INRFS.Financer.Infrastructure;

public sealed class MonthlyBillingClosingWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<MonthlyBillingClosingWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CloseLatestCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to close the monthly billing cycle");
            }

            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }

    private async Task CloseLatestCycleAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinancerDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IPlatformService>();
        var indiaTimeZone = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata"
        );
        var indiaNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, indiaTimeZone);
        var cycle = MonthlyBillingCycle.LatestClosed(DateOnly.FromDateTime(indiaNow.DateTime));
        var financerIds = await db.Financers.AsNoTracking()
            .Where(x => x.Status == AccountStatus.Active)
            .Select(x => x.Id)
            .ToListAsync(ct);
        var systemActor = new CurrentUser(
            Guid.Empty,
            null,
            ["System"],
            ["settings.manage"]
        );

        foreach (var financerId in financerIds)
        {
            try
            {
                var periodInvoices = await db.ServiceChargeInvoices.AsNoTracking()
                    .Where(x => x.FinancerId == financerId
                        && x.PeriodEnd.Year == cycle.PeriodEnd.Year
                        && x.PeriodEnd.Month == cycle.PeriodEnd.Month)
                    .Select(x => new { x.PeriodStart, x.PeriodEnd })
                    .ToListAsync(ct);
                var hasLegacyMonthlyStatement = periodInvoices.Count > 0
                    && !periodInvoices.Any(x => x.PeriodStart == cycle.PeriodStart && x.PeriodEnd == cycle.PeriodEnd);
                if (hasLegacyMonthlyStatement)
                {
                    logger.LogInformation(
                        "Skipped 25th-cycle conversion for financer {FinancerId}; a legacy statement already exists for {BillingMonth}",
                        financerId,
                        cycle.PeriodEnd.ToString("yyyy-MM")
                    );
                    continue;
                }
                await service.GenerateInvoiceAsync(
                    new GenerateInvoiceRequest(financerId, cycle.PeriodStart, cycle.PeriodEnd, cycle.DueDate),
                    systemActor,
                    ct
                );
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to close billing for financer {FinancerId}", financerId);
            }
        }
    }
}
