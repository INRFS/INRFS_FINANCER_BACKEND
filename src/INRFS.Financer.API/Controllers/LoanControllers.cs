using INRFS.Financer.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace INRFS.Financer.API.Controllers;

[Authorize, Route("api/v1/loan-products")]
public sealed class LoanProductsController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<IReadOnlyList<LoanProductDto>>>> List(
        [FromQuery] bool includeInactive,
        CancellationToken ct
    ) => OkResult(await service.GetProductsAsync(includeInactive, ct));

    [Authorize(Roles = "SuperAdmin,Admin"), HttpPost]
    public async Task<ActionResult<ApiResult<LoanProductDto>>> Create(
        LoanProductRequest r,
        CancellationToken ct
    ) => OkResult(await service.SaveProductAsync(null, r, user.User, ct));

    [Authorize(Roles = "SuperAdmin,Admin"), HttpPut("{id:guid}")]
    public async Task<ActionResult<ApiResult<LoanProductDto>>> Update(
        Guid id,
        LoanProductRequest r,
        CancellationToken ct
    ) => OkResult(await service.SaveProductAsync(id, r, user.User, ct));
}

[Authorize, Route("api/v1/eligibility/checks")]
public sealed class EligibilityController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ApiResult<EligibilityDto>>> Check(
        EligibilityRequest r,
        CancellationToken ct
    ) => OkResult(await service.CheckEligibilityAsync(r, user.User, ct));
}

[Authorize, Route("api/v1/loan-applications")]
public sealed class LoanApplicationsController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<PagedResult<LoanApplicationDto>>>> List(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetApplicationsAsync(q, user.User, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResult<LoanApplicationDto>>> Get(
        Guid id,
        CancellationToken ct
    ) => OkResult(await service.GetApplicationAsync(id, user.User, ct));

    [HttpGet("{id:guid}/history")]
    public async Task<ActionResult<ApiResult<object>>> History(Guid id, CancellationToken ct) =>
        OkResult(await service.GetApplicationHistoryAsync(id, user.User, ct));

    [HttpPost]
    public async Task<ActionResult<ApiResult<LoanApplicationDto>>> Create(
        LoanApplicationRequest r,
        CancellationToken ct
    ) => OkResult(await service.CreateApplicationAsync(r, user.User, ct));

    [HttpPost("{id:guid}/submit")]
    public async Task<ActionResult<ApiResult<LoanApplicationDto>>> Submit(
        Guid id,
        CancellationToken ct
    ) => OkResult(await service.TransitionApplicationAsync(id, "submit", null, user.User, ct));

    [
        Authorize(Roles = "SuperAdmin,Admin,ComplianceOfficer,LoanOfficer"),
        HttpPost("{id:guid}/verify")
    ]
    public async Task<ActionResult<ApiResult<LoanApplicationDto>>> Verify(
        Guid id,
        CancellationToken ct
    ) => OkResult(await service.TransitionApplicationAsync(id, "verify", null, user.User, ct));

    [
        Authorize(Roles = "SuperAdmin,Admin,FinanceOfficer,FinancerOwner"),
        HttpPost("{id:guid}/approve")
    ]
    public async Task<ActionResult<ApiResult<LoanApplicationDto>>> Approve(
        Guid id,
        LoanDecisionRequest r,
        CancellationToken ct
    ) => OkResult(await service.TransitionApplicationAsync(id, "approve", r, user.User, ct));

    [
        Authorize(Roles = "SuperAdmin,Admin,FinanceOfficer,FinancerOwner"),
        HttpPost("{id:guid}/reject")
    ]
    public async Task<ActionResult<ApiResult<LoanApplicationDto>>> Reject(
        Guid id,
        RejectLoanRequest r,
        CancellationToken ct
    ) => OkResult(await service.TransitionApplicationAsync(id, "reject", r, user.User, ct));

    [
        Authorize(Roles = "SuperAdmin,Admin,FinanceOfficer,FinancerOwner"),
        HttpPost("{id:guid}/disburse")
    ]
    public async Task<ActionResult<ApiResult<LoanApplicationDto>>> Disburse(
        Guid id,
        DisbursementRequest r,
        CancellationToken ct
    ) => OkResult(await service.TransitionApplicationAsync(id, "disburse", r, user.User, ct));
}

[Authorize, Route("api/v1/loans")]
public sealed class LoansController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ApiResult<LoanDto>>> Create(
        DirectLoanRequest r,
        CancellationToken ct
    ) => OkResult(await service.CreateDirectLoanAsync(r, user.User, ct));

    [HttpGet]
    public async Task<ActionResult<ApiResult<PagedResult<LoanDto>>>> List(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetLoansAsync(q, user.User, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResult<LoanDto>>> Get(Guid id, CancellationToken ct) =>
        OkResult(await service.GetLoanAsync(id, user.User, ct));

    [HttpGet("{id:guid}/schedule")]
    public async Task<ActionResult<ApiResult<IReadOnlyList<ScheduleDto>>>> Schedule(
        Guid id,
        CancellationToken ct
    ) => OkResult(await service.GetScheduleAsync(id, user.User, ct));
}
