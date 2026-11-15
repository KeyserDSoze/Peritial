using Peritial.Domains;

namespace Peritial.Business;

public enum UploadIdempotencyState { Acquired = 1, InProgress = 2, Completed = 3 }

public sealed record UploadIdempotencyReservation(
    UploadIdempotencyState State, DocumentDto? Document = null);

public sealed class UploadIdempotencyConflictException : Exception
{
    public UploadIdempotencyConflictException()
        : base("Idempotency key already used for another request or upload still in progress.") { }
}

/// <summary>
/// Reserves by (tenant, signed subject, client key) and SHA-256 request hash.
/// A completed reservation returns the committed DTO, not a new upload.
/// </summary>
public interface IUploadIdempotencyStore
{
    Task<UploadIdempotencyReservation> ReserveAsync(TenantId tenant, string subject,
        string key, byte[] requestDigest, Guid documentId, CancellationToken ct = default);

    Task ReleaseAsync(TenantId tenant, Guid documentId, CancellationToken ct = default);
}
