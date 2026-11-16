using Peritial.Domains;
using Xunit;

namespace Peritial.Domains.Tests;

public sealed class SinistroTests
{
    private static TenantId Tenant() => TenantId.From(Guid.NewGuid());
    private static readonly DateOnly Occurred = new(2026, 10, 9);

    [Fact]
    public void Creazione_preserva_riferimento_esterno_date_e_tenant()
    {
        var tenant = Tenant();
        var reported = new DateOnly(2026, 10, 12);

        var sinistro = Sinistro.Create(tenant, "  SIN-2026-001  ", Occurred, reported);

        Assert.NotEqual(Guid.Empty, sinistro.Id);
        Assert.Equal(tenant, sinistro.TenantId);
        Assert.Equal("SIN-2026-001", sinistro.ExternalReference);
        Assert.Equal(Occurred, sinistro.OccurredOn);
        Assert.Equal(reported, sinistro.ReportedOn);
        Assert.Equal(DateTimeKind.Utc, sinistro.CreatedAtUtc.Kind);
    }

    [Fact]
    public void Data_denuncia_e_opzionale_senza_inventare_regole_temporali_legacy()
    {
        var sinistro = Sinistro.Create(Tenant(), "SIN-2026-002", Occurred);
        Assert.Null(sinistro.ReportedOn);

        // Il legacy non chiarisce una regola di ordinamento: la caratterizzazione
        // è rinviata prima di applicare una validazione che rifiuti dati storici.
        sinistro.Update(sinistro.TenantId, "SIN-2026-002", Occurred, new DateOnly(2026, 10, 8));
        Assert.Equal(new DateOnly(2026, 10, 8), sinistro.ReportedOn);
    }

    [Fact]
    public void Rifiuta_riferimenti_vuoti_lunghi_o_date_default()
    {
        var tenant = Tenant();
        Assert.Throws<ArgumentException>(() => Sinistro.Create(tenant, " ", Occurred));
        Assert.Throws<ArgumentException>(() => Sinistro.Create(tenant, new string('X', 101), Occurred));
        Assert.Throws<ArgumentException>(() => Sinistro.Create(tenant, "SIN", default));
        Assert.Throws<ArgumentException>(() => Sinistro.Create(tenant, "SIN", Occurred, default(DateOnly)));
        Assert.Throws<ArgumentException>(() => Sinistro.Create(default, "SIN", Occurred));
    }

    [Fact]
    public void Aggiornamento_richiede_tenant_e_non_applica_input_invalidi()
    {
        var tenant = Tenant();
        var sinistro = Sinistro.Create(tenant, "ORIGINALE", Occurred);

        Assert.Throws<UnauthorizedAccessException>(() =>
            sinistro.Update(Tenant(), "ALTRO", new DateOnly(2026, 11, 1)));
        Assert.Throws<ArgumentException>(() =>
            sinistro.Update(tenant, "MODIFICATO", Occurred, default(DateOnly)));
        Assert.Equal("ORIGINALE", sinistro.ExternalReference);
        Assert.Null(sinistro.ReportedOn);

        sinistro.Update(tenant, "  MODIFICATO  ", new DateOnly(2026, 11, 1));
        Assert.Equal("MODIFICATO", sinistro.ExternalReference);
        Assert.Equal(new DateOnly(2026, 11, 1), sinistro.OccurredOn);
    }

    [Fact]
    public void Restore_di_persistenza_preserva_identita_e_valida_l_id()
    {
        var id = Guid.NewGuid();
        var tenant = Tenant();
        var created = new DateTime(2026, 10, 10, 13, 0, 0, DateTimeKind.Utc);
        var sinistro = Sinistro.Restore(id, tenant, "SIN-ARCHIVE", Occurred, null, created);

        Assert.Equal(id, sinistro.Id);
        Assert.Equal(tenant, sinistro.TenantId);
        Assert.Equal(created, sinistro.CreatedAtUtc);
        Assert.Throws<ArgumentException>(() =>
            Sinistro.Restore(Guid.Empty, tenant, "SIN-ARCHIVE", Occurred, null, created));
    }
}
