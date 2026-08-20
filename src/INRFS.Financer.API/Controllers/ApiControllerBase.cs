using INRFS.Financer.Application;
using Microsoft.AspNetCore.Mvc;

namespace INRFS.Financer.API.Controllers;

[ApiController]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    protected ActionResult<ApiResult<T>> OkResult<T>(T data, string? message = null) =>
        Ok(new ApiResult<T>(true, data, message, null, HttpContext.TraceIdentifier));
}
