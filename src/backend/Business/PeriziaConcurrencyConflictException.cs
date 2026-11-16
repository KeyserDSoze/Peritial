namespace Peritial.Business;

/// <summary>
/// Raised when a tenant-visible perizia no longer matches the database
/// snapshot read by this request. The failed mutation and its success audit
/// both roll back; clients must reload state rather than blindly retry.
/// </summary>
public sealed class PeriziaConcurrencyConflictException : InvalidOperationException
{
    public PeriziaConcurrencyConflictException()
        : base("Perizia was modified by another request. Reload and retry.") { }
}
