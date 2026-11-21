namespace Peritial.Domains;

/// <summary>
/// Timestamp policy for NEW PostgreSQL-backed aggregates only. PostgreSQL
/// timestamptz stores microseconds, whereas DateTime.UtcNow has 100-ns ticks.
/// Truncating before the first insert makes in-memory create responses (REST
/// and MCP) byte-for-byte equal to later reads. Never apply this to legacy
/// restoration, accident occurred/reported dates or audited historical data.
/// </summary>
public static class PersistedUtcTimestamp
{
    private const long TicksPerMicrosecond = 10;

    public static DateTime ForNewRecord(DateTime timestampUtc)
    {
        if (timestampUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("A UTC timestamp is required.", nameof(timestampUtc));
        long ticks = timestampUtc.Ticks;
        return new DateTime(ticks - ticks % TicksPerMicrosecond, DateTimeKind.Utc);
    }
}
