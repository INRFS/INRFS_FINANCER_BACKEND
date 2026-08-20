using System.Security.Cryptography;
using INRFS.Financer.Application;
using INRFS.Financer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace INRFS.Financer.Infrastructure;

public sealed class DocumentService(FinancerDbContext db, IOptions<StorageOptions> options)
    : IDocumentService
{
    private readonly StorageOptions _options = options.Value;

    public async Task<IReadOnlyList<StoredDocument>> ListForFinancerAsync(Guid financerId, CurrentUser actor, CancellationToken ct)
    {
        if (!await db.Financers.AnyAsync(x => x.Id == financerId, ct))
            throw new DomainException("Financer not found.", 404);
        if (actor.FinancerId.HasValue && actor.FinancerId != financerId)
            throw new DomainException("Financer is outside your organization.", 403);
        return await db.Documents.AsNoTracking()
            .Where(x => x.FinancerId == financerId && x.CustomerId == null && x.LoanApplicationId == null && !x.IsDeleted)
            .OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<StoredDocument>> ListForCustomerAsync(
        Guid customerId,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == customerId, ct)
            ?? throw new DomainException("Customer not found.", 404);
        if (actor.FinancerId.HasValue && customer.FinancerId != actor.FinancerId)
            throw new DomainException("Customer is outside your organization.", 403);
        return await db.Documents.AsNoTracking()
            .Where(x => x.CustomerId == customerId && !x.IsDeleted)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<StoredDocument> UploadAsync(
        Stream content,
        string fileName,
        string contentType,
        long size,
        string category,
        Guid? financerId,
        Guid? customerId,
        Guid? applicationId,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        if (size <= 0 || size > _options.MaximumFileSizeBytes)
            throw new DomainException(
                $"File size must be between 1 and {_options.MaximumFileSizeBytes} bytes."
            );
        if (!_options.AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            throw new DomainException("File type is not allowed.");
        Guid? tenant = financerId ?? actor.FinancerId;
        if (financerId.HasValue)
        {
            if (!await db.Financers.AnyAsync(x => x.Id == financerId.Value, ct))
                throw new DomainException("Financer not found.", 404);
            if (actor.FinancerId.HasValue && actor.FinancerId != financerId)
                throw new DomainException("Financer is outside your organization.", 403);
        }
        if (customerId.HasValue)
        {
            var c =
                await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == customerId, ct)
                ?? throw new DomainException("Customer not found.", 404);
            if (actor.FinancerId.HasValue && c.FinancerId != actor.FinancerId)
                throw new DomainException("Customer is outside your organization.", 403);
            tenant = c.FinancerId;
        }
        if (applicationId.HasValue)
        {
            var a =
                await db
                    .LoanApplications.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == applicationId, ct)
                ?? throw new DomainException("Application not found.", 404);
            if (actor.FinancerId.HasValue && a.FinancerId != actor.FinancerId)
                throw new DomainException("Application is outside your organization.", 403);
            tenant = a.FinancerId;
        }
        var safeName = Path.GetFileName(fileName);
        var extension = Path.GetExtension(safeName);
        var storageKey = Path.Combine(
                DateTime.UtcNow.ToString("yyyy/MM"),
                $"{Guid.NewGuid():N}{extension}"
            )
            .Replace('\\', '/');
        var full = Path.GetFullPath(Path.Combine(_options.RootPath, storageKey));
        var root = Path.GetFullPath(_options.RootPath);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Invalid storage path.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using (var output = File.Create(full))
        {
            await content.CopyToAsync(output, ct);
        }
        await using var read = File.OpenRead(full);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(read, ct));
        var doc = new StoredDocument
        {
            FinancerId = tenant,
            CustomerId = customerId,
            LoanApplicationId = applicationId,
            Category = category.Trim(),
            OriginalFileName = safeName,
            ContentType = contentType,
            Size = size,
            Sha256 = hash,
            StorageKey = storageKey,
            Status = DocumentStatus.Pending,
            CreatedBy = actor.UserId,
        };
        db.Documents.Add(doc);
        db.AuditLogs.Add(
            new AuditLog
            {
                ActorId = actor.UserId,
                FinancerId = tenant,
                Action = "Document.Uploaded",
                EntityType = nameof(StoredDocument),
                EntityId = doc.Id.ToString(),
                AfterJson = System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        doc.Category,
                        doc.OriginalFileName,
                        doc.Size,
                        doc.Sha256,
                    }
                ),
            }
        );
        await db.SaveChangesAsync(ct);
        return doc;
    }

    public async Task<(StoredDocument Metadata, Stream Content)> DownloadAsync(
        Guid id,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        var doc =
            await db.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new DomainException("Document not found.", 404);
        if (actor.FinancerId.HasValue && doc.FinancerId != actor.FinancerId)
            throw new DomainException("Document is outside your organization.", 403);
        var full = Path.GetFullPath(Path.Combine(_options.RootPath, doc.StorageKey));
        var root = Path.GetFullPath(_options.RootPath);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new DomainException("Stored content is unavailable.", 404);
        return (doc, File.OpenRead(full));
    }

    public async Task<StoredDocument> VerifyAsync(
        Guid id,
        DocumentDecisionRequest request,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        if (!actor.Roles.Contains("SuperAdmin") && !actor.Permissions.Contains("documents.verify"))
            throw new DomainException("Permission denied.", 403);
        if (request.Status is not (DocumentStatus.Verified or DocumentStatus.Rejected))
            throw new DomainException("Invalid document decision.");
        var doc =
            await db.Documents.FindAsync([id], ct)
            ?? throw new DomainException("Document not found.", 404);
        doc.Status = request.Status;
        doc.VerificationNotes = request.Notes;
        doc.VerifiedBy = actor.UserId;
        doc.VerifiedAt = DateTimeOffset.UtcNow;
        db.AuditLogs.Add(
            new AuditLog
            {
                ActorId = actor.UserId,
                FinancerId = doc.FinancerId,
                Action = "Document.Verified",
                EntityType = nameof(StoredDocument),
                EntityId = id.ToString(),
                AfterJson = System.Text.Json.JsonSerializer.Serialize(
                    new { doc.Status, doc.VerificationNotes }
                ),
            }
        );
        await db.SaveChangesAsync(ct);
        return doc;
    }

    public async Task<StoredDocument> GetAsync(Guid id, CurrentUser actor, CancellationToken ct)
    {
        var doc =
            await db.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new DomainException("Document not found.", 404);
        if (actor.FinancerId.HasValue && doc.FinancerId != actor.FinancerId)
            throw new DomainException("Document is outside your organization.", 403);
        return doc;
    }

    public async Task DeleteAsync(Guid id, CurrentUser actor, CancellationToken ct)
    {
        var doc =
            await db.Documents.FindAsync([id], ct)
            ?? throw new DomainException("Document not found.", 404);
        if (actor.FinancerId.HasValue && doc.FinancerId != actor.FinancerId)
            throw new DomainException("Document is outside your organization.", 403);
        if (doc.Status == DocumentStatus.Verified)
            throw new DomainException(
                "Verified documents cannot be deleted; use a superseding document.",
                409
            );
        doc.IsDeleted = true;
        doc.DeletedAt = DateTimeOffset.UtcNow;
        doc.DeletedBy = actor.UserId;
        db.AuditLogs.Add(
            new AuditLog
            {
                ActorId = actor.UserId,
                FinancerId = doc.FinancerId,
                Action = "Document.Deleted",
                EntityType = nameof(StoredDocument),
                EntityId = id.ToString(),
            }
        );
        await db.SaveChangesAsync(ct);
    }
}
