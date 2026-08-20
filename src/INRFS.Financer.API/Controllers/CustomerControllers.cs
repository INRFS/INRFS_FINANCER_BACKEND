using INRFS.Financer.Application;
using INRFS.Financer.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace INRFS.Financer.API.Controllers;

[Authorize, Route("api/v1/customers")]
public sealed class CustomersController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<PagedResult<CustomerDto>>>> List(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetCustomersAsync(q, user.User, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResult<CustomerDto>>> Get(Guid id, CancellationToken ct) =>
        OkResult(await service.GetCustomerAsync(id, user.User, ct));

    [HttpPost]
    public async Task<ActionResult<ApiResult<CustomerDto>>> Create(
        CreateCustomerRequest r,
        CancellationToken ct
    ) => OkResult(await service.CreateCustomerAsync(r, user.User, ct));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApiResult<CustomerDto>>> Update(
        Guid id,
        UpdateCustomerRequest r,
        CancellationToken ct
    ) => OkResult(await service.UpdateCustomerAsync(id, r, user.User, ct));

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<ApiResult<object>>> Delete(Guid id, CancellationToken ct)
    {
        await service.DeleteCustomerAsync(id, user.User, ct);
        return OkResult<object>(new { }, "Customer deleted.");
    }

    [HttpPost("{id:guid}/notes")]
    public async Task<ActionResult<ApiResult<object>>> Note(
        Guid id,
        AddNoteRequest r,
        CancellationToken ct
    )
    {
        await service.AddCustomerNoteAsync(id, r, user.User, ct);
        return OkResult<object>(new { }, "Note added.");
    }

    [HttpGet("{id:guid}/ledger")]
    public async Task<ActionResult<ApiResult<object>>> Ledger(
        Guid id,
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetCustomerLedgerAsync(id, q, user.User, ct));
}

[Authorize, Route("api/v1/kyc")]
public sealed class KycController(IPlatformService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<object>>> List(
        [FromQuery] PageQuery q,
        CancellationToken ct
    ) => OkResult(await service.GetKycAsync(q, user.User, ct));

    [HttpPost]
    public async Task<ActionResult<ApiResult<KycRecord>>> Submit(
        KycSubmissionRequest r,
        CancellationToken ct
    ) => OkResult(await service.SubmitKycAsync(r, user.User, ct));

    [Authorize(Roles = "SuperAdmin,Admin,ComplianceOfficer"), HttpPost("{id:guid}/decision")]
    public async Task<ActionResult<ApiResult<KycRecord>>> Decide(
        Guid id,
        KycDecisionRequest r,
        CancellationToken ct
    ) => OkResult(await service.DecideKycAsync(id, r, user.User, ct));
}

[Authorize, Route("api/v1/documents")]
public sealed class DocumentsController(IDocumentService service, ICurrentUserAccessor user)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<IReadOnlyList<StoredDocument>>>> List(
        [FromQuery] Guid? customerId,
        [FromQuery] Guid? financerId,
        CancellationToken ct
    ) => OkResult(customerId.HasValue
        ? await service.ListForCustomerAsync(customerId.Value, user.User, ct)
        : financerId.HasValue
            ? await service.ListForFinancerAsync(financerId.Value, user.User, ct)
            : throw new DomainException("Customer or financer is required."));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResult<StoredDocument>>> Get(Guid id, CancellationToken ct) =>
        OkResult(await service.GetAsync(id, user.User, ct));

    [RequestSizeLimit(10_485_760), HttpPost]
    public async Task<ActionResult<ApiResult<StoredDocument>>> Upload(
        IFormFile file,
        [FromForm] string category,
        [FromForm] Guid? financerId,
        [FromForm] Guid? customerId,
        [FromForm] Guid? applicationId,
        CancellationToken ct
    )
    {
        await using var stream = file.OpenReadStream();
        return OkResult(
            await service.UploadAsync(
                stream,
                file.FileName,
                file.ContentType,
                file.Length,
                category,
                financerId,
                customerId,
                applicationId,
                user.User,
                ct
            )
        );
    }

    [HttpGet("{id:guid}/content")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var result = await service.DownloadAsync(id, user.User, ct);
        return File(result.Content, result.Metadata.ContentType, result.Metadata.OriginalFileName);
    }

    [Authorize(Roles = "SuperAdmin,Admin,ComplianceOfficer"), HttpPost("{id:guid}/verify")]
    public async Task<ActionResult<ApiResult<StoredDocument>>> Verify(
        Guid id,
        DocumentDecisionRequest r,
        CancellationToken ct
    ) => OkResult(await service.VerifyAsync(id, r, user.User, ct));

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<ApiResult<object>>> Delete(Guid id, CancellationToken ct)
    {
        await service.DeleteAsync(id, user.User, ct);
        return OkResult<object>(new { });
    }
}
