using Peritial.Business;
using Peritial.Domains;
using Xunit;

namespace Peritial.Business.Tests;

public sealed class DocumentKeyTests
{
    [Fact]
    public void La_chiave_storage_include_il_tenant_e_non_espone_il_nome_file()
    {
        var tenant = TenantId.From(Guid.NewGuid());
        var documentId = Guid.NewGuid();
        var key = DocumentKeys.For(tenant, documentId, "../../foo.pdf");
        Assert.StartsWith(tenant.Value.ToString("N") + "/", key);
        Assert.DoesNotContain("..", key);
        Assert.DoesNotContain("foo.pdf", key);
    }

    [Fact]
    public void Estensioni_non_consentite_vengono_rifiutate()
    {
        Assert.Throws<ArgumentException>(() => DocumentKeys.For(
            TenantId.From(Guid.NewGuid()), Guid.NewGuid(), "payload.exe"));
    }
}
