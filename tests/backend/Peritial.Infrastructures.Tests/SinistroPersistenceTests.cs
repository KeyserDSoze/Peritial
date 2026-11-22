using Npgsql;
using Peritial.Business;
using Peritial.Domains;
using Peritial.Infrastructures;
using Xunit;

namespace Peritial.Infrastructures.Tests;

/// <summary>
/// Isolated real PostgreSQL 17 schema, real least-privileged app role and
/// actual transaction-local tenant context. No mock RLS or privileged reads.
/// </summary>
public sealed class SinistroPersistenceTests
{
    [Fact]
    public async Task New_sinistro_createdAt_round_trips_postgres_without_tick_loss_or_audit_drift()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var repo = new PgSinistroRepository(db.App);
        var occurred = new DateOnly(2026, 11, 19);
        var reported = new DateOnly(2026, 11, 20);
        var item = Sinistro.Create(tenant,
            "PG-MICROS-" + Guid.NewGuid().ToString("N"), occurred, reported);
        Assert.Equal(0, item.CreatedAtUtc.Ticks % 10);

        await repo.AddAuditedAsync(item, Event(tenant, item.Id, "sinistro.create"));
        var restored = await repo.FindAsync(tenant, item.Id);
        Assert.NotNull(restored);
        Assert.Equal(item.CreatedAtUtc.Ticks, restored.CreatedAtUtc.Ticks);
        Assert.Equal(DateTimeKind.Utc, restored.CreatedAtUtc.Kind);
        Assert.Equal(occurred, restored.OccurredOn);
        Assert.Equal(reported, restored.ReportedOn);
        Assert.Null(await repo.FindAsync(TenantId.From(Guid.NewGuid()), item.Id));
        Assert.Equal(1L, restored.PersistedVersion);

