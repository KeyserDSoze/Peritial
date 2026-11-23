using Npgsql;
using Peritial.Business;
using Peritial.Domains;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class DocumentCatalogRepositoryTests
{
    [Fact]
    public async Task Read_catalog_metadata_filters_tenant_and_perizia_and_does_not_leak_pool_context()
    {
        var connection = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES")
            ?? throw new InvalidOperationException("PERITIAL_TEST_POSTGRES required.");
        await using var admin = NpgsqlDataSource.Create(connection);
        await SchemaBootstrap.ApplyDevelopmentSchemaAsync(admin);
        await TestDbRoleProvisioner.ConfigureAsync(admin);
        var builder = new NpgsqlConnectionStringBuilder(connection)
        {
            Username = "peritial_app", Password = "local-only-app", MaxPoolSize = 1
        };
        await using var app = NpgsqlDataSource.Create(builder.ConnectionString);
        var writer = new PgDocumentRepository(admin);
        var repo = new PgDocumentCatalogRepository(app);

        var tenantA = TenantId.From(Guid.NewGuid());
        var tenantB = TenantId.From(Guid.NewGuid());
        // A document FK includes (tenant_id, perizia_id). Seed real parent
        // rows including the same perizia UUID in two tenants to exercise RLS.
        var parentA = Perizia.Create(tenantA, "CAT-A-" + Guid.NewGuid().ToString("N"));
        var parentC = Perizia.Create(tenantA, "CAT-C-" + Guid.NewGuid().ToString("N"));
        var parentB = Perizia.Restore(parentA.Id, tenantB,
            "CAT-B-" + Guid.NewGuid().ToString("N"),
            PeriziaStatus.Registered, null, DateTime.UtcNow);
        var perizie = new PgPeriziaRepository(admin);
        await perizie.AddAsync(parentA);
        await perizie.AddAsync(parentC);
        await perizie.AddAsync(parentB);
        var aDoc = Record(tenantA, parentA.Id, DateTime.UtcNow.AddSeconds(-2));
        var latest = Record(tenantA, parentA.Id, DateTime.UtcNow);
        var tied = Record(tenantA, parentA.Id, latest.CreatedAtUtc);
        var other = Record(tenantA, parentC.Id, DateTime.UtcNow.AddSeconds(-1));
        var foreign = Record(tenantB, parentA.Id, DateTime.UtcNow.AddSeconds(1));
        // The catalog is the read-only projection, not the ingestion seam.
        // Seeding with admin bypasses the upload-intent workflow in test only.
        foreach (var item in new[] {aDoc, latest, tied, other, foreign})
            await writer.AddAsync(item);

        var scoped = await repo.ListAsync(tenantA, parentA.Id, 100);
        var expected = new[] {latest, tied}.OrderByDescending(x => x.Id)
            .Select(x => x.Id).Append(aDoc.Id).ToArray();
        Assert.Equal(expected, scoped.Select(x => x.Id));
        Assert.Equal(expected[0], (await repo.ListAsync(tenantA, null, 1)).Single().Id);

        // Real PostgreSQL seek: tie-break on UUID DESC and filter before LIMIT.
        var first = await repo.PageAsync(tenantA, parentA.Id, null, 2);
        Assert.Equal(expected[..2], first.Select(x => x.Id));
        var more = await repo.PageAsync(tenantA, parentA.Id,
            new DocumentPageBoundary(first[^1].CreatedAtUtc, first[^1].Id), 2);
        Assert.Equal(expected[2..], more.Select(x => x.Id));
        var onlyForeign = await repo.PageAsync(tenantB, parentA.Id, null, 4);
        Assert.Equal(foreign.Id, Assert.Single(onlyForeign).Id);
        var empty = await repo.PageAsync(tenantA, Guid.NewGuid(), null, 4);
        Assert.Empty(empty);
        Assert.Equal(foreign.Id, (await repo.ListAsync(tenantB, null, 100)).Single().Id);
        Assert.Null(await repo.FindAsync(tenantA, foreign.Id));
        Assert.Null(await repo.FindAsync(tenantB, latest.Id));
        var metadata = await repo.FindAsync(tenantA, latest.Id);
        Assert.NotNull(metadata);
        Assert.Equal(latest.FileName, metadata.FileName);
        Assert.Equal(latest.Length, metadata.Length);
        Assert.Equal(latest.PeriziaId, metadata.PeriziaId);
        Assert.DoesNotContain(latest.StorageKey,
            System.Text.Json.JsonSerializer.Serialize(metadata));

        // Private signed-tenant download lookup is the ONLY read that may
        // retrieve storage_key; it must enforce the same pooled-connection
        // RLS boundaries as the metadata list and preserve the actual key.
        var locator = await repo.FindForDownloadAsync(tenantA, latest.Id);
        Assert.NotNull(locator);
        Assert.Equal(latest.StorageKey, locator.StorageKey);
        Assert.Equal(latest.Id, locator.Document.Id);
        Assert.Null(await repo.FindForDownloadAsync(tenantB, latest.Id));
        Assert.Null(await repo.FindForDownloadAsync(tenantA, foreign.Id));
        Assert.Null(await repo.FindForDownloadAsync(tenantA, Guid.NewGuid()));
        Assert.Equal(foreign.StorageKey,
            (await repo.FindForDownloadAsync(tenantB, foreign.Id))!.StorageKey);
        Assert.DoesNotContain("StorageKey",
            System.Text.Json.JsonSerializer.Serialize(locator.Document));

        // RLS setting is transaction-local: same pooled connection with no
        // tenant setting may not see any metadata from either previous call.
        await using var conn = await app.OpenConnectionAsync();
        await using var noTenant = new NpgsqlCommand("SELECT count(*) FROM documents WHERE id=@id", conn);
        noTenant.Parameters.AddWithValue("id", latest.Id);
        Assert.Equal(0L, (long)(await noTenant.ExecuteScalarAsync())!);
    }

    private static DocumentRecord Record(TenantId tenant, Guid periziaId, DateTime date)
    {
        var id = Guid.NewGuid();
        return new DocumentRecord(id, tenant, periziaId, "read-test.pdf",
            "application/pdf", 42, tenant.Value.ToString("N") + "/" + id.ToString("N") + ".pdf", date);
    }
}
