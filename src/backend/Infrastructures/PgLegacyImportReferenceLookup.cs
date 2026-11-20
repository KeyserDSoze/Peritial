using Npgsql;
using Peritial.Business;
using Peritial.Domains;

namespace Peritial.Infrastructures;

/// <summary>
/// Minimal case facts for a dry-run XML import; exact reference only.
/// Reuses V003's (tenant_id, external_reference) index and FORCE RLS.
/// No SELECT *, no actor/PII or mutation-success audits.
/// </summary>
public sealed class PgLegacyImportReferenceLookup(NpgsqlDataSource dataSource)
    : ILegacyImportReferenceLookup
{
    public async Task<IReadOnlyList<LegacySinistroMatch>> FindExactAsync(
        TenantId tenant, string reference, int limit,
        CancellationToken ct = default)
    {
        if (limit != 2)
            throw new ArgumentOutOfRangeException(nameof(limit),
                "Reconciliation requires exactly two lookahead records.");
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 100 ||
            !string.Equals(reference, reference.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Invalid legacy reference.", nameof(reference));

        await using var scope = await TenantDbScope.OpenAsync(dataSource, tenant, ct);
        var matches = new List<LegacySinistroMatch>(2);
        await using (var cmd = scope.Command(
            "SELECT id, occurred_on, reported_on FROM sinistri " +
            "WHERE tenant_id=@tenant AND external_reference=@reference " +
            "ORDER BY id LIMIT 2"))
        {
            cmd.Parameters.AddWithValue("tenant", tenant.Value);
            cmd.Parameters.AddWithValue("reference", reference);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                matches.Add(new LegacySinistroMatch(
                    reader.GetGuid(0), reader.GetFieldValue<DateOnly>(1),
                    reader.IsDBNull(2) ? null : reader.GetFieldValue<DateOnly>(2)));
        }
        await scope.CommitAsync(ct);
        return matches;
    }
}
