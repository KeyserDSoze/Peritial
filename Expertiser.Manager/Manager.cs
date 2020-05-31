using Expertiser.Database.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Rystem.Web;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace Expertiser
{
    public class Manager
    {
        private ContextExpertiser context;
        public ContextExpertiser Context => context ??= new ContextExpertiser(this.ConnectionString);
        public IHttpContextAccessor HttpContextAccessor { get; }
        public UserManager<IdentityUser> UserManager { get; }
        public RoleManager<IdentityRole> RoleManager { get; }
        public SignInManager<IdentityUser> SignInManager { get; }
        public Manager(IHttpContextAccessor httpContextAccessor, SignInManager<IdentityUser> signInManager, UserManager<IdentityUser> userManager, RoleManager<IdentityRole> roleManager)
        {
            this.HttpContextAccessor = httpContextAccessor;
            this.UserManager = userManager;
            this.RoleManager = roleManager;
            this.SignInManager = signInManager;
        }

        private string ConnectionString => Const.DatabaseConnectionString;
        private QueryManager queryManager;
        public QueryManager QueryManager => queryManager ??= new QueryManager(this);
        private ModalManager modalManager;
        public ModalManager ModalManager => modalManager ??= new ModalManager(this);
        private DropdownManager dropdownManager;
        public DropdownManager DropdownManager => dropdownManager ??= new DropdownManager(this);
    }

    public class QueryManager
    {
        private readonly Manager Manager;
        public QueryManager(Manager manager)
            => this.Manager = manager;
        public IQueryable<TableAccident> Accidents => Manager.Context.TableAccident.Include(t => t.Inspectorate).Include(t => t.Insurance).Include(t => t.InsuranceAgency).Include(t => t.Place);
        public IQueryable<TableLocation> Locations => Manager.Context.TableLocation.Include(x => x.City);
        public IQueryable<TableInsuranceAgency> InsuranceAgencies => Manager.Context.TableInsuranceAgency
                    .Include(x => x.InsuranceCompany)
                    .Include(x => x.Address);
        public IQueryable<TableInspectorate> Inspectorates => Manager.Context.TableInspectorate
                    .Include(x => x.InsuranceCompany)
                    .Include(x => x.Address);
    }
    public class DropdownManager
    {
        private readonly Manager Manager;
        public DropdownManager(Manager manager)
            => this.Manager = manager;
        public IEnumerable<DropdownItem> GetInspectorates()
        {
            return this.Manager.QueryManager.Inspectorates
                .Select(x => new DropdownItem()
                {
                    Label = x.Label,
                    Value = x.Id.ToString(),
                    Group = x.AddressId != null ? x.Address.City.Name : string.Empty
                });
        }
        public IEnumerable<DropdownItem> GetInsuranceAgencies()
        {
            return this.Manager.QueryManager.InsuranceAgencies
                .Select(x => new DropdownItem()
                {
                    Label = x.Label,
                    Value = x.Id.ToString(),
                    Group = x.AddressId != null ? x.Address.City.Name : string.Empty
                });
        }
        public IEnumerable<DropdownItem> GetLocations(int locationId = 0)
        {
            return this.Manager.QueryManager.Locations
                .Where(x => !string.IsNullOrWhiteSpace(x.Address))
                .Select(x => new DropdownItem()
                {
                    Value = x.Id.ToString(),
                    Group = x.City.Name,
                    Label = x.Address,
                    Selected = x.Id == locationId
                });
        }
        public IEnumerable<DropdownItem> GetCities(int selectedCity = 0)
        {
            return this.Manager.Context.TableCity
                .Select(x => new DropdownItem()
                {
                    Label = x.Name,
                    Value = x.Id.ToString(),
                    Selected = x.Id == selectedCity
                });
        }
        public IEnumerable<DropdownItem> GetPostalCodes(int cityId, string selectedPostalCode = null)
        {
            TableCity city = this.Manager.Context.TableCity.Where(x => x.Id == cityId).FirstOrDefault();
            return city.PostalCodes.Select(
                   postalCode =>
               new DropdownItem()
               {
                   Label = postalCode,
                   Value = postalCode,
                   Selected = selectedPostalCode == postalCode
               });
        }
        public async Task<IEnumerable<DropdownItem>> GetRoles(string userId)
        {
            IdentityUser user = await Manager.UserManager.Users.FirstOrDefaultAsync(x => x.Id == userId);
            return this.Manager.RoleManager.Roles.Select(
                   role =>
               new DropdownItem()
               {
                   Label = role.Name,
                   Value = role.Id,
                   Selected = Manager.UserManager.IsInRoleAsync(user, role.Name).ToResult()
               });
        }
    }
    public class ModalManager
    {
        private readonly Manager Manager;
        public ModalManager(Manager manager)
            => this.Manager = manager;
        public RequestContext CreateNewLocation => new RequestContext() { Action = "Create", Controller = "Location" };
        public RequestContext UpdatePostalCodes => new RequestContext() { Action = "GetPostalCodes", Controller = "Location", Selector = "#updatePostalCodes", QueryString = new Dictionary<object, object>() { { "cityId", 1 } } };
        public RequestContext GetLocations => new RequestContext() { Action = "Create", Controller = "Location", RequestType = RequestType.Post, Selector = "#accidentLocation" };
        public RequestContext CreateNewInsurance => new RequestContext() { Action = "PartialCreate", Controller = "Insurance" };
        public RequestContext UpdateUserRole(string userId) => new RequestContext() { Action = "UpdateRole", Controller = "User", QueryString = new Dictionary<object, object>() { { "id", userId } } };
    }
}
