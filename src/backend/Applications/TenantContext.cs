using System.Security.Claims;
using Peritial.Business;
using Peritial.Domains;

namespace Peritial.Applications;

public sealed class TenantContext(IHttpContextAccessor httpContextAccessor) : ITenantContext
{
    private ClaimsPrincipal User => httpContextAccessor.HttpContext?.User
        ?? throw new UnauthorizedAccessException("Missing caller identity.");

    public TenantId TenantId
    {
        get
        {
            var claim = User.FindFirst("tenant_id")?.Value;
            if (!Guid.TryParse(claim, out var id) || id == Guid.Empty)
                throw new UnauthorizedAccessException("A signed tenant_id claim is required.");
            return TenantId.From(id);
        }
    }

    public string Subject => User.FindFirst("sub")?.Value
        ?? throw new UnauthorizedAccessException("A signed subject is required.");

    public bool HasScope(string scope)
    {
        return OAuthSecurity.HasScope(User, scope);
    }
}
