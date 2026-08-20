using INRFS.Financer.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace INRFS.Financer.API.Controllers;

[Authorize, Route("api/v1/profile")]
public sealed class ProfileController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<object>>> Get(CancellationToken ct) =>
        OkResult(await service.GetMyProfileAsync(user.User, ct));

    [HttpPut]
    public async Task<ActionResult<ApiResult<object>>> Update(
        UpdateMyProfileRequest request,
        CancellationToken ct
    ) => OkResult(await service.UpdateMyProfileAsync(request, user.User, ct));
}