        await using var auditCount = db.Admin.CreateCommand(
            "SELECT count(*) FROM security_audit WHERE tenant_id=@tenant " +
            "AND resource_id=@id AND action='sinistro.create' AND outcome='succeeded'");
        auditCount.Parameters.AddWithValue("tenant", tenant.Value);
        auditCount.Parameters.AddWithValue("id", item.Id);
        Assert.Equal(1L, (long)(await auditCount.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task V003_creates_forced_tenant_rls_with_least_privilege_and_idempotent_journal()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        Assert.Equal(0, await SchemaMigrator.ApplyAsync(db.Admin));

        await using var conn = await db.Admin.OpenConnectionAsync();
        await using (var policy = new NpgsqlCommand(
            "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class " +
            "WHERE oid = to_regclass('sinistri')", conn))
            Assert.True((bool)(await policy.ExecuteScalarAsync())!);
        await using (var role = new NpgsqlCommand(
            "SELECT has_table_privilege('peritial_app','sinistri','SELECT') " +
            "AND has_table_privilege('peritial_app','sinistri','INSERT') " +
            "AND has_table_privilege('peritial_app','sinistri','UPDATE') " +
            "AND has_table_privilege('peritial_app','sinistri','DELETE') " +
            "AND NOT has_table_privilege('peritial_recovery','sinistri','SELECT')", conn))
            Assert.True((bool)(await role.ExecuteScalarAsync())!);
        await using (var versions = new NpgsqlCommand(
            "SELECT array_agg(version ORDER BY version) FROM schema_migrations", conn))
            Assert.Equal(new[] {1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12}, (int[])(await versions.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Repository_read_write_isolation_is_enforced_by_RLS_and_pool_context_does_not_leak()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var repo = new PgSinistroRepository(db.App);
        var a = TenantId.From(Guid.NewGuid());
        var b = TenantId.From(Guid.NewGuid());
        var first = Sinistro.Create(a, "SIN-SHARED", new DateOnly(2026, 11, 2));
        var second = Sinistro.Create(b, "SIN-SHARED", new DateOnly(2026, 11, 3));

        await repo.AddAuditedAsync(first, Event(a, first.Id, "sinistro.create"));
        await repo.AddAuditedAsync(second, Event(b, second.Id, "sinistro.create"));
        Assert.Equal(first.Id, (await repo.ListAsync(a)).Single().Id);
        Assert.Equal(second.Id, (await repo.ListAsync(b)).Single().Id);
        Assert.Null(await repo.FindAsync(a, second.Id));
        Assert.Null(await repo.FindAsync(b, first.Id));
        Assert.Equal(1L, first.PersistedVersion);
        Assert.Equal(1L, (await repo.FindAsync(a, first.Id))!.PersistedVersion);

        // The same pooled app connection has no cross-request tenant context.
        await using var conn = await db.App.OpenConnectionAsync();
        await using (var noTenant = new NpgsqlCommand(
            "SELECT count(*) FROM sinistri WHERE id IN (@a,@b)", conn))
        {
            noTenant.Parameters.AddWithValue("a", first.Id);
            noTenant.Parameters.AddWithValue("b", second.Id);
            Assert.Equal(0L, (long)(await noTenant.ExecuteScalarAsync())!);
        }
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand(
            "SELECT set_config('peritial.tenant_id', @tenant, true)", conn, tx))
        {
            set.Parameters.AddWithValue("tenant", a.Value.ToString("D"));
            await set.ExecuteNonQueryAsync();
        }
        await using (var crossTenantWrite = new NpgsqlCommand(
            "INSERT INTO sinistri (tenant_id,id,external_reference,occurred_on,created_at_utc) " +
            "VALUES (@tenant,@id,'RLS-DENIED',DATE '2026-11-01',now())", conn, tx))
        {
            crossTenantWrite.Parameters.AddWithValue("tenant", b.Value);
            crossTenantWrite.Parameters.AddWithValue("id", Guid.NewGuid());
            var ex = await Assert.ThrowsAsync<PostgresException>(
                () => crossTenantWrite.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Import_reconciliation_reference_lookup_is_exact_bounded_and_forced_tenant_scoped()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var writer = new PgSinistroRepository(db.App);
        var lookup = new PgLegacyImportReferenceLookup(db.App);
        var tenantA = TenantId.From(Guid.NewGuid());
        var tenantB = TenantId.From(Guid.NewGuid());
        var sameA = Sinistro.Create(tenantA, "XML-REF", new DateOnly(2026, 11, 17));
        var sameB = Sinistro.Create(tenantA, "XML-REF", new DateOnly(2026, 11, 18));
        var foreign = Sinistro.Create(tenantB, "XML-REF", new DateOnly(2026, 11, 19));
        var similar = Sinistro.Create(tenantA, "XML-REF-SUFFIX", new DateOnly(2026, 11, 20));
        foreach (var s in new[] {sameA, sameB, foreign, similar})
            await writer.AddAuditedAsync(s, Event(s.TenantId, s.Id, "sinistro.create"));

        var matches = await lookup.FindExactAsync(tenantA, "XML-REF", 2);
        Assert.Equal(2, matches.Count);
        Assert.True(new HashSet<Guid> { sameA.Id, sameB.Id }
            .SetEquals(matches.Select(x => x.Id)));
        Assert.Equal(foreign.Id, Assert.Single(
            await lookup.FindExactAsync(tenantB, "XML-REF", 2)).Id);
        Assert.Empty(await lookup.FindExactAsync(tenantA, "xml-ref", 2));
        Assert.Empty(await lookup.FindExactAsync(tenantA, "XML-REF%", 2));
        Assert.Empty(await lookup.FindExactAsync(tenantA, "UNKNOWN", 2));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            lookup.FindExactAsync(tenantA, "XML-REF", 3));

        // The pooled application connection must never retain the last
        // tenant's session-local RLS setting after this read-only query.
        await using var conn = await db.App.OpenConnectionAsync();
        await using var noTenant = new NpgsqlCommand(
            "SELECT count(*) FROM sinistri WHERE external_reference='XML-REF'", conn);
        Assert.Equal(0L, (long)(await noTenant.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task V011_case_only_import_replays_key_without_duplicate_case_or_audit()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var other = TenantId.From(Guid.NewGuid());
        var reference = "XML-COMMIT-" + Guid.NewGuid().ToString("N");
        var parsed = new LegacyXmlPreviewDto("classic", reference,
            new DateOnly(2026, 11, 19), new DateOnly(2026, 11, 20), 0);
        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("safe-xml"));
        var changed = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("different-safe-xml"));
        var repo = new PgLegacyXmlCaseImportStore(db.App);
        const string key = "import-test-key-00000001";
        var created = await repo.CommitAsync(tenant, "subject-A", key, digest, parsed);
        Assert.False(created.Replayed);
        Assert.NotEqual(Guid.Empty, created.SinistroId);
        var replay = await repo.CommitAsync(tenant, "subject-A", key, digest, parsed);
        Assert.True(replay.Replayed);
        Assert.Equal(created.SinistroId, replay.SinistroId);

        await Assert.ThrowsAsync<LegacyXmlIdempotencyConflictException>(() =>
            repo.CommitAsync(tenant, "subject-A", key, changed, parsed));
        await Assert.ThrowsAsync<LegacyXmlReferenceConflictException>(() =>
            repo.CommitAsync(tenant, "subject-A", "import-test-key-00000002", digest, parsed));
        await Assert.ThrowsAsync<LegacyXmlReferenceConflictException>(() =>
            repo.CommitAsync(tenant, "subject-B", key, digest, parsed));
        Assert.NotNull(await new PgSinistroRepository(db.App).FindAsync(tenant, created.SinistroId));
        Assert.Null(await new PgSinistroRepository(db.App).FindAsync(other, created.SinistroId));

        var otherTenant = await repo.CommitAsync(other, "subject-A", key, digest, parsed);
        Assert.False(otherTenant.Replayed);
        Assert.NotEqual(created.SinistroId, otherTenant.SinistroId);

        await using var count = db.Admin.CreateCommand(
            "SELECT (SELECT count(*) FROM legacy_xml_import_keys WHERE tenant_id=@tenant), " +
            "(SELECT count(*) FROM sinistri WHERE tenant_id=@tenant " +
            "AND external_reference=@reference), " +
            "(SELECT count(*) FROM security_audit WHERE tenant_id=@tenant " +
            "AND action='sinistro.import-xml' AND outcome='succeeded')");
        count.Parameters.AddWithValue("tenant", tenant.Value);
        count.Parameters.AddWithValue("reference", reference);
        await using var row = await count.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal(1L, row.GetInt64(0));
        Assert.Equal(1L, row.GetInt64(1));
        Assert.Equal(1L, row.GetInt64(2));

        // Every stored identity/request element is a SHA256 digest, not plaintext.
        await using var digestSql = db.Admin.CreateCommand(
            "SELECT octet_length(subject_digest),octet_length(key_digest)," +
            "octet_length(request_digest) FROM legacy_xml_import_keys WHERE tenant_id=@tenant");
        digestSql.Parameters.AddWithValue("tenant", tenant.Value);
        await using var digests = await digestSql.ExecuteReaderAsync();
        Assert.True(await digests.ReadAsync());
        Assert.Equal(32, digests.GetInt32(0));
        Assert.Equal(32, digests.GetInt32(1));
        Assert.Equal(32, digests.GetInt32(2));
    }

    [Fact]
    public async Task V011_two_simultaneous_imports_for_one_reference_allow_only_one_commit()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var parsed = new LegacyXmlPreviewDto("accertatore",
            "CONCURRENT-" + Guid.NewGuid().ToString("N"),
            new DateOnly(2026, 11, 19), null, 0);
        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("same-xml"));
        var repo = new PgLegacyXmlCaseImportStore(db.App);
        var jobs = await Task.WhenAll(new[] {"xml-import-unique-key-001", "xml-import-unique-key-002"}
            .Select(async key =>
            {
                try { return (Ok: true, Outcome: await repo.CommitAsync(tenant, "subject-A", key, digest, parsed)); }
                catch (LegacyXmlReferenceConflictException) { return (Ok: false, Outcome: (LegacyXmlCaseImportResult?)null); }
            }));
        Assert.Equal(1, jobs.Count(x => x.Ok));
        Assert.Equal(1, jobs.Count(x => !x.Ok));
        await using var count = db.Admin.CreateCommand(
            "SELECT count(*) FROM sinistri WHERE tenant_id=@tenant AND external_reference=@reference");
        count.Parameters.AddWithValue("tenant", tenant.Value);
        count.Parameters.AddWithValue("reference", parsed.ExternalReference);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task V012_every_writer_denies_duplicate_of_imported_reference_but_keeps_legacy_duplicates()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var repo = new PgSinistroRepository(db.App);
        var import = new PgLegacyXmlCaseImportStore(db.App);
        var a = TenantId.From(Guid.NewGuid());
        var b = TenantId.From(Guid.NewGuid());
        var reference = "GUARD-" + Guid.NewGuid().ToString("N");
        var preview = new LegacyXmlPreviewDto("classic", reference,
            new DateOnly(2026, 11, 20), null, 0);
        var digest = System.Security.Cryptography.SHA256.HashData("guard-payload"u8);
        var imported = await import.CommitAsync(a, "signed-actor", "import-guard-key-0000001", digest, preview);

        var denied = Sinistro.Create(a, reference, new DateOnly(2026, 11, 20));
        await Assert.ThrowsAsync<LegacyXmlReferenceConflictException>(() =>
            repo.AddAuditedAsync(denied, Event(a, denied.Id, "sinistro.create")));
        Assert.Null(await repo.FindAsync(a, denied.Id));

        // A real app-role SQL writer cannot bypass the application repository.
        await using (var conn = await db.App.OpenConnectionAsync())
        await using (var transaction = await conn.BeginTransactionAsync())
        {
            await using (var scope = new NpgsqlCommand(
                "SELECT set_config('peritial.tenant_id',@tenant,true)", conn, transaction))
            {
                scope.Parameters.AddWithValue("tenant", a.Value.ToString("D"));
                await scope.ExecuteNonQueryAsync();
            }
            await using var raw = new NpgsqlCommand(
                "INSERT INTO sinistri (tenant_id,id,external_reference,occurred_on,created_at_utc) " +
                "VALUES (@tenant,@id,@reference,DATE '2026-11-20',now())",
                conn, transaction);
            raw.Parameters.AddWithValue("tenant", a.Value);
            raw.Parameters.AddWithValue("id", Guid.NewGuid());
            raw.Parameters.AddWithValue("reference", reference);
            var violation = await Assert.ThrowsAsync<PostgresException>(
                () => raw.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, violation.SqlState);
            Assert.Equal("ck_imported_sinistro_reference_guard", violation.ConstraintName);
            await transaction.RollbackAsync();
        }

        // Different tenant, identical reference, is fully independent.
        var other = Sinistro.Create(b, reference, new DateOnly(2026, 11, 20));
        await repo.AddAuditedAsync(other, Event(b, other.Id, "sinistro.create"));
        Assert.NotNull(await repo.FindAsync(a, imported.SinistroId));
        Assert.NotNull(await repo.FindAsync(b, other.Id));

        // Historical duplicates are still allowed when no XML provenance
        // exists; this change must not invent a global UNIQUE constraint.
        var legacyReference = "LEGACY-DUP-" + Guid.NewGuid().ToString("N");
        foreach (var existing in new[] {
            Sinistro.Create(a, legacyReference, new DateOnly(2026, 11, 20)),
            Sinistro.Create(a, legacyReference, new DateOnly(2026, 11, 19)) })
            await repo.AddAuditedAsync(existing, Event(a, existing.Id, "sinistro.create"));
        await using var check = db.Admin.CreateCommand(
            "SELECT count(*) FROM sinistri WHERE tenant_id=@tenant AND external_reference=@reference");
        check.Parameters.AddWithValue("tenant", a.Value);
        check.Parameters.AddWithValue("reference", legacyReference);
        Assert.Equal(2L, (long)(await check.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task V012_protects_imported_case_from_update_and_blocks_retargeting()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var repo = new PgSinistroRepository(db.App);
        var tenant = TenantId.From(Guid.NewGuid());
        var reference = "IMMUTABLE-" + Guid.NewGuid().ToString("N");
        var preview = new LegacyXmlPreviewDto("accertatore", reference,
            new DateOnly(2026, 11, 19), null, 0);
        var digest = System.Security.Cryptography.SHA256.HashData("immutable"u8);
        var imported = await new PgLegacyXmlCaseImportStore(db.App).CommitAsync(
            tenant, "signed-actor", "immutable-key-00000001", digest, preview);

        var snapshot = (await repo.FindAsync(tenant, imported.SinistroId))!;
        snapshot.Update(tenant, reference, new DateOnly(2026, 11, 18));
        await Assert.ThrowsAsync<LegacyXmlReferenceConflictException>(() =>
            repo.UpdateAuditedAsync(snapshot, Event(tenant, snapshot.Id, "sinistro.update")));

        var ordinary = Sinistro.Create(tenant, "ORDINARY-" + Guid.NewGuid().ToString("N"),
            new DateOnly(2026, 11, 19));
        await repo.AddAuditedAsync(ordinary, Event(tenant, ordinary.Id, "sinistro.create"));
        ordinary.Update(tenant, reference, new DateOnly(2026, 11, 20));
        await Assert.ThrowsAsync<LegacyXmlReferenceConflictException>(() =>
            repo.UpdateAuditedAsync(ordinary, Event(tenant, ordinary.Id, "sinistro.update")));
        var unchanged = (await repo.FindAsync(tenant, ordinary.Id))!;
        Assert.NotEqual(reference, unchanged.ExternalReference);
        Assert.Equal(1L, unchanged.PersistedVersion);

        await using var audit = db.Admin.CreateCommand(
            "SELECT count(*) FROM security_audit WHERE tenant_id=@tenant AND " +
            "action='sinistro.update' AND outcome='succeeded' AND " +
            "resource_id IN (@imported,@ordinary)");
        audit.Parameters.AddWithValue("tenant", tenant.Value);
        audit.Parameters.AddWithValue("imported", imported.SinistroId);
        audit.Parameters.AddWithValue("ordinary", ordinary.Id);
        Assert.Equal(0L, (long)(await audit.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task V012_import_and_regular_case_create_from_independent_pools_serialize_without_duplicate()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        // NpgsqlDataSource.ConnectionString intentionally omits passwords; use
        // the fixture's scoped app-role test credential for the second pool.
        await using var otherPool = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(db.App.ConnectionString)
            { Password = "local-only-app" }.ConnectionString);
        var tenant = TenantId.From(Guid.NewGuid());
        var reference = "RACE-" + Guid.NewGuid().ToString("N");
        var preview = new LegacyXmlPreviewDto("classic", reference,
            new DateOnly(2026, 11, 20), null, 0);
        var digest = System.Security.Cryptography.SHA256.HashData("race-case"u8);
        var ordinary = Sinistro.Create(tenant, reference, preview.OccurredOn);
        var importedStore = new PgLegacyXmlCaseImportStore(db.App);
        var regularStore = new PgSinistroRepository(otherPool);

        async Task<bool> ImportAsync()
        {
            try
            {
                await importedStore.CommitAsync(tenant, "signed-actor", "race-key-0000000001",
                    digest, preview);
                return true;
            }
            catch (LegacyXmlReferenceConflictException) { return false; }
        }
        async Task<bool> RegularAsync()
        {
            try
            {
                await regularStore.AddAuditedAsync(ordinary, Event(tenant, ordinary.Id, "sinistro.create"));
                return true;
            }
            catch (LegacyXmlReferenceConflictException) { return false; }
        }
        var outcomes = await Task.WhenAll(ImportAsync(), RegularAsync());
        Assert.Single(outcomes.Where(x => x));
        await using var verify = db.Admin.CreateCommand(
            "SELECT count(*) FROM sinistri WHERE tenant_id=@tenant AND external_reference=@reference");
        verify.Parameters.AddWithValue("tenant", tenant.Value);
        verify.Parameters.AddWithValue("reference", reference);
        Assert.Equal(1L, (long)(await verify.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task V012_contended_import_mutex_times_out_without_ledger_audit_or_cross_tenant_block()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        // Independent real PG pool: an advisory lock is per connection and
        // would be reentrant if the import reused the holder session.
        await using var otherPool = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(db.App.ConnectionString)
            { Password = "local-only-app" }.ConnectionString);
        var importer = new PgLegacyXmlCaseImportStore(otherPool);
        var tenant = TenantId.From(Guid.NewGuid());
        var other = TenantId.From(Guid.NewGuid());
        var reference = "LOCK-WAIT-" + Guid.NewGuid().ToString("N");
        var preview = new LegacyXmlPreviewDto("classic", reference,
            new DateOnly(2026, 11, 22), null, 0);
        var digest = System.Security.Cryptography.SHA256.HashData("lock-wait-xml"u8);
        const string key = "mutex-wait-key-000001";

        async Task<(long Cases, long Proofs, long SuccessAudits)> Counts(TenantId actor)
        {
            await using var sql = db.Admin.CreateCommand(
                "SELECT " +
                "(SELECT count(*) FROM sinistri WHERE tenant_id=@tenant " +
                "AND external_reference=@reference), " +
                "(SELECT count(*) FROM legacy_xml_import_keys WHERE tenant_id=@tenant), " +
                "(SELECT count(*) FROM security_audit WHERE tenant_id=@tenant " +
                "AND action='sinistro.import-xml' AND outcome='succeeded')");
            sql.Parameters.AddWithValue("tenant", actor.Value);
            sql.Parameters.AddWithValue("reference", reference);
            await using var row = await sql.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            return (row.GetInt64(0), row.GetInt64(1), row.GetInt64(2));
        }

        await using var holder = await db.App.OpenConnectionAsync();
        await using var heldTransaction = await holder.BeginTransactionAsync();
        await using (var mutex = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@input, 0))",
            holder, heldTransaction))
        {
            mutex.Parameters.AddWithValue("input",
                tenant.Value.ToString("D") + "\n" + reference);
            await mutex.ExecuteNonQueryAsync();
        }

        // This must not stall indefinitely waiting for another process.
        // A generous client-side deadline makes a missing timeout fail
        // the test without hanging CI. The store raises a distinct busy
        // outcome, never a false duplicate-reference diagnosis.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await Assert.ThrowsAsync<LegacyXmlImportBusyException>(() =>
            importer.CommitAsync(tenant, "signed-actor", key,
                digest, preview, deadline.Token));
        Assert.Equal((0L, 0L, 0L), await Counts(tenant));

        // The lock is scoped by the signed tenant and exact reference.
        // A different tenant with the same reference must still import.
        var foreign = await importer.CommitAsync(other, "signed-actor",
            "mutex-tenant-other-0001", digest, preview);
        Assert.False(foreign.Replayed);
        Assert.Equal((1L, 1L, 1L), await Counts(other));
        Assert.Equal((0L, 0L, 0L), await Counts(tenant));

        // After the holder releases its transaction, the very same key
        // must be reusable: no ghost reservation or poisoned pool state.
        await heldTransaction.CommitAsync();
        var created = await importer.CommitAsync(tenant, "signed-actor", key,
            digest, preview);
        Assert.False(created.Replayed);
        var replay = await importer.CommitAsync(tenant, "signed-actor", key,
            digest, preview);
        Assert.True(replay.Replayed);
        Assert.Equal(created.SinistroId, replay.SinistroId);
        Assert.NotEqual(foreign.SinistroId, created.SinistroId);
        Assert.Equal((1L, 1L, 1L), await Counts(tenant));
        Assert.Equal((1L, 1L, 1L), await Counts(other));

        // A genuine duplicate AFTER import is a different, permanent
        // reference conflict. It is never masked as transient mutex busy.
        var duplicate = Sinistro.Create(tenant, reference, preview.OccurredOn);
        await Assert.ThrowsAsync<LegacyXmlReferenceConflictException>(() =>
            new PgSinistroRepository(otherPool).AddAuditedAsync(
                duplicate, Event(tenant, duplicate.Id, "sinistro.create")));
        Assert.Equal((1L, 1L, 1L), await Counts(tenant));
    }

    [Fact]
    public async Task V011_failed_success_audit_rolls_back_both_case_and_ledger()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var parsed = new LegacyXmlPreviewDto("classic",
            "NO-AUDIT-" + Guid.NewGuid().ToString("N"),
            new DateOnly(2026, 11, 19), null, 0);
        // Isolated schema: deliberately deny audit insert for the app role.
        // If the implementation commits the case or idempotency row separately,
        // this test will expose the partial commit.
        await using (var revoke = db.Admin.CreateCommand(
            "REVOKE INSERT ON security_audit FROM peritial_app"))
            await revoke.ExecuteNonQueryAsync();
        var digest = System.Security.Cryptography.SHA256.HashData("safe-input"u8);
        await Assert.ThrowsAsync<PostgresException>(() =>
            new PgLegacyXmlCaseImportStore(db.App).CommitAsync(
                tenant, "subject-A", "audit-rejection-key-00001", digest, parsed));

        await using var count = db.Admin.CreateCommand(
            "SELECT (SELECT count(*) FROM sinistri WHERE tenant_id=@tenant " +
            "AND external_reference=@reference), " +
            "(SELECT count(*) FROM legacy_xml_import_keys WHERE tenant_id=@tenant)");
        count.Parameters.AddWithValue("tenant", tenant.Value);
        count.Parameters.AddWithValue("reference", parsed.ExternalReference);
        await using var rows = await count.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal(0L, rows.GetInt64(0));
        Assert.Equal(0L, rows.GetInt64(1));
    }

