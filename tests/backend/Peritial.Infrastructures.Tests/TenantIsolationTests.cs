using Npgsql;
using Peritial.Domains;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

public sealed class TenantIsolationTests
{
    [Fact]
    public async Task Perizie_sono_isolate_per_tenant_in_postgres()
    {
        var connection = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES")
            ?? throw new InvalidOperationException("PERITIAL_TEST_POSTGRES required: run docker compose test profile.");
        await using var db = NpgsqlDataSource.Create(connection);
        await SchemaBootstrap.ApplyDevelopmentSchemaAsync(db);
        var repository = new PgPeriziaRepository(db);
        var tenantA = TenantId.From(Guid.NewGuid());
        var tenantB = TenantId.From(Guid.NewGuid());
        var practice = Perizia.Create(tenantA, "TDD-" + Guid.NewGuid().ToString("N"));
        await repository.AddAsync(practice);
        Assert.NotNull(await repository.FindAsync(tenantA, practice.Id));
        Assert.Null(await repository.FindAsync(tenantB, practice.Id));
        Assert.DoesNotContain(await repository.ListAsync(tenantB), p => p.Id == practice.Id);
    }
}