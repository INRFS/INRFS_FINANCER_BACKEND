using INRFS.Financer.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace INRFS.Financer.API.Controllers;

[Authorize, Route("api/v1/financers")]
public sealed class FinancersController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<PagedResult<FinancerDto>>>> List(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetFinancersAsync(q, user.User, ct));

    [HttpGet("billing-usage")]
    public async Task<ActionResult<ApiResult<IReadOnlyList<FinancerBillingUsageDto>>>> BillingUsage(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken ct
    ) => OkResult(await service.GetFinancerBillingUsageAsync(user.User, ct, from, to));

    [Authorize(Roles = "SuperAdmin,Admin"), HttpPost]
    public async Task<ActionResult<ApiResult<FinancerDto>>> Create(
        CreateFinancerRequest r,
        CancellationToken ct
    ) => OkResult(await service.CreateFinancerAsync(r, user.User, ct));

    [Authorize(Roles = "SuperAdmin,Admin"), HttpPost("{id:guid}/status")]
    public async Task<ActionResult<ApiResult<FinancerDto>>> Status(
        Guid id,
        ChangeStatusRequest r,
        CancellationToken ct
    ) => OkResult(await service.ChangeFinancerStatusAsync(id, r, user.User, ct));

    [Authorize(Roles = "SuperAdmin,Admin,ComplianceOfficer"), HttpPost("{id:guid}/kyc")]
    public async Task<ActionResult<ApiResult<FinancerDto>>> Kyc(Guid id, KycDecisionRequest r, CancellationToken ct) =>
        OkResult(await service.DecideFinancerKycAsync(id, r, user.User, ct));

    [HttpGet("{id:guid}/usage")]
    public async Task<ActionResult<ApiResult<object>>> Usage(Guid id, CancellationToken ct) =>
        OkResult(
            await service.GetDashboardAsync(true, new PageQuery(FinancerId: id), user.User, ct)
        );
}

[Authorize, Route("api/v1/users")]
public sealed class UsersController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<PagedResult<UserDto>>>> List(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetUsersAsync(q, user.User, ct));

    [HttpPost]
    public async Task<ActionResult<ApiResult<UserDto>>> Create(
        CreateUserRequest r,
        CancellationToken ct
    ) => OkResult(await service.CreateUserAsync(r, user.User, ct));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApiResult<UserDto>>> Update(
        Guid id,
        UpdateUserRequest r,
        CancellationToken ct
    ) => OkResult(await service.UpdateUserAsync(id, r, user.User, ct));

    [HttpPut("{id:guid}/roles")]
    public async Task<ActionResult<ApiResult<UserDto>>> Roles(
        Guid id,
        [FromBody] IReadOnlyList<Guid> roleIds,
        CancellationToken ct
    ) => OkResult(await service.SetUserRolesAsync(id, roleIds, user.User, ct));

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<ApiResult<object>>> Delete(Guid id, CancellationToken ct)
    {
        await service.DeleteUserAsync(id, user.User, ct);
        return OkResult<object>(new { });
    }

    [HttpGet("{id:guid}/sessions")]
    public async Task<ActionResult<ApiResult<IReadOnlyList<AdminSessionDto>>>> Sessions(Guid id, CancellationToken ct) =>
        OkResult(await service.GetUserSessionsAsync(id, user.User, ct));

    [HttpDelete("{id:guid}/sessions/{sessionId:guid}")]
    public async Task<ActionResult<ApiResult<object>>> RevokeSession(Guid id, Guid sessionId, CancellationToken ct)
    {
        await service.RevokeUserSessionAsync(id, sessionId, user.User, ct);
        return OkResult<object>(new { });
    }
}

[Authorize(Roles = "SuperAdmin,Admin"), Route("api/v1/admins")]
public sealed class AdminsController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<PagedResult<UserDto>>>> List(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetUsersAsync(q with { FinancerId = null }, user.User, ct));
}

[Authorize, Route("api/v1/employees")]
public sealed class EmployeesController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<PagedResult<UserDto>>>> List(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetUsersAsync(q, user.User, ct));
}

[Authorize, Route("api/v1/roles")]
public sealed class RolesController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<IReadOnlyList<RoleDto>>>> List(CancellationToken ct) =>
        OkResult(await service.GetRolesAsync(ct));

    [Authorize(Roles = "SuperAdmin"), HttpPost]
    public async Task<ActionResult<ApiResult<RoleDto>>> Create(
        CreateRoleRequest r,
        CancellationToken ct
    ) => OkResult(await service.CreateRoleAsync(r, user.User, ct));
}
