using System.Text;
using System.Text.Json;
using INRFS.Financer.Application;
using INRFS.Financer.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace INRFS.Financer.API.Controllers;

[Authorize, Route("api/v1/dashboard")]
public sealed class DashboardController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet("financer")]
    public async Task<ActionResult<ApiResult<object>>> Financer(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetDashboardAsync(false, q, user.User, ct));

    [
        Authorize(Roles = "SuperAdmin,Admin,FinanceOfficer,ComplianceOfficer,Auditor"),
        HttpGet("admin")
    ]
    public async Task<ActionResult<ApiResult<object>>> Admin(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetDashboardAsync(true, q, user.User, ct));
}

[Authorize, Route("api/v1/notifications")]
public sealed class NotificationsController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<object>>> List(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetNotificationsAsync(q, user.User, ct));

    [Authorize(Roles = "SuperAdmin,Admin,SupportAgent,FinancerOwner,FinancerManager"), HttpPost]
    public async Task<ActionResult<ApiResult<Notification>>> Create(
        NotificationRequest r,
        CancellationToken ct
    ) => OkResult(await service.CreateNotificationAsync(r, user.User, ct));

    [HttpPost("{id:guid}/read")]
    public async Task<ActionResult<ApiResult<object>>> Read(Guid id, CancellationToken ct)
    {
        await service.MarkNotificationsReadAsync(id, user.User, ct);
        return OkResult<object>(new { });
    }

    [HttpPost("read-all")]
    public async Task<ActionResult<ApiResult<object>>> ReadAll(CancellationToken ct)
    {
        await service.MarkNotificationsReadAsync(null, user.User, ct);
        return OkResult<object>(new { });
    }
}

[Authorize, Route("api/v1/support-tickets")]
public sealed class SupportTicketsController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<object>>> List(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetTicketsAsync(q, user.User, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResult<SupportTicket>>> Get(Guid id, CancellationToken ct) =>
        OkResult(await service.GetTicketAsync(id, user.User, ct));

    [HttpPost]
    public async Task<ActionResult<ApiResult<SupportTicket>>> Create(
        TicketRequest r,
        CancellationToken ct
    ) => OkResult(await service.CreateTicketAsync(r, user.User, ct));

    [HttpPost("{id:guid}/messages")]
    public async Task<ActionResult<ApiResult<SupportTicket>>> Message(
        Guid id,
        TicketMessageRequest r,
        CancellationToken ct
    ) => OkResult(await service.UpdateTicketAsync(id, r, null, user.User, ct));

    [Authorize(Roles = "SuperAdmin,Admin,SupportAgent"), HttpPost("{id:guid}/status")]
    public async Task<ActionResult<ApiResult<SupportTicket>>> Status(
        Guid id,
        TicketStatusRequest r,
        CancellationToken ct
    ) => OkResult(await service.UpdateTicketAsync(id, null, r, user.User, ct));

    [Authorize(Roles = "SuperAdmin,Admin,SupportAgent"), HttpPost("{id:guid}/assign")]
    public async Task<ActionResult<ApiResult<SupportTicket>>> Assign(
        Guid id,
        TicketStatusRequest r,
        CancellationToken ct
    ) => OkResult(await service.UpdateTicketAsync(id, null, r, user.User, ct));
}

[Authorize, Route("api/v1/reports")]
public sealed class ReportsController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet("{name}")]
    public async Task<IActionResult> Report(
        string name,
        [FromQuery] PageQuery q,
        [FromQuery] string format = "json",
        CancellationToken ct = default
    )
    {
        var data = await service.GetReportAsync(name, q, user.User, ct);
        if (!format.Equals("csv", StringComparison.OrdinalIgnoreCase))
            return Ok(new ApiResult<object>(true, data, null, null, HttpContext.TraceIdentifier));
        var csv = ToCsv(data);
        return File(
            Encoding.UTF8.GetBytes(csv),
            "text/csv",
            $"inrfs-{name}-{DateTime.UtcNow:yyyyMMdd}.csv"
        );
    }

    private static string ToCsv(object data)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(data));
        var root = document.RootElement;
        var rows =
            root.ValueKind == JsonValueKind.Array
                ? root
                : root.EnumerateObject()
                    .FirstOrDefault(x => x.Name.Equals("items", StringComparison.OrdinalIgnoreCase))
                    .Value;
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
            return "message\r\n\"No records found\"\r\n";
        var properties = rows[0].EnumerateObject().Select(x => x.Name).ToArray();
        static string Escape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
        var output = new StringBuilder();
        output.AppendLine(string.Join(',', properties.Select(Escape)));
        foreach (var row in rows.EnumerateArray())
            output.AppendLine(
                string.Join(
                    ',',
                    properties.Select(name =>
                    {
                        var value = row.TryGetProperty(name, out var property)
                            ? property.ToString()
                            : "";
                        return Escape(value);
                    })
                )
            );
        return output.ToString();
    }
}

[Authorize, Route("api/v1/settings")]
public sealed class SettingsController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<object>>> List(
        [FromQuery] string? scope,
        CancellationToken ct
    ) => OkResult(await service.GetSettingsAsync(scope, user.User, ct));

    [HttpPut("{scope}/{key}")]
    public async Task<ActionResult<ApiResult<PlatformSetting>>> Save(
        string scope,
        string key,
        SettingRequest r,
        CancellationToken ct
    ) => OkResult(await service.SaveSettingAsync(scope, key, r, user.User, ct));
}

[Authorize(Roles = "SuperAdmin,Admin,Auditor"), Route("api/v1/audit-logs")]
public sealed class AuditLogsController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<object>>> List(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetAuditLogsAsync(q, user.User, ct));

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ApiResult<AuditLog>>> Get(long id, CancellationToken ct) =>
        OkResult(await service.GetAuditLogAsync(id, user.User, ct));
}