    [Fact]
    public async Task Stale_update_cannot_overwrite_newer_sinistro_or_append_success_audit()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var repo = new PgSinistroRepository(db.App);
        var a = TenantId.From(Guid.NewGuid());
        var initial = Sinistro.Create(a, "SIN-CONCURRENT", new DateOnly(2026, 10, 1));
        await repo.AddAuditedAsync(initial, Event(a, initial.Id, "sinistro.create"));
        var one = (await repo.FindAsync(a, initial.Id))!;
        var stale = (await repo.FindAsync(a, initial.Id))!;

        one.Update(a, "SIN-CURRENT", new DateOnly(2026, 10, 2));
        await repo.UpdateAuditedAsync(one, Event(a, one.Id, "sinistro.update"));
        Assert.Equal(2L, one.PersistedVersion);

        stale.Update(a, "SIN-STALE", new DateOnly(2026, 10, 3));
        await Assert.ThrowsAsync<SinistroConcurrencyConflictException>(() =>
            repo.UpdateAuditedAsync(stale, Event(a, stale.Id, "sinistro.update")));
        var restored = (await repo.FindAsync(a, initial.Id))!;
        Assert.Equal("SIN-CURRENT", restored.ExternalReference);
        Assert.Equal(2L, restored.PersistedVersion);

