namespace Peritial.Domains;

/// <summary>Validated tenant identity. Never inferred from request input.</summary>
public readonly record struct TenantId
{
    public Guid Value { get; }
    private TenantId(Guid value) => Value = value;
    public static TenantId From(Guid value) =>
        value == Guid.Empty ? throw new ArgumentException("Tenant id cannot be empty.", nameof(value)) : new TenantId(value);
}
