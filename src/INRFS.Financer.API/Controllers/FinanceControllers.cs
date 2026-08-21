using INRFS.Financer.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace INRFS.Financer.API.Controllers;

[Authorize, Route("api/v1/payments")]
public sealed class PaymentsController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<PagedResult<PaymentDto>>>> List(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetPaymentsAsync(q, user.User, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResult<PaymentDto>>> Get(Guid id, CancellationToken ct) =>
        OkResult(await service.GetPaymentAsync(id, user.User, ct));

    [HttpPost]
    public async Task<ActionResult<ApiResult<PaymentDto>>> Record(
        RecordPaymentRequest r,
        CancellationToken ct
    ) => OkResult(await service.RecordPaymentAsync(r, user.User, ct));

    [HttpGet("settlement-quote/{loanId:guid}")]
    public async Task<ActionResult<ApiResult<SettlementQuoteDto>>> SettlementQuote(
        Guid loanId,
        [FromQuery] DateOnly date,
        CancellationToken ct
    ) => OkResult(await service.GetSettlementQuoteAsync(loanId, date, user.User, ct));

    [
        Authorize(Roles = "SuperAdmin,Admin,FinanceOfficer,FinancerOwner"),
        HttpPost("{id:guid}/reverse")
    ]
    public async Task<ActionResult<ApiResult<PaymentDto>>> Reverse(
        Guid id,
        ReversePaymentRequest r,
        CancellationToken ct
    ) => OkResult(await service.ReversePaymentAsync(id, r, user.User, ct));
}

[Authorize, Route("api/v1/payment-schedules")]
public sealed class PaymentSchedulesController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<object>>> List(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetSchedulesAsync(q, user.User, ct));

    [HttpPost("{id:guid}/reschedule")]
    public async Task<ActionResult<ApiResult<ScheduleDto>>> Reschedule(
        Guid id,
        ReschedulePaymentRequest r,
        CancellationToken ct
    ) => OkResult(await service.RescheduleAsync(id, r, user.User, ct));
}

[Authorize, Route("api/v1/transactions")]
public sealed class TransactionsController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<object>>> List(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetTransactionsAsync(q, user.User, ct));

    [
        Authorize(Roles = "SuperAdmin,Admin,FinanceOfficer,FinancerOwner"),
        HttpPost("{id:guid}/reconcile")
    ]
    public async Task<ActionResult<ApiResult<object>>> Reconcile(
        Guid id,
        [FromBody] ReconcileRequest r,
        CancellationToken ct
    ) => OkResult(await service.ReconcileTransactionAsync(id, r.ExternalReference, user.User, ct));
}

public sealed record ReconcileRequest(string ExternalReference);

[Authorize, Route("api/v1/collections")]
public sealed class CollectionsController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<object>>> List(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetCollectionsAsync(q, user.User, ct));

    [HttpPost("{loanId:guid}/actions")]
    public async Task<ActionResult<ApiResult<object>>> Action(
        Guid loanId,
        CollectionActionRequest r,
        CancellationToken ct
    ) => OkResult(await service.AddCollectionActionAsync(loanId, r, user.User, ct));

    [HttpPost("{loanId:guid}/reminders")]
    public async Task<ActionResult<ApiResult<object>>> Reminder(
        Guid loanId,
        CollectionActionRequest request,
        CancellationToken ct
    ) =>
        OkResult(
            await service.AddCollectionActionAsync(
                loanId,
                request with
                {
                    Type = "PaymentReminder",
                },
                user.User,
                ct
            )
        );
}

[Authorize, Route("api/v1/overdue-loans")]
public sealed class OverdueLoansController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<object>>> List(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetCollectionsAsync(q, user.User, ct));
}
