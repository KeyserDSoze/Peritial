using Peritial.Domains;

namespace Peritial.Business;

// Explicit allowlist of fields: never store reference, assignee, file name,
// content, bearer token, HTTP headers, or raw MCP tool arguments.
public sealed record SecurityAuditEvent(
    Guid Id, TenantId TenantId, string Subject, string Action, string Outcome,
    Guid? ResourceId, DateTime OccurredAtUtc);

public interface ISecurityAuditTrail
{
    Task AppendAsync(SecurityAuditEvent entry, CancellationToken ct = default);
}
