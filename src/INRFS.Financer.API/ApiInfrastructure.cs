using System.Diagnostics;
using System.Security.Claims;
using FluentValidation;
using INRFS.Financer.Application;
using INRFS.Financer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace INRFS.Financer.API;

public interface ICurrentUserAccessor
{
    CurrentUser User { get; }
}

public sealed class CurrentUserAccessor(IHttpContextAccessor context) : ICurrentUserAccessor
{
    public CurrentUser User
    {
        get
        {
            var p =
                context.HttpContext?.User
                ?? throw new DomainException("Authentication context is unavailable.", 401);
            var id = p.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(id, out var userId))
                throw new DomainException("Authentication is required.", 401);
            Guid? financer = null;
            if (Guid.TryParse(p.FindFirstValue("financer_id"), out var f))
                financer = f;
            return new(
                userId,
                financer,
                p.FindAll(ClaimTypes.Role).Select(x => x.Value).ToArray(),
                p.FindAll("permission").Select(x => x.Value).ToArray()
            );
        }
    }
}

public sealed class CorrelationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlation =
            context.Request.Headers["X-Correlation-ID"].FirstOrDefault()
            ?? Guid.NewGuid().ToString("N");
        context.TraceIdentifier = correlation;
        context.Response.Headers["X-Correlation-ID"] = correlation;
        using var activity = new Activity("INRFS.Request").Start();
        await next(context);
    }
}

public sealed class ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger, IHostEnvironment environment)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var (status, message, errors) = ex switch
            {
                ValidationException v => (
                    400,
                    "Validation failed.",
                    v.Errors.GroupBy(x => x.PropertyName)
                        .ToDictionary(x => x.Key, x => x.Select(e => e.ErrorMessage).ToArray())
                ),
                DomainException d => (d.StatusCode, d.Message, (Dictionary<string, string[]>?)null),
                DbUpdateConcurrencyException => (
                    409,
                    "This payment schedule changed while you were recording the payment. Refresh the dues and verify whether the payment was already saved before trying again.",
                    (Dictionary<string, string[]>?)null
                ),
                _ => (
                    500,
                    environment.IsDevelopment() ? ex.Message : "An unexpected error occurred.",
                    (Dictionary<string, string[]>?)null
                ),
            };
            if (status >= 500)
                logger.LogError(
                    ex,
                    "Unhandled request error. TraceId {TraceId}",
                    context.TraceIdentifier
                );
            else
                logger.LogWarning(
                    ex,
                    "Request rejected. TraceId {TraceId}",
                    context.TraceIdentifier
                );
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(
                new ApiResult<object>(false, null, message, errors, context.TraceIdentifier)
            );
        }
    }
}

public sealed class DatabaseHealthCheck(FinancerDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default
    ) =>
        await db.Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy("Database connection succeeded.")
            : HealthCheckResult.Unhealthy("Database connection failed.");
}
