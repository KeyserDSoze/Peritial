using Expertiser.Base.Constant;
using System.Security.Claims;

namespace Expertiser
{
    public class UserHelperManager
    {
        private readonly Manager Manager;
        public UserHelperManager(Manager manager)
            => this.Manager = manager;
        public bool IsAdmin => Manager.HttpContextAccessor.HttpContext.User.IsInRole(Roles.Admin);
        public bool IsManager => Manager.HttpContextAccessor.HttpContext.User.IsInRole(Roles.Admin) || Manager.HttpContextAccessor.HttpContext.User.IsInRole(Roles.Contributor);
        public bool IsReader => Manager.HttpContextAccessor.HttpContext.User.IsInRole(Roles.Reader);
        public string Id => Manager.HttpContextAccessor.HttpContext.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        public bool IsSuperAdmin => Manager.HttpContextAccessor.HttpContext.User.Identity.Name == "alessandro.rapiti44@gmail.com";
    }
}