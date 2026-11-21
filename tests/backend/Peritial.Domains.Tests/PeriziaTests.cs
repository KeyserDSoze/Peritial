using Peritial.Domains;
using Xunit;

namespace Peritial.Domains.Tests;

public sealed class PeriziaTests
{
    private static TenantId Tenant() => TenantId.From(Guid.NewGuid());

    [Fact]
    public void Nuova_perizia_si_avvia_in_stato_registered()
    {
        var perizia = Perizia.Create(Tenant(), "PER-2026-001");
        Assert.Equal(PeriziaStatus.Registered, perizia.Status);
        Assert.NotEqual(Guid.Empty, perizia.Id);
    }

    [Fact]
    public void Nuova_perizia_ha_timestamp_utc_esattamente_persistibile_in_PostgreSQL()
    {
        var created = Perizia.Create(Tenant(), "MICROSECOND-PERIZIA");
        Assert.Equal(DateTimeKind.Utc, created.CreatedAtUtc.Kind);
        Assert.Equal(0, created.CreatedAtUtc.Ticks % 10);
        var restored = Perizia.Restore(created.Id, created.TenantId, created.Reference,
            created.Status, created.AssignedTo, created.CreatedAtUtc);
        Assert.Equal(created.CreatedAtUtc, restored.CreatedAtUtc);
    }

    [Fact]
    public void Restore_non_riscrive_la_precisione_di_un_timestamp_storico()
    {
        var tenant = Tenant();
        var historical = new DateTime(2026, 11, 21, 23, 59, 59, DateTimeKind.Utc)
            .AddTicks(9);
        var perizia = Perizia.Restore(Guid.NewGuid(), tenant, "LEGACY-PERIZIA",
            PeriziaStatus.Registered, null, historical);
        Assert.Equal(historical.Ticks, perizia.CreatedAtUtc.Ticks);
    }

    [Fact]
    public void Assegnazione_richiede_utente_e_tenant_proprietario()
    {
        var tenant = Tenant();
        var perizia = Perizia.Create(tenant, "PER-2026-002");

        Assert.Throws<UnauthorizedAccessException>(() => perizia.Assign(Tenant(), "user1"));
        Assert.Throws<ArgumentException>(() => perizia.Assign(tenant, " "));
        perizia.Assign(tenant, "user1");

        Assert.Equal(PeriziaStatus.Assigned, perizia.Status);
        Assert.Equal("user1", perizia.AssignedTo);
    }

    [Fact]
    public void Le_transizioni_non_possono_saltare_stati()
    {
        var tenant = Tenant();
        var perizia = Perizia.Create(tenant, "PER-2026-003");
        Assert.Throws<InvalidOperationException>(() => perizia.Advance(tenant, PeriziaStatus.Billed));
        perizia.Assign(tenant, "user1");
        perizia.Advance(tenant, PeriziaStatus.ToBeRevised);
        Assert.Equal(PeriziaStatus.ToBeRevised, perizia.Status);
    }

    [Fact]
    public void Annullamento_richiede_tenant_e_rifiuta_stati_terminali()
    {
        var tenant = Tenant();
        var perizia = Perizia.Create(tenant, "PER-CANCEL");
        Assert.Throws<UnauthorizedAccessException>(() => perizia.Cancel(Tenant()));
        perizia.Cancel(tenant);
        Assert.Equal(PeriziaStatus.Cancelled, perizia.Status);
        Assert.Throws<InvalidOperationException>(() => perizia.Cancel(tenant));
        Assert.Throws<InvalidOperationException>(() => perizia.Advance(tenant, PeriziaStatus.Billed));

        var billed = Perizia.Restore(Guid.NewGuid(), tenant, "PER-BILLED",
            PeriziaStatus.Billed, "operator", DateTime.UtcNow);
        var duplicated = Perizia.Restore(Guid.NewGuid(), tenant, "PER-DUPLICATE",
            PeriziaStatus.Duplicated, null, DateTime.UtcNow);
        Assert.Throws<InvalidOperationException>(() => billed.Cancel(tenant));
        Assert.Throws<InvalidOperationException>(() => duplicated.Cancel(tenant));
    }

    [Fact]
    public void Tenant_vuoto_e_riferimento_vuoto_non_sono_ammessi()
    {
        Assert.Throws<ArgumentException>(() => TenantId.From(Guid.Empty));
        Assert.Throws<ArgumentException>(() => Perizia.Create(Tenant(), " "));
    }
}
