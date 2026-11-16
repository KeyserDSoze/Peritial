namespace Peritial.Domains;

public sealed class Perizia
{
    public Guid Id { get; }
    public TenantId TenantId { get; }
    public string Reference { get; }
    public PeriziaStatus Status { get; private set; }
    public string? AssignedTo { get; private set; }
    public DateTime CreatedAtUtc { get; }
    // Optimistic concurrency snapshot, captured at restore / last confirmed
    // database commit. Domain transitions do not change this snapshot.
    public PeriziaStatus PersistedStatus { get; private set; }
    public string? PersistedAssignedTo { get; private set; }

    private Perizia(Guid id, TenantId tenantId, string reference, PeriziaStatus status, string? assignedTo, DateTime createdAtUtc)
    {
        if (tenantId.Value == Guid.Empty) throw new ArgumentException("Invalid tenant.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(reference)) throw new ArgumentException("Reference is required.", nameof(reference));
        if (reference.Length > 160) throw new ArgumentException("Reference too long.", nameof(reference));
        Id = id;
        TenantId = tenantId;
        Reference = reference.Trim();
        Status = status;
        AssignedTo = assignedTo;
        CreatedAtUtc = createdAtUtc;
        PersistedStatus = status;
        PersistedAssignedTo = assignedTo;
    }

    public static Perizia Create(TenantId tenantId, string reference) =>
        new(Guid.NewGuid(), tenantId, reference, PeriziaStatus.Registered, null, DateTime.UtcNow);

    /// <summary>Used exclusively by the persistence adapter to reconstruct an aggregate.</summary>
    public static Perizia Restore(Guid id, TenantId tenantId, string reference, PeriziaStatus status, string? assignedTo, DateTime createdAtUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("Invalid id.", nameof(id));
        return new Perizia(id, tenantId, reference, status, assignedTo, createdAtUtc);
    }

    /// <summary>
    /// Used by persistence ONLY after a successful database commit.
    /// Failed or rolled-back writes must not advance the expected state.
    /// </summary>
    public void ConfirmPersisted()
    {
        PersistedStatus = Status;
        PersistedAssignedTo = AssignedTo;
    }

    public void Assign(TenantId actorTenant, string assignee)
    {
        CheckTenant(actorTenant);
        if (Status != PeriziaStatus.Registered) throw new InvalidOperationException("Only registered perizie may be assigned.");
        if (string.IsNullOrWhiteSpace(assignee)) throw new ArgumentException("Assignee required.", nameof(assignee));
        AssignedTo = assignee.Trim();
        Status = PeriziaStatus.Assigned;
    }

    public void Advance(TenantId actorTenant, PeriziaStatus target)
    {
        CheckTenant(actorTenant);
        var next = Status switch
        {
            PeriziaStatus.Assigned => PeriziaStatus.ToBeRevised,
            PeriziaStatus.ToBeRevised => PeriziaStatus.Revised,
            PeriziaStatus.Revised => PeriziaStatus.ToSend,
            PeriziaStatus.ToSend => PeriziaStatus.Sent,
            PeriziaStatus.Sent => PeriziaStatus.Billed,
            _ => throw new InvalidOperationException("This state cannot be advanced.")
        };
        if (next != target) throw new InvalidOperationException("Invalid state transition.");
        Status = target;
    }

    public void Cancel(TenantId actorTenant)
    {
        CheckTenant(actorTenant);
        if (Status is PeriziaStatus.Billed or PeriziaStatus.Cancelled or PeriziaStatus.Duplicated)
            throw new InvalidOperationException("Terminal perizie cannot be cancelled.");
        Status = PeriziaStatus.Cancelled;
    }

    private void CheckTenant(TenantId actorTenant)
    {
        if (actorTenant != TenantId) throw new UnauthorizedAccessException("Cross-tenant operation denied.");
    }
}