        await using var count = db.Admin.CreateCommand(
            "SELECT count(*) FROM security_audit WHERE tenant_id=@tenant " +
            "AND resource_id=@id AND action='sinistro.update' AND outcome='succeeded'");
        count.Parameters.AddWithValue("tenant", a.Value);
        count.Parameters.AddWithValue("id", initial.Id);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Audit_failure_rolls_back_both_insert_and_update_and_wrong_tenant_is_rejected()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var repo = new PgSinistroRepository(db.App);
        var a = TenantId.From(Guid.NewGuid());
        var b = TenantId.From(Guid.NewGuid());
        var created = Sinistro.Create(a, "SIN-AUDIT", new DateOnly(2026, 9, 1));
        var previousAudit = Event(a, created.Id, "sinistro.create");
        await new PgSecurityAuditTrail(db.App).AppendAsync(previousAudit);
        var conflict = await Assert.ThrowsAsync<PostgresException>(() =>
            repo.AddAuditedAsync(created, previousAudit));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, conflict.SqlState);
        Assert.Null(await repo.FindAsync(a, created.Id));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            repo.AddAuditedAsync(created, Event(b, created.Id, "sinistro.create")));
        await repo.AddAuditedAsync(created, Event(a, created.Id, "sinistro.create"));
        var staleVersion = created.PersistedVersion;

        created.Update(a, "SIN-AUDIT-UPDATED", new DateOnly(2026, 9, 2));
        var duplicate = previousAudit with {Action = "sinistro.update"};
        var failedUpdate = await Assert.ThrowsAsync<PostgresException>(() =>
            repo.UpdateAuditedAsync(created, duplicate));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, failedUpdate.SqlState);
        Assert.Equal("SIN-AUDIT", (await repo.FindAsync(a, created.Id))!.ExternalReference);
        Assert.Equal(staleVersion, created.PersistedVersion);
        await repo.UpdateAuditedAsync(created, Event(a, created.Id, "sinistro.update"));
        Assert.Equal("SIN-AUDIT-UPDATED", (await repo.FindAsync(a, created.Id))!.ExternalReference);
    }

    [Fact]
    public async Task Search_filters_inside_postgres_before_limit_and_escapes_ilike_wildcards_with_RLS()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var repo = new PgSinistroRepository(db.App);
        var a = TenantId.From(Guid.NewGuid());
        var b = TenantId.From(Guid.NewGuid());
        var entities = new[]
        {
            Sinistro.Create(a, "FRESH-NONMATCH", new DateOnly(2026, 12, 1)),
            Sinistro.Create(a, "CL_%Case-1", new DateOnly(2026, 10, 5)),
            Sinistro.Create(a, "cl_%case-2", new DateOnly(2026, 10, 7)),
            Sinistro.Create(a, "CL123-FALSE-POSITIVE", new DateOnly(2026, 11, 20)),
            Sinistro.Create(b, "CL_%PRIVATE", new DateOnly(2026, 10, 6)),
            Sinistro.Create(a, "CL_%OUTSIDE-DATES", new DateOnly(2026, 8, 1)),
        };
        foreach (var entity in entities)
            await repo.AddAuditedAsync(entity, Event(entity.TenantId, entity.Id, "sinistro.create"));

        // Literal '_' and '%' MUST NOT match arbitrary chars or records.
        var aResults = await repo.SearchAsync(a, "CL_%", new DateOnly(2026, 10, 1),
            new DateOnly(2026, 11, 30), limit: 2);
        Assert.Equal(new[] {entities[2].Id, entities[1].Id}, aResults.Select(x => x.Id));
        Assert.All(aResults, x => Assert.Equal(a, x.TenantId));
        Assert.Empty(await repo.SearchAsync(b, "FRESH"));
        Assert.Equal(entities[4].Id, (await repo.SearchAsync(b, "cL_%")).Single().Id);

        // Also escape a literal escape character (!) in the prefix.
        var escape = Sinistro.Create(a, "ESC!_%EXACT", new DateOnly(2026, 10, 8));
        await repo.AddAuditedAsync(escape, Event(a, escape.Id, "sinistro.create"));
        Assert.Equal(escape.Id, (await repo.SearchAsync(a, "esc!_%")).Single().Id);
    }

    [Fact]
    public async Task Filtered_keyset_search_pages_in_sql_with_literal_wildcards_date_bounds_and_RLS()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var repo = new PgSinistroRepository(db.App);
        var tenant = TenantId.From(Guid.NewGuid());
        var foreign = TenantId.From(Guid.NewGuid());
        var date = new DateOnly(2026, 11, 18);
        var ids = Enumerable.Range(1, 3).Select(i =>
            Guid.Parse($"00000000-0000-0000-0000-{i:000000000000}")).ToArray();
        var items = new[]
        {
            Sinistro.Restore(ids[1], tenant, "PG_%SECOND", date, null, DateTime.UtcNow),
            Sinistro.Restore(ids[0], tenant, "PG_%FIRST", date, null, DateTime.UtcNow),
            Sinistro.Restore(ids[2], tenant, "PG_%OLDER", date.AddDays(-1), null, DateTime.UtcNow),
            Sinistro.Create(tenant, "PGZZDECOY", date.AddDays(3)),
            Sinistro.Create(tenant, "PG_%OUTSIDE", date.AddDays(-30)),
            Sinistro.Create(foreign, "PG_%SECRET", date.AddDays(2)),
        };
        foreach (var item in items)
            await repo.AddAuditedAsync(item, Event(item.TenantId, item.Id, "sinistro.create"));

        var filter = SinistroSearchFilter.Create(" pg_% ", date.AddDays(-2), date.AddDays(1), 2);
        var first = await repo.SearchPageAsync(tenant, filter, null, fetchLimit: 3);
        Assert.Equal(ids, first.Select(x => x.Id));
        Assert.All(first, item => Assert.Equal(tenant, item.TenantId));
        var second = await repo.SearchPageAsync(tenant, filter,
            new SinistroPageBoundary(date, ids[1]), fetchLimit: 3);
        Assert.Equal(new[] {ids[2]}, second.Select(x => x.Id));
        Assert.Empty(await repo.SearchPageAsync(foreign, filter, null, fetchLimit: 3));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repo.SearchPageAsync(tenant, filter, null, fetchLimit: 52));
    }

    [Fact]
    public async Task Keyset_page_uses_tenant_RLS_and_date_id_boundary_in_postgres_not_offset()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var repo = new PgSinistroRepository(db.App);
        var a = TenantId.From(Guid.NewGuid());
        var b = TenantId.From(Guid.NewGuid());
        var date = new DateOnly(2026, 11, 18);
        var ids = Enumerable.Range(1, 4).Select(i =>
            Guid.Parse($"00000000-0000-0000-0000-{i:000000000000}")).ToArray();
        var entities = new[]
        {
            Sinistro.Restore(ids[1], a, "A-SECOND", date, null, DateTime.UtcNow),
            Sinistro.Restore(ids[0], a, "A-FIRST", date, null, DateTime.UtcNow),
            Sinistro.Restore(ids[2], a, "A-OLD", date.AddDays(-1), null, DateTime.UtcNow),
            Sinistro.Restore(ids[3], a, "A-NEW", date.AddDays(1), null, DateTime.UtcNow),
            Sinistro.Create(b, "B-PRIVATE", date.AddDays(3))
        };
        foreach (var item in entities)
            await repo.AddAuditedAsync(item, Event(item.TenantId, item.Id, "sinistro.create"));

        var first = await repo.PageAsync(a, null, fetchLimit: 3);
        Assert.Equal(new[] {ids[3], ids[0], ids[1]}, first.Select(x => x.Id));
        var second = await repo.PageAsync(a, new SinistroPageBoundary(date, ids[1]), fetchLimit: 3);
        Assert.Equal(new[] {ids[2]}, second.Select(x => x.Id));
        Assert.All(first.Concat(second), x => Assert.Equal(a, x.TenantId));
        Assert.Equal(new[] {entities[4].Id},
            (await repo.PageAsync(b, null, fetchLimit: 3)).Select(x => x.Id));
        Assert.Empty(await repo.PageAsync(a,
            new SinistroPageBoundary(date.AddDays(-2), ids[2]), fetchLimit: 3));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repo.PageAsync(a, null, fetchLimit: 52));
    }

    [Fact]
    public async Task V005_delete_is_tenant_scoped_cas_audited_and_rejects_linked_perizie()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var repo = new PgSinistroRepository(db.App);
        var a = TenantId.From(Guid.NewGuid());
        var b = TenantId.From(Guid.NewGuid());
        var linked = Sinistro.Create(a, "DELETE-LINKED", new DateOnly(2026, 11, 18));
        var free = Sinistro.Create(a, "DELETE-FREE", new DateOnly(2026, 11, 17));
        var foreign = Sinistro.Create(b, "DELETE-FOREIGN", new DateOnly(2026, 11, 16));
        foreach (var item in new[] {linked, free, foreign})
            await repo.AddAuditedAsync(item, Event(item.TenantId, item.Id, "sinistro.create"));
        var perizia = Perizia.Create(a, "DELETE-LINK-CASE-" + Guid.NewGuid().ToString("N"));
        await new PgPeriziaRepository(db.App).AddAuditedAsync(perizia,
            Event(a, perizia.Id, "perizia.create"));
        await new PgPeriziaSinistroLinkRepository(db.App).LinkAuditedAsync(
            a, perizia.Id, linked.Id, 1, Event(a, perizia.Id, "perizia.link-sinistro"));

        await Assert.ThrowsAsync<SinistroLinkedPerizieException>(() =>
            repo.DeleteAuditedAsync(a, linked.Id, 1, Event(a, linked.Id, "sinistro.delete")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            repo.DeleteAuditedAsync(a, foreign.Id, 1, Event(a, foreign.Id, "sinistro.delete")));
        await Assert.ThrowsAsync<SinistroConcurrencyConflictException>(() =>
            repo.DeleteAuditedAsync(a, free.Id, 2, Event(a, free.Id, "sinistro.delete")));
        Assert.NotNull(await repo.FindAsync(a, linked.Id));
        Assert.NotNull(await repo.FindAsync(a, free.Id));
        Assert.NotNull(await repo.FindAsync(b, foreign.Id));

        // An audit failure must roll back the SQL DELETE in the same transaction.
        var duplicate = Event(a, free.Id, "sinistro.delete");
        await new PgSecurityAuditTrail(db.App).AppendAsync(duplicate);
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            repo.DeleteAuditedAsync(a, free.Id, 1, duplicate));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.NotNull(await repo.FindAsync(a, free.Id));
        await repo.DeleteAuditedAsync(a, free.Id, 1, Event(a, free.Id, "sinistro.delete"));
        Assert.Null(await repo.FindAsync(a, free.Id));
        Assert.Equal(linked.Id, (await repo.ListAsync(a)).Single().Id);
        Assert.Equal(foreign.Id, (await repo.ListAsync(b)).Single().Id);

        await using var count = db.Admin.CreateCommand(
            "SELECT count(*) FROM security_audit WHERE tenant_id=@tenant " +
            "AND resource_id=@id AND action='sinistro.delete' AND outcome='succeeded'");
        count.Parameters.AddWithValue("tenant", a.Value);
        count.Parameters.AddWithValue("id", free.Id);
        Assert.Equal(2L, (long)(await count.ExecuteScalarAsync())!);
        await using var linkedAudit = db.Admin.CreateCommand(
            "SELECT count(*) FROM security_audit WHERE tenant_id=@tenant " +
            "AND resource_id=@id AND action='sinistro.delete'");
        linkedAudit.Parameters.AddWithValue("tenant", a.Value);
        linkedAudit.Parameters.AddWithValue("id", linked.Id);
        Assert.Equal(0L, (long)(await linkedAudit.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task V004_links_perizie_to_own_sinistro_with_tenant_composite_FK_and_atomic_audit()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var tenantA = TenantId.From(Guid.NewGuid());
        var tenantB = TenantId.From(Guid.NewGuid());
        var sinistri = new PgSinistroRepository(db.App);
        var perizie = new PgPeriziaRepository(db.App);
        var links = new PgPeriziaSinistroLinkRepository(db.App);
        var a = Sinistro.Create(tenantA, "SIN-LINK-A", new DateOnly(2026, 10, 1));
        var b = Sinistro.Create(tenantB, "SIN-LINK-B", new DateOnly(2026, 10, 2));
        await sinistri.AddAuditedAsync(a, Event(tenantA, a.Id, "sinistro.create"));
        await sinistri.AddAuditedAsync(b, Event(tenantB, b.Id, "sinistro.create"));
        var periziaA = Perizia.Create(tenantA, "PERIZIA-LINK-A-" + Guid.NewGuid().ToString("N"));
        var periziaB = Perizia.Create(tenantB, "PERIZIA-LINK-B-" + Guid.NewGuid().ToString("N"));
        await perizie.AddAuditedAsync(periziaA, Event(tenantA, periziaA.Id, "perizia.create"));
        await perizie.AddAuditedAsync(periziaB, Event(tenantB, periziaB.Id, "perizia.create"));

        Assert.Empty(await links.ListPeriziaIdsAsync(tenantA, a.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            links.LinkAuditedAsync(tenantA, periziaA.Id, b.Id, 1,
                Event(tenantA, periziaA.Id, "perizia.link-sinistro")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            links.LinkAuditedAsync(tenantB, periziaA.Id, b.Id, 1,
                Event(tenantB, periziaA.Id, "perizia.link-sinistro")));
        Assert.Empty(await links.ListPeriziaIdsAsync(tenantA, a.Id));

        await links.LinkAuditedAsync(tenantA, periziaA.Id, a.Id, 1,
            Event(tenantA, periziaA.Id, "perizia.link-sinistro"));
        Assert.Equal(new[] { periziaA.Id }, await links.ListPeriziaIdsAsync(tenantA, a.Id));
        Assert.Empty(await links.ListPeriziaIdsAsync(tenantB, b.Id));
        await Assert.ThrowsAsync<PeriziaConcurrencyConflictException>(() =>
            links.LinkAuditedAsync(tenantA, periziaA.Id, a.Id, 2,
                Event(tenantA, periziaA.Id, "perizia.link-sinistro")));
        Assert.Equal(1L, await CountLinkAuditAsync(db.Admin, tenantA, periziaA.Id));

        // Elevated SQL still cannot cross the V004 same-tenant composite FK.
        // Bump the V007 revision correctly to isolate the FK check itself;
        // an unversioned write is now independently rejected by V007.
        await using var sql = db.Admin.CreateCommand(
            "UPDATE perizie SET sinistro_id=@sinistro, " +
            "sinistro_link_version=sinistro_link_version+1 " +
            "WHERE tenant_id=@tenant AND id=@perizia");
        sql.Parameters.AddWithValue("sinistro", b.Id);
        sql.Parameters.AddWithValue("tenant", tenantA.Value);
        sql.Parameters.AddWithValue("perizia", periziaA.Id);
        var fk = await Assert.ThrowsAsync<PostgresException>(() => sql.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, fk.SqlState);
        Assert.Equal(new[] { periziaA.Id }, await links.ListPeriziaIdsAsync(tenantA, a.Id));
    }

    [Fact]
    public async Task Unlink_is_tenant_scoped_CAS_audited_and_rolls_back_on_duplicate_audit()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var foreignTenant = TenantId.From(Guid.NewGuid());
        var sinistri = new PgSinistroRepository(db.App);
        var perizie = new PgPeriziaRepository(db.App);
        var links = new PgPeriziaSinistroLinkRepository(db.App);
        var sinistro = Sinistro.Create(tenant, "UNLINK-PG-OLD", new DateOnly(2026, 11, 18));
        var second = Sinistro.Create(tenant, "UNLINK-PG-NEXT", new DateOnly(2026, 11, 17));
        await sinistri.AddAuditedAsync(sinistro, Event(tenant, sinistro.Id, "sinistro.create"));
        await sinistri.AddAuditedAsync(second, Event(tenant, second.Id, "sinistro.create"));
        var caseA = Perizia.Create(tenant, "UNLINK-PG-" + Guid.NewGuid().ToString("N"));
        var foreign = Perizia.Create(foreignTenant, "UNLINK-PG-FOREIGN-" + Guid.NewGuid().ToString("N"));
        await perizie.AddAuditedAsync(caseA, Event(tenant, caseA.Id, "perizia.create"));
        await perizie.AddAuditedAsync(foreign, Event(foreignTenant, foreign.Id, "perizia.create"));
        await links.LinkAuditedAsync(tenant, caseA.Id, sinistro.Id, 1,
            Event(tenant, caseA.Id, "perizia.link-sinistro"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            links.UnlinkAuditedAsync(foreignTenant, caseA.Id, sinistro.Id, 2,
                Event(foreignTenant, caseA.Id, "perizia.unlink-sinistro")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            links.UnlinkAuditedAsync(tenant, foreign.Id, sinistro.Id, 2,
                Event(tenant, foreign.Id, "perizia.unlink-sinistro")));
        await Assert.ThrowsAsync<PeriziaConcurrencyConflictException>(() =>
            links.UnlinkAuditedAsync(tenant, caseA.Id, second.Id, 2,
                Event(tenant, caseA.Id, "perizia.unlink-sinistro")));
        Assert.Equal(sinistro.Id, await links.FindSinistroIdForPeriziaAsync(tenant, caseA.Id));

        var duplicate = Event(tenant, caseA.Id, "perizia.unlink-sinistro");
        await new PgSecurityAuditTrail(db.App).AppendAsync(duplicate);
        var conflict = await Assert.ThrowsAsync<PostgresException>(() =>
            links.UnlinkAuditedAsync(tenant, caseA.Id, sinistro.Id, 2, duplicate));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, conflict.SqlState);
        Assert.Equal(sinistro.Id, await links.FindSinistroIdForPeriziaAsync(tenant, caseA.Id));

        await links.UnlinkAuditedAsync(tenant, caseA.Id, sinistro.Id, 2,
            Event(tenant, caseA.Id, "perizia.unlink-sinistro"));
        Assert.Null(await links.FindSinistroIdForPeriziaAsync(tenant, caseA.Id));
        Assert.Empty(await links.ListPeriziaIdsAsync(tenant, sinistro.Id));
        await Assert.ThrowsAsync<PeriziaConcurrencyConflictException>(() =>
            links.UnlinkAuditedAsync(tenant, caseA.Id, sinistro.Id, 3,
                Event(tenant, caseA.Id, "perizia.unlink-sinistro")));
        await links.LinkAuditedAsync(tenant, caseA.Id, second.Id, 3,
            Event(tenant, caseA.Id, "perizia.link-sinistro"));
        Assert.Equal(second.Id, await links.FindSinistroIdForPeriziaAsync(tenant, caseA.Id));

        await using var success = db.Admin.CreateCommand(
            "SELECT count(*) FROM security_audit WHERE tenant_id=@tenant " +
            "AND resource_id=@perizia AND action='perizia.unlink-sinistro' AND outcome='succeeded'");
        success.Parameters.AddWithValue("tenant", tenant.Value);
        success.Parameters.AddWithValue("perizia", caseA.Id);
        Assert.Equal(2L, (long)(await success.ExecuteScalarAsync())!); // pre-inserted duplicate and committed unlink
    }

    [Fact]
    public async Task Reparent_is_atomic_CAS_tenant_RLS_target_validation_and_audit_rollback_in_postgres()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var a = TenantId.From(Guid.NewGuid());
        var b = TenantId.From(Guid.NewGuid());
        var perizie = new PgPeriziaRepository(db.App);
        var sinistri = new PgSinistroRepository(db.App);
        var links = new PgPeriziaSinistroLinkRepository(db.App);
        var source = Sinistro.Create(a, "REPAR-PG-SOURCE", new DateOnly(2026, 11, 18));
        var target = Sinistro.Create(a, "REPAR-PG-TARGET", new DateOnly(2026, 11, 17));
        var foreignTarget = Sinistro.Create(b, "REPAR-PG-FOREIGN", new DateOnly(2026, 11, 16));
        foreach (var item in new[] {source, target, foreignTarget})
            await sinistri.AddAuditedAsync(item, Event(item.TenantId, item.Id, "sinistro.create"));
        var perizia = Perizia.Create(a, "REPAR-PG-" + Guid.NewGuid().ToString("N"));
        var foreignPerizia = Perizia.Create(b, "REPAR-PG-FOREIGN-" + Guid.NewGuid().ToString("N"));
        await perizie.AddAuditedAsync(perizia, Event(a, perizia.Id, "perizia.create"));
        await perizie.AddAuditedAsync(foreignPerizia, Event(b, foreignPerizia.Id, "perizia.create"));
        await links.LinkAuditedAsync(a, perizia.Id, source.Id, 1,
            Event(a, perizia.Id, "perizia.link-sinistro"));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            links.ReparentAuditedAsync(a, perizia.Id, source.Id, foreignTarget.Id, 2,
                Event(a, perizia.Id, "perizia.reparent-sinistro")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            links.ReparentAuditedAsync(a, perizia.Id, source.Id, Guid.NewGuid(), 2,
                Event(a, perizia.Id, "perizia.reparent-sinistro")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            links.ReparentAuditedAsync(b, perizia.Id, source.Id, foreignTarget.Id, 2,
                Event(b, perizia.Id, "perizia.reparent-sinistro")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            links.ReparentAuditedAsync(a, foreignPerizia.Id, source.Id, target.Id, 2,
                Event(a, foreignPerizia.Id, "perizia.reparent-sinistro")));
        await Assert.ThrowsAsync<PeriziaConcurrencyConflictException>(() =>
            links.ReparentAuditedAsync(a, perizia.Id, Guid.NewGuid(), target.Id, 2,
                Event(a, perizia.Id, "perizia.reparent-sinistro")));
        Assert.Equal(source.Id, await links.FindSinistroIdForPeriziaAsync(a, perizia.Id));
        Assert.Equal(new[] {perizia.Id}, await links.ListPeriziaIdsAsync(a, source.Id));

        // Terminal workflow is not editable: a billed, duplicated or
        // cancelled perizia must never have its Sinistro silently replaced.
        var cancelled = Perizia.Create(a, "REPAR-PG-CANCELLED-" + Guid.NewGuid().ToString("N"));
        await perizie.AddAuditedAsync(cancelled, Event(a, cancelled.Id, "perizia.create"));
        await links.LinkAuditedAsync(a, cancelled.Id, source.Id, 1,
            Event(a, cancelled.Id, "perizia.link-sinistro"));
        cancelled.Cancel(a);
        await perizie.UpdateAuditedAsync(cancelled,
            Event(a, cancelled.Id, "perizia.cancel"));
        await Assert.ThrowsAsync<PeriziaConcurrencyConflictException>(() =>
            links.ReparentAuditedAsync(a, cancelled.Id, source.Id, target.Id, 2,
                Event(a, cancelled.Id, "perizia.reparent-sinistro")));
        Assert.Equal(source.Id, await links.FindSinistroIdForPeriziaAsync(a, cancelled.Id));

        var duplicate = Event(a, perizia.Id, "perizia.reparent-sinistro");
        await new PgSecurityAuditTrail(db.App).AppendAsync(duplicate);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            links.ReparentAuditedAsync(a, perizia.Id, source.Id, target.Id, 2, duplicate));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal(source.Id, await links.FindSinistroIdForPeriziaAsync(a, perizia.Id));

        await links.ReparentAuditedAsync(a, perizia.Id, source.Id, target.Id, 2,
            Event(a, perizia.Id, "perizia.reparent-sinistro"));
        Assert.Equal(target.Id, await links.FindSinistroIdForPeriziaAsync(a, perizia.Id));
        // The cancelled perizia is intentionally still linked to source;
        // moving the active case must not alter another workflow row.
        Assert.Equal(new[] {cancelled.Id},
            await links.ListPeriziaIdsAsync(a, source.Id));
        Assert.Equal(new[] {perizia.Id}, await links.ListPeriziaIdsAsync(a, target.Id));
        await Assert.ThrowsAsync<PeriziaConcurrencyConflictException>(() =>
            links.ReparentAuditedAsync(a, perizia.Id, source.Id, target.Id, 3,
                Event(a, perizia.Id, "perizia.reparent-sinistro")));
        Assert.Equal(target.Id, await links.FindSinistroIdForPeriziaAsync(a, perizia.Id));

        await using var count = db.Admin.CreateCommand(
            "SELECT count(*) FROM security_audit WHERE tenant_id=@tenant AND " +
            "resource_id=@id AND action='perizia.reparent-sinistro' AND outcome='succeeded'");
        count.Parameters.AddWithValue("tenant", a.Value);
        count.Parameters.AddWithValue("id", perizia.Id);
        Assert.Equal(2L, (long)(await count.ExecuteScalarAsync())!); // pre-inserted plus committed
    }

    [Fact]
    public async Task V009_document_metadata_cannot_be_rewritten_or_deleted_by_app_sql()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var perizia = Perizia.Create(tenant, "V009-DOC-" + Guid.NewGuid().ToString("N"));
        await new PgPeriziaRepository(db.App).AddAuditedAsync(
            perizia, Event(tenant, perizia.Id, "perizia.create"));
        var documentId = Guid.NewGuid();
        var originalKey = $"{tenant.Value:N}/{documentId:N}.pdf";
        var documents = new PgDocumentRepository(db.App);
        // Test-only insertion seam: production ingestion uses AddAuditedAsync.
        await documents.AddAsync(new DocumentRecord(
            documentId, tenant, perizia.Id, "original.pdf", "application/pdf",
            8, originalKey, DateTime.UtcNow));

        await using (var grants = db.Admin.CreateCommand(
            "SELECT has_table_privilege('peritial_app','documents','SELECT') " +
            "AND has_table_privilege('peritial_app','documents','INSERT') " +
            "AND NOT has_table_privilege('peritial_app','documents','UPDATE') " +
            "AND NOT has_table_privilege('peritial_app','documents','DELETE') " +
            "AND NOT has_table_privilege('peritial_recovery','documents','UPDATE') " +
            "AND NOT has_table_privilege('peritial_recovery','documents','DELETE')"))
            Assert.True((bool)(await grants.ExecuteScalarAsync())!);

        await using (var connection = await db.App.OpenConnectionAsync())
        {
            foreach (var mutation in new[] {
                "UPDATE documents SET storage_key='forged.pdf' WHERE tenant_id=@tenant AND id=@id",
                "UPDATE documents SET file_name='forged.pdf' WHERE tenant_id=@tenant AND id=@id",
                "DELETE FROM documents WHERE tenant_id=@tenant AND id=@id"
            })
            {
                await using var tx = await connection.BeginTransactionAsync();
                await using (var context = new NpgsqlCommand(
                    "SELECT set_config('peritial.tenant_id', @tenant, true)", connection, tx))
                {
                    context.Parameters.AddWithValue("tenant", tenant.Value.ToString("D"));
                    await context.ExecuteNonQueryAsync();
                }
                await using var command = new NpgsqlCommand(mutation, connection, tx);
                command.Parameters.AddWithValue("tenant", tenant.Value);
                command.Parameters.AddWithValue("id", documentId);
                var denied = await Assert.ThrowsAsync<PostgresException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
                await tx.RollbackAsync();
            }
        }

        await using (var unchanged = db.Admin.CreateCommand(
            "SELECT file_name, storage_key FROM documents WHERE tenant_id=@tenant AND id=@id"))
        {
            unchanged.Parameters.AddWithValue("tenant", tenant.Value);
            unchanged.Parameters.AddWithValue("id", documentId);
            await using var reader = await unchanged.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("original.pdf", reader.GetString(0));
            Assert.Equal(originalKey, reader.GetString(1));
        }

        var visible = await new PgDocumentCatalogRepository(db.App)
            .FindAsync(tenant, documentId);
        Assert.NotNull(visible);
        Assert.Equal("original.pdf", visible.FileName);
    }

    [Fact]
    public async Task V008_perizia_delete_is_not_granted_to_app_role_and_cannot_erase_legacy_association()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var perizie = new PgPeriziaRepository(db.App);
        var perizia = Perizia.Create(tenant, "V008-NO-DELETE-" + Guid.NewGuid().ToString("N"));
        await perizie.AddAuditedAsync(perizia, Event(tenant, perizia.Id, "perizia.create"));
        // A linked Perizia with a V006 provenance row must survive an attempt
        // to bypass the audited workflow via direct table DELETE.
        var claim = Sinistro.Create(tenant, "V008-RETAIN-CLAIM",
            new DateOnly(2026,11,18));
        await new PgSinistroRepository(db.App).AddAuditedAsync(
            claim, Event(tenant,claim.Id,"sinistro.create"));
        var links = new PgPeriziaSinistroLinkRepository(db.App);
        Assert.Equal(2, await links.LinkAuditedAsync(
            tenant,perizia.Id,claim.Id,1,Event(tenant,perizia.Id,"perizia.link-sinistro")));

        await using (var grant = db.Admin.CreateCommand(
            "SELECT NOT has_table_privilege('peritial_app','perizie','DELETE') " +
            "AND NOT has_table_privilege('peritial_recovery','perizie','DELETE') " +
            "AND has_table_privilege('peritial_app','perizie','SELECT') " +
            "AND has_table_privilege('peritial_app','perizie','UPDATE')"))
            Assert.True((bool)(await grant.ExecuteScalarAsync())!);

        await using (var conn = await db.App.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await using (var set = new NpgsqlCommand(
                "SELECT set_config('peritial.tenant_id',@tenant,true)",conn,tx))
            {
                set.Parameters.AddWithValue("tenant",tenant.Value.ToString("D"));
                await set.ExecuteNonQueryAsync();
            }
            await using var deletion = new NpgsqlCommand(
                "DELETE FROM perizie WHERE tenant_id=@tenant AND id=@id",conn,tx);
            deletion.Parameters.AddWithValue("tenant",tenant.Value);
            deletion.Parameters.AddWithValue("id",perizia.Id);
            var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                deletion.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,ex.SqlState);
        }
        Assert.NotNull(await perizie.FindAsync(tenant,perizia.Id));
        var unchanged = await links.ReadAssociationAsync(tenant,perizia.Id);
        Assert.Equal(claim.Id,unchanged.SinistroId);
        Assert.Equal(2,unchanged.LinkVersion);
    }

    [Fact]
    public async Task V007_database_guards_block_unversioned_and_unaudited_direct_link_writes()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var perizie = new PgPeriziaRepository(db.App);
        var sinistri = new PgSinistroRepository(db.App);
        var links = new PgPeriziaSinistroLinkRepository(db.App);
        var claim = Sinistro.Create(tenant, "V007-RAW-GUARD", new DateOnly(2026,11,18));
        var perizia = Perizia.Create(tenant, "V007-RAW-" + Guid.NewGuid().ToString("N"));
        await sinistri.AddAuditedAsync(claim, Event(tenant, claim.Id, "sinistro.create"));
        await perizie.AddAuditedAsync(perizia, Event(tenant, perizia.Id, "perizia.create"));

        async Task<(NpgsqlConnection Connection, NpgsqlTransaction Tx)> StartAsync()
        {
            var connection = await db.App.OpenConnectionAsync();
            var transaction = await connection.BeginTransactionAsync();
            await using var set = new NpgsqlCommand(
                "SELECT set_config('peritial.tenant_id',@tenant,true)", connection, transaction);
            set.Parameters.AddWithValue("tenant",tenant.Value.ToString("D"));
            await set.ExecuteNonQueryAsync();
            return (connection, transaction);
        }
        async Task<int> DirectUpdateAsync(NpgsqlConnection conn,
            NpgsqlTransaction transaction, bool bump)
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE perizie SET sinistro_id=@sinistro" +
                (bump ? ",sinistro_link_version=sinistro_link_version+1" : "") +
                " WHERE tenant_id=@tenant AND id=@perizia",conn,transaction);
            cmd.Parameters.AddWithValue("tenant",tenant.Value);
            cmd.Parameters.AddWithValue("perizia",perizia.Id);
            cmd.Parameters.AddWithValue("sinistro",claim.Id);
            return await cmd.ExecuteNonQueryAsync();
        }

        {
            var (conn,tx) = await StartAsync();
            await using (conn)
            await using (tx)
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() =>
                    DirectUpdateAsync(conn,tx,bump:false));
                Assert.Equal("P0001",error.SqlState);
            }
        }
        Assert.Equal(1,(await links.ReadAssociationAsync(tenant,perizia.Id)).LinkVersion);

        {
            var (conn,tx) = await StartAsync();
            await using (conn)
            await using (tx)
            {
                Assert.Equal(1,await DirectUpdateAsync(conn,tx,bump:true));
                // Deferred check must reject commit when no matching
                // audit and provenance were appended in this transaction.
                var error = await Assert.ThrowsAsync<PostgresException>(() =>
                    tx.CommitAsync());
                Assert.Equal("P0001",error.SqlState);
            }
        }
        Assert.Null((await links.ReadAssociationAsync(tenant,perizia.Id)).SinistroId);

        {
            var (conn,tx) = await StartAsync();
            await using (conn)
            await using (tx)
            {
                Assert.Equal(1,await DirectUpdateAsync(conn,tx,bump:true));
                var auditId = Guid.NewGuid();
                await using(var wrong = new NpgsqlCommand(
                    "INSERT INTO security_audit " +
                    "(tenant_id,id,subject,action,outcome,resource_id,occurred_at_utc) " +
                    "VALUES (@tenant,@audit,'app','perizia.create','succeeded',@perizia,now())",
                    conn,tx))
                {
                    wrong.Parameters.AddWithValue("tenant",tenant.Value);
                    wrong.Parameters.AddWithValue("audit",auditId);
                    wrong.Parameters.AddWithValue("perizia",perizia.Id);
                    await wrong.ExecuteNonQueryAsync();
                }
                await using(var history = new NpgsqlCommand(
                    "INSERT INTO perizia_sinistro_history " +
                    "(tenant_id,perizia_id,revision,previous_sinistro_id,new_sinistro_id,audit_id) " +
                    "VALUES (@tenant,@perizia,2,NULL,@sinistro,@audit)",conn,tx))
                {
                    history.Parameters.AddWithValue("tenant",tenant.Value);
                    history.Parameters.AddWithValue("perizia",perizia.Id);
                    history.Parameters.AddWithValue("sinistro",claim.Id);
                    history.Parameters.AddWithValue("audit",auditId);
                    await history.ExecuteNonQueryAsync();
                }
                var error = await Assert.ThrowsAsync<PostgresException>(() =>
                    tx.CommitAsync());
                Assert.Equal("P0001",error.SqlState);
            }
        }
        Assert.Equal(1,(await links.ReadAssociationAsync(tenant,perizia.Id)).LinkVersion);
        Assert.Null((await links.ReadAssociationAsync(tenant,perizia.Id)).SinistroId);
        var linked = await links.LinkAuditedAsync(tenant,perizia.Id,claim.Id,1,
            Event(tenant,perizia.Id,"perizia.link-sinistro"));
        Assert.Equal(2,linked);
        Assert.Equal(claim.Id,(await links.ReadAssociationAsync(tenant,perizia.Id)).SinistroId);
    }

    [Fact]
    public async Task V006_monotonic_link_revision_and_history_reject_ABA_and_rollback_audit()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var ps = new PgPeriziaRepository(db.App);
        var ss = new PgSinistroRepository(db.App);
        var links = new PgPeriziaSinistroLinkRepository(db.App);
        var a = Sinistro.Create(tenant, "V006-ABA-A", new DateOnly(2026,11,18));
        var b = Sinistro.Create(tenant, "V006-ABA-B", new DateOnly(2026,11,17));
        var p = Perizia.Create(tenant, "V006-ABA-" + Guid.NewGuid().ToString("N"));
        await ss.AddAuditedAsync(a, Event(tenant,a.Id,"sinistro.create"));
        await ss.AddAuditedAsync(b, Event(tenant,b.Id,"sinistro.create"));
        await ps.AddAuditedAsync(p, Event(tenant,p.Id,"perizia.create"));
        var initial = await links.ReadAssociationAsync(tenant,p.Id);
        Assert.Equal(1L, initial.LinkVersion);
        Assert.Null(initial.SinistroId);

        var rev2 = await links.LinkAuditedAsync(tenant,p.Id,a.Id,1,
            Event(tenant,p.Id,"perizia.link-sinistro"));
        var rev3 = await links.ReparentAuditedAsync(tenant,p.Id,a.Id,b.Id,rev2,
            Event(tenant,p.Id,"perizia.reparent-sinistro"));
        var rev4 = await links.ReparentAuditedAsync(tenant,p.Id,b.Id,a.Id,rev3,
            Event(tenant,p.Id,"perizia.reparent-sinistro"));
        Assert.Equal(new[] {2L,3L,4L}, new[] {rev2,rev3,rev4});
        Assert.Equal(a.Id, (await links.ReadAssociationAsync(tenant,p.Id)).SinistroId);
        await Assert.ThrowsAsync<PeriziaConcurrencyConflictException>(() =>
            links.UnlinkAuditedAsync(tenant,p.Id,a.Id,rev2,
                Event(tenant,p.Id,"perizia.unlink-sinistro")));
        await Assert.ThrowsAsync<PeriziaConcurrencyConflictException>(() =>
            links.ReparentAuditedAsync(tenant,p.Id,a.Id,b.Id,rev2,
                Event(tenant,p.Id,"perizia.reparent-sinistro")));
        var duplicate = Event(tenant,p.Id,"perizia.unlink-sinistro");
        await new PgSecurityAuditTrail(db.App).AppendAsync(duplicate);
        await Assert.ThrowsAsync<PostgresException>(() =>
            links.UnlinkAuditedAsync(tenant,p.Id,a.Id,rev4,duplicate));
        Assert.Equal(rev4,(await links.ReadAssociationAsync(tenant,p.Id)).LinkVersion);
        var rev5 = await links.UnlinkAuditedAsync(tenant,p.Id,a.Id,rev4,
            Event(tenant,p.Id,"perizia.unlink-sinistro"));
        Assert.Equal(5,rev5);
        var rev6 = await links.LinkAuditedAsync(tenant,p.Id,b.Id,rev5,
            Event(tenant,p.Id,"perizia.link-sinistro"));
        Assert.Equal(6,rev6);
        // Two independent signed writers started from the SAME revision:
        // the PG row lock + versioned CAS admits exactly one success.
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try
            {
                return await links.ReparentAuditedAsync(tenant, p.Id, b.Id, a.Id, rev6,
                    Event(tenant,p.Id,"perizia.reparent-sinistro"));
            }
            catch (PeriziaConcurrencyConflictException)
            {
                return -1L;
            }
        }));
        Assert.Single(attempts.Where(v => v == 7L));
        Assert.Single(attempts.Where(v => v == -1L));
        Assert.Equal(a.Id, (await links.ReadAssociationAsync(tenant,p.Id)).SinistroId);
        Assert.Equal(7, (await links.ReadAssociationAsync(tenant,p.Id)).LinkVersion);

        // App and recovery roles have no history SELECT/UPDATE/DELETE:
        // provenance is append-only and available only to privileged auditors.
        await using (var privileges = db.Admin.CreateCommand(
            "SELECT has_table_privilege('peritial_app','perizia_sinistro_history','INSERT') " +
            "AND NOT has_table_privilege('peritial_app','perizia_sinistro_history','SELECT') " +
            "AND NOT has_table_privilege('peritial_app','perizia_sinistro_history','UPDATE') " +
            "AND NOT has_table_privilege('peritial_app','perizia_sinistro_history','DELETE') " +
            "AND NOT has_table_privilege('peritial_recovery','perizia_sinistro_history','SELECT')"))
            Assert.True((bool)(await privileges.ExecuteScalarAsync())!);
        await using var cmd = db.Admin.CreateCommand(
            "SELECT revision, previous_sinistro_id, new_sinistro_id " +
            "FROM perizia_sinistro_history WHERE tenant_id=@tenant AND perizia_id=@p ORDER BY revision");
        cmd.Parameters.AddWithValue("tenant",tenant.Value);
        cmd.Parameters.AddWithValue("p",p.Id);
        await using var reader = await cmd.ExecuteReaderAsync();
        var history = new List<(long,Guid?,Guid?)>();
        while (await reader.ReadAsync())
            history.Add((reader.GetInt64(0),reader.IsDBNull(1)?null:reader.GetGuid(1),
                reader.IsDBNull(2)?null:reader.GetGuid(2)));
        Assert.Equal(new (long,Guid?,Guid?)[] {
            (2,null,a.Id),(3,a.Id,b.Id),(4,b.Id,a.Id),(5,a.Id,null),(6,null,b.Id),
            (7,b.Id,a.Id)
        },history);
    }

    [Fact]
    public async Task V004_association_read_returns_unlinked_or_linked_and_hides_foreign_perizia_as_missing()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var a = TenantId.From(Guid.NewGuid());
        var b = TenantId.From(Guid.NewGuid());
        var perizie = new PgPeriziaRepository(db.App);
        var links = new PgPeriziaSinistroLinkRepository(db.App);
        var owned = Perizia.Create(a, "LINK-READ-" + Guid.NewGuid().ToString("N"));
        var foreign = Perizia.Create(b, "LINK-FOREIGN-" + Guid.NewGuid().ToString("N"));
        await perizie.AddAuditedAsync(owned, Event(a, owned.Id, "perizia.create"));
        await perizie.AddAuditedAsync(foreign, Event(b, foreign.Id, "perizia.create"));
        Assert.Null(await links.FindSinistroIdForPeriziaAsync(a, owned.Id));
        Assert.Null(await links.FindSinistroIdForPeriziaAsync(b, foreign.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            links.FindSinistroIdForPeriziaAsync(a, foreign.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            links.FindSinistroIdForPeriziaAsync(a, Guid.NewGuid()));

        var sinistro = Sinistro.Create(a, "LINK-READ-SINISTRO", new DateOnly(2026, 11, 16));
        await new PgSinistroRepository(db.App).AddAuditedAsync(
            sinistro, Event(a, sinistro.Id, "sinistro.create"));
        await links.LinkAuditedAsync(a, owned.Id, sinistro.Id, 1,
            Event(a, owned.Id, "perizia.link-sinistro"));
        Assert.Equal(sinistro.Id, await links.FindSinistroIdForPeriziaAsync(a, owned.Id));
        Assert.Equal(1L, await CountLinkAuditAsync(db.Admin, a, owned.Id));
    }

    [Fact]
    public async Task V004_failed_audit_rolls_back_link_and_does_not_generate_success()
    {
        await using var db = await IsolatedDatabases.CreateAsync();
        var tenant = TenantId.From(Guid.NewGuid());
        var sinistro = Sinistro.Create(tenant, "SIN-ROLLBACK", new DateOnly(2026, 10, 9));
        await new PgSinistroRepository(db.App).AddAuditedAsync(
            sinistro, Event(tenant, sinistro.Id, "sinistro.create"));
        var perizia = Perizia.Create(tenant, "LINK-ROLLBACK-" + Guid.NewGuid().ToString("N"));
        await new PgPeriziaRepository(db.App).AddAuditedAsync(
            perizia, Event(tenant, perizia.Id, "perizia.create"));
        var repository = new PgPeriziaSinistroLinkRepository(db.App);
        var repeated = Event(tenant, perizia.Id, "perizia.link-sinistro");
        await new PgSecurityAuditTrail(db.App).AppendAsync(repeated);

        var conflict = await Assert.ThrowsAsync<PostgresException>(() =>
            repository.LinkAuditedAsync(tenant, perizia.Id, sinistro.Id, 1, repeated));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, conflict.SqlState);
        Assert.Empty(await repository.ListPeriziaIdsAsync(tenant, sinistro.Id));
        Assert.Equal(1L, await CountLinkAuditAsync(db.Admin, tenant, perizia.Id));

        await repository.LinkAuditedAsync(tenant, perizia.Id, sinistro.Id, 1,
            Event(tenant, perizia.Id, "perizia.link-sinistro"));
        Assert.Equal(new[] { perizia.Id }, await repository.ListPeriziaIdsAsync(tenant, sinistro.Id));
        Assert.Equal(2L, await CountLinkAuditAsync(db.Admin, tenant, perizia.Id));
    }

    private static async Task<long> CountLinkAuditAsync(NpgsqlDataSource db, TenantId tenant, Guid id)
    {
        await using var cmd = db.CreateCommand(
            "SELECT count(*) FROM security_audit WHERE tenant_id=@tenant AND resource_id=@id " +
            "AND action='perizia.link-sinistro' AND outcome='succeeded'");
        cmd.Parameters.AddWithValue("tenant", tenant.Value);
        cmd.Parameters.AddWithValue("id", id);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static SecurityAuditEvent Event(TenantId tenant, Guid id, string action) =>
        new(Guid.NewGuid(), tenant, "signed-subject", action, "succeeded", id, DateTime.UtcNow);

    private sealed class IsolatedDatabases(NpgsqlDataSource admin, NpgsqlDataSource app,
        NpgsqlDataSource root, string schema) : IAsyncDisposable
    {
        public NpgsqlDataSource Admin { get; } = admin;
        public NpgsqlDataSource App { get; } = app;

        public static async Task<IsolatedDatabases> CreateAsync()
        {
            var cs = Environment.GetEnvironmentVariable("PERITIAL_TEST_POSTGRES")
                ?? throw new InvalidOperationException("PERITIAL_TEST_POSTGRES required.");
            var schema = "sinistro_test_" + Guid.NewGuid().ToString("N");
            var root = NpgsqlDataSource.Create(cs);
            NpgsqlDataSource? admin = null;
            NpgsqlDataSource? app = null;
            try
            {
                await using (var conn = await root.OpenConnectionAsync())
                await using (var command = new NpgsqlCommand("CREATE SCHEMA " + schema, conn))
                    await command.ExecuteNonQueryAsync();
                admin = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(cs)
                { SearchPath = schema }.ConnectionString);
                Assert.Equal(12, await SchemaMigrator.ApplyAsync(admin));
                await TestDbRoleProvisioner.ConfigureAsync(admin);
                await using (var conn = await root.OpenConnectionAsync())
                await using (var grant = new NpgsqlCommand(
                    "GRANT USAGE ON SCHEMA " + schema + " TO peritial_app", conn))
                    await grant.ExecuteNonQueryAsync();
                app = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(cs)
                {
                    SearchPath = schema, Username = "peritial_app",
                    Password = "local-only-app", MaxPoolSize = 1
                }.ConnectionString);
                return new IsolatedDatabases(admin, app, root, schema);
            }
            catch
            {
                if (app is not null) await app.DisposeAsync();
                if (admin is not null) await admin.DisposeAsync();
                await using (var conn = await root.OpenConnectionAsync())
                await using (var drop = new NpgsqlCommand(
                    "DROP SCHEMA IF EXISTS " + schema + " CASCADE", conn))
                    await drop.ExecuteNonQueryAsync();
                await root.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Admin.DisposeAsync();
            await using (var conn = await root.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand(
                "DROP SCHEMA IF EXISTS " + schema + " CASCADE", conn))
                await command.ExecuteNonQueryAsync();
            await root.DisposeAsync();
        }
    }
}
