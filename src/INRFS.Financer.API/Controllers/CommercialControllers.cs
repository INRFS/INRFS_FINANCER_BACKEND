using INRFS.Financer.Application;
using INRFS.Financer.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace INRFS.Financer.API.Controllers;

[Authorize, Route("api/v1/service-charges")]
public sealed class ServiceChargesController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet("invoices")]
    public async Task<ActionResult<ApiResult<object>>> Invoices(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetBillingAsync(q, user.User, ct));

    [Authorize(Roles = "SuperAdmin,Admin,FinanceOfficer"), HttpPost("invoices/generate")]
    public async Task<ActionResult<ApiResult<ServiceChargeInvoice>>> Generate(
        GenerateInvoiceRequest r,
        CancellationToken ct
    ) => OkResult(await service.GenerateInvoiceAsync(r, user.User, ct));

    [Authorize(Roles = "SuperAdmin,Admin,FinanceOfficer"), HttpPost("invoices/{id:guid}/collect")]
    public async Task<ActionResult<ApiResult<ServiceChargeInvoice>>> Collect(
        Guid id,
        CollectInvoiceRequest r,
        CancellationToken ct
    ) => OkResult(await service.CollectInvoiceAsync(id, r, user.User, ct));

    [Authorize(Roles = "SuperAdmin,Admin,FinanceOfficer"), HttpPost("invoices/{id:guid}/credit-note")]
    public async Task<ActionResult<ApiResult<ServiceChargeInvoice>>> CreditNote(
        Guid id,
        AdjustInvoiceRequest r,
        CancellationToken ct
    ) => OkResult(await service.AdjustInvoiceAsync(id, r, user.User, ct));
}

[Authorize, Route("api/v1/monthly-billing")]
public sealed class MonthlyBillingController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<object>>> List(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetBillingAsync(q, user.User, ct));
}

[Authorize, Route("api/v1/subscriptions")]
public sealed class SubscriptionsController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<object>>> List(CancellationToken ct) =>
        OkResult(await service.GetSubscriptionsAsync(user.User, ct));

    [Authorize(Roles = "SuperAdmin,Admin"), HttpPost("plans")]
    public async Task<ActionResult<ApiResult<SubscriptionPlan>>> CreatePlan(
        SubscriptionPlanRequest r,
        CancellationToken ct
    ) => OkResult(await service.SaveSubscriptionPlanAsync(null, r, user.User, ct));

    [Authorize(Roles = "SuperAdmin,Admin"), HttpPut("plans/{id:guid}")]
    public async Task<ActionResult<ApiResult<SubscriptionPlan>>> UpdatePlan(
        Guid id,
        SubscriptionPlanRequest r,
        CancellationToken ct
    ) => OkResult(await service.SaveSubscriptionPlanAsync(id, r, user.User, ct));

    [Authorize(Roles = "SuperAdmin,Admin"), HttpPost("assign")]
    public async Task<ActionResult<ApiResult<FinancerSubscription>>> Assign(
        AssignSubscriptionRequest r,
        CancellationToken ct
    ) => OkResult(await service.AssignSubscriptionAsync(r, user.User, ct));
}

[
    Authorize(Roles = "SuperAdmin,Admin,SupportAgent,FinanceOfficer,Auditor"),
    Route("api/v1/sms-management")
]
public sealed class SmsManagementController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet("usage")]
    public async Task<ActionResult<ApiResult<object>>> Usage(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetSmsUsageAsync(q, user.User, ct));
}
