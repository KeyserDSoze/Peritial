using Expertiser.Base.Constant;
using Expertiser.Database.Models;
using Expertiser.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Rystem.Web;
using Rystem.Web.Backoffice;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
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
        private static readonly NavigationManager navigationManager = new NavigationManager();
        public NavigationManager NavigationManager => navigationManager;

        private static NavigationBuilder navigationBar = new NavigationBar("Experty", "fa-institution")
            .AddSuperGroup()
                .AddGroup("Home", "fa-business-time", new NavigationUrl())
            .AddSuperGroup("Manage Experty", null, null, Roles.AdminContributor)
                .AddGroup("Expertises", "fa-cog")
                    .AddItem("Accidents", null, new NavigationUrl("Accident"))
                    .AddItem("Expertises", null, new NavigationUrl("Experty"))
            .AddSuperGroup("Manage", null, null, Roles.Admin)
                .AddGroup("Manage Members", "fa-user-check")
                    .AddItem("Members", null, new NavigationUrl("User"))
                .AddGroup("Companies", "fa-memory", null)
                    .AddItem("Inspectorates", null, new NavigationUrl("Inspectorate"))
                    .AddItem("Insurance Agencies", null, new NavigationUrl("InsuranceAgency"))
                    .AddItem("Insurance Companies", null, new NavigationUrl("InsuranceCompany"))
            .AddSuperGroup("Customer and Utility", null, null, Roles.AdminContributor)
                .AddGroup("Customers", "fa-user")
                    .AddItem("Insurances", null, new NavigationUrl("Insurance"))
                    .AddItem("Customers", null, new NavigationUrl("Customer"))
                .AddGroup("Companies", "fa-tools")
                    .AddItem("Countries", null, new NavigationUrl("Country"))
                    .AddItem("Locations", null, new NavigationUrl("Location"))
            .Build();
        public NavigationBuilder NavigationBar => navigationBar;
    }
    public class FileManager
    {

    }
    public class NavigationManager
    {
        public INavigation<TableAccident> GetAccidentNavigation(string title, IStringLocalizer localizer)
           => INavigation<TableAccident>
               .Create(NavigationOptions.CanDoneAll(title, localizer, "Edit"))
               .Include(x => x.Id, PropertyOptions.Key)
               .Include(x => x.ExternalId)
               .Include(x => x.AccidentDate, new PropertyOptions() { Format = "dd-MM-yyyy" })
               .Include(x => x.DenunciationDate, new PropertyOptions() { Format = "dd-MM-yyyy" })
               .Include(x => x.Inspectorate)
                   .ThenInclude(x => x.Label)
               .Include(x => x.Insurance)
                   .ThenInclude(x => x.Customer)
                       .ThenInclude(x => x.Name)
               .Include(x => x.InsuranceAgency)
                   .ThenInclude(x => x.Label)
               .Include(x => x.Address)
                   .ThenInclude(x => x.Address)
               .Include(x => x.TableExpertise, new PropertyOptions(OutputType.Count));
        public INavigation<TableCustomer> GetCustomerNavigation(string title, IStringLocalizer localizer)
            => INavigation<TableCustomer>.Create(NavigationOptions.CanDoneAll(title, localizer, "Edit"))
                .Include(x => x.Id, PropertyOptions.Key)
                .Include(x => x.Name)
                .Include(x => x.FiscalCode)
                .Include(x => x.TableInsurance, PropertyOptions.Count)
                .Include(x => x.Address)
                    .ThenInclude(x => x.Address);
        public INavigation<TableExpertise> GetExpertiseNavigation(string title, IStringLocalizer localizer, ClaimsPrincipal user)
            => INavigation<TableExpertise>
                .Create(NavigationOptions.CanDone(user.IsInRole(Roles.AdminContributor), true, user.IsInRole(Roles.Admin),
                            title, localizer, "Edit"))
                .Include(x => x.Id, PropertyOptions.Key)
                .Include(x => x.Branch)
                    .ThenInclude(x => x.BranchType)
                        .ThenInclude(x => x.Label)
                .Include(x => x.Contributor)
                    .ThenInclude(x => x.UserName)
                .Include(x => x.Status)
                .Include(x => x.Member)
                    .ThenInclude(x => x.UserName)
                .Include(x => x.TableBill)
                    .ThenInclude(x => x.Amount)
                .Include(x => x.TableMessage, PropertyOptions.Count)
                .Include(x => x.Liquidated)
                .Include(x => x.AssignmentDate)
                .Include(x => x.InspectionDate);
        public INavigation<TableInspectorate> GetInspectorateNavigation(string title, IStringLocalizer localizer)
            => INavigation<TableInspectorate>
                .Create(NavigationOptions.CanDoneAll(title, localizer, "Edit"))
               .Include(x => x.Label)
               .Include(x => x.InsuranceCompany)
                    .ThenInclude(x => x.Label)
               .Include(x => x.IsActive)
               .Include(x => x.TableAccident, PropertyOptions.Count)
               .Include(x => x.Address)
                    .ThenInclude(x => x.City)
               .Include(x => x.Id, PropertyOptions.Key);
        public INavigation<TableInsuranceAgency> GetInsuranceAgencyNavigation(string title, IStringLocalizer localizer)
           => INavigation<TableInsuranceAgency>
               .Create(NavigationOptions.CanDoneAll(title, localizer, "Edit"))
               .Include(x => x.Id, PropertyOptions.Key)
               .Include(x => x.Label)
               .Include(x => x.IsActive)
               .Include(x => x.TableAccident, PropertyOptions.Count);
        public INavigation<TableInsuranceCompany> GetInsuranceCompanyNavigation(string title, IStringLocalizer localizer)
           => INavigation<TableInsuranceCompany>
               .Create(NavigationOptions.CanDoneAll(title, localizer, "Edit"))
               .Include(x => x.Id, PropertyOptions.Key)
               .Include(x => x.Label)
               .Include(x => x.IsActive)
               .Include(x => x.TableInsuranceAgency, PropertyOptions.Count)
               .Include(x => x.TableInspectorate, PropertyOptions.Count);
        public INavigation<TableInsurance> GetInsuranceNavigation(string title, IStringLocalizer localizer)
            => INavigation<TableInsurance>
                .Create(NavigationOptions.CanDoneAll(title, localizer, "Edit"))
                .Include(x => x.Id, PropertyOptions.Key)
                .Include(x => x.Customer)
                    .ThenInclude(x => x.Name)
                .Include(x => x.InsuranceCompany)
                    .ThenInclude(x => x.Label)
                .Include(x => x.TableAccident, PropertyOptions.Count);
        public INavigation<TableLocation> GetLocationNavigation(string title, IStringLocalizer localizer)
          => INavigation<TableLocation>
              .Create(NavigationOptions.CanDoneAll(title, localizer, "Edit"))
              .Include(x => x.Address)
              .Include(x => x.City)
                  .ThenInclude(x => x.Name)
              .Include(x => x.PostalCode)
              .Include(x => x.Id, PropertyOptions.Key);
        public INavigation<AspNetUsers> GetUserNavigation(string title, IStringLocalizer localizer)
            => INavigation<AspNetUsers>
                .Create(NavigationOptions.CanDoneAll(title, localizer, "Edit"))
                .Include(x => x.Id, PropertyOptions.Key)
                .Include(x => x.UserName)
                .Include(x => x.EmailConfirmed)
                .Include(x => x.AspNetUserRoles)
                    .ThenInclude(x => x.Role)
                        .ThenInclude(x => x.Name);
    }
    public class QueryManager
    {
        private readonly Manager Manager;
        public QueryManager(Manager manager)
            => this.Manager = manager;
        public IQueryable<TableAccident> Accidents => Manager.Context.TableAccident
            .Include(t => t.Inspectorate)
            .Include(t => t.Insurance)
                .ThenInclude(t => t.Customer)
            .Include(t => t.InsuranceAgency)
            .Include(t => t.Address)
            .Include(t => t.TableExpertise)
                .ThenInclude(t => t.Contributor);
        public IQueryable<TableExpertise> Expertises => Manager.Context.TableExpertise
            .Include(x => x.TableBill)
            .Include(x => x.TableMessage)
                .ThenInclude(x => x.User)
            .Include(x => x.Branch)
                .ThenInclude(x => x.BranchType)
            .Include(x => x.Contributor)
            .Include(x => x.Member);
        public IQueryable<TableLocation> Locations => Manager.Context.TableLocation
            .Include(x => x.City);
        public IQueryable<TableInsuranceAgency> InsuranceAgencies => Manager.Context.TableInsuranceAgency
                    .Include(x => x.InsuranceCompany)
                    .Include(x => x.Address);
        public IQueryable<TableInspectorate> Inspectorates => Manager.Context.TableInspectorate
                    .Include(x => x.InsuranceCompany)
                    .Include(x => x.Address);
        public IQueryable<TableInsuranceCompany> InsuranceCompanies => Manager.Context.TableInsuranceCompany
                    .Include(x => x.Address);
        public IQueryable<TableInsurance> Insurances => Manager.Context.TableInsurance
                    .Include(x => x.InsuranceCompany)
                    .Include(x => x.TableAccident)
                    .Include(x => x.Customer);
        public IQueryable<TableCustomer> Customers => Manager.Context.TableCustomer
                    .Include(x => x.Address)
                        .Include(x => x.TableInsurance);
        public IQueryable<AspNetUsers> Users => Manager.Context.AspNetUsers
            .Include(x => x.AspNetUserRoles)
                .ThenInclude(x => x.Role);
    }
    public class DropdownManager
    {
        private readonly Manager Manager;
        public DropdownManager(Manager manager)
            => this.Manager = manager;
#warning da mettere la parte di controllo che se quel tizio ha quella insurance company, può avere solo cose inerenti a quella compagnia o strettamente legate
        public IEnumerable<DropdownItem> GetInspectorates(int insuranceId = 0)
        {
            return this.Manager.QueryManager.Inspectorates
                .Select(x => new DropdownItem()
                {
                    Label = x.Label,
                    Value = x.Id.ToString(),
                    Group = x.InsuranceCompany != null ? x.InsuranceCompany.Label : string.Empty
                });
        }
        public IEnumerable<DropdownItem> GetInsuranceAgencies(int insuranceId = 0)
        {
            //TableInsurance insurance = await this.Manager.QueryManager.Insurances.FirstOrDefaultAsync(x => x.Id == insuranceId);
            return this.Manager.QueryManager.InsuranceAgencies
                .Select(x => new DropdownItem()
                {
                    Label = x.Label,
                    Value = x.Id.ToString(),
                    Group = x.InsuranceCompany != null ? x.InsuranceCompany.Label : string.Empty
                });
        }
        public IEnumerable<DropdownItem> GetInsuranceCompanies(int insuranceCompanyId = 0)
        {
            return this.Manager.QueryManager.InsuranceCompanies
                .Select(x => new DropdownItem()
                {
                    Label = x.Label,
                    Value = x.Id.ToString(),
                    Group = x.AddressId != null ? x.Address.City.Name : string.Empty,
                    Selected = x.Id == insuranceCompanyId
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
        public IEnumerable<DropdownItem> GetMembers(string id)
        {
            return this.Manager.Context.AspNetUsers
                .Select(x => new DropdownItem()
                {
                    Label = x.UserName,
                    Value = x.Id,
                    Selected = x.Id == id
                });
        }
        public IEnumerable<DropdownItem> GetContributors(string id)
        {
            return this.Manager.Context.AspNetUsers
                .Select(x => new DropdownItem()
                {
                    Label = x.UserName,
                    Value = x.Id,
                    Selected = x.Id == id
                });
        }
        public IEnumerable<DropdownItem> GetBranches(int branchId)
        {
            return this.Manager.Context.TableBranch
                .Include(x => x.BranchType)
                .Select(x => new DropdownItem()
                {
                    Label = $"{x.Label} - {x.BranchType.Label}",
                    Value = x.Id.ToString(),
                    Selected = x.Id == branchId
                });
        }
        public IEnumerable<DropdownItem> GetStatus(int statusId, IStringLocalizer stringLocalizer)
        {
            return Status()
                .Select(x => new DropdownItem()
                {
                    Label = stringLocalizer[$"{x}"],
                    Value = ((int)x).ToString(),
                    Selected = (int)x == statusId
                });
            IEnumerable<StatusType> Status()
            {
                yield return StatusType.Presented;
                yield return StatusType.Registered;
                yield return StatusType.Duplicated;
                yield return StatusType.Cancelled;
                yield return StatusType.Billed;
            }
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
        public IEnumerable<DropdownItem> GetCustomers(int customerId)
        {
            return Manager.QueryManager.Customers.Select(
                   customer =>
               new DropdownItem()
               {
                   Label = customer.Name,
                   Value = customer.Id.ToString(),
                   Selected = customer.Id == customerId,
                   Group = customer.Name[0].ToString()
               });
        }
        public IEnumerable<DropdownItem> GetInsurances(int insuranceId)
        {
            return this.Manager.QueryManager.Insurances
                .Select(x => new DropdownItem()
                {
                    Value = x.Id.ToString(),
                    Group = x.InsuranceCompany.Label,
                    Label = $"{x.Customer.Name} - {x.ExternalId}",
                    Selected = x.Id == insuranceId
                });
        }
    }
    public class ModalManager
    {
        private readonly Manager Manager;
        public ModalManager(Manager manager)
            => this.Manager = manager;
        public RequestContext CreateNewLocation => new RequestContext() { Action = "Create", Controller = "Location" };
        public RequestContext CreateNewCustomer => new RequestContext() { Action = "Create", Controller = "Customer" };
        public RequestContext CreateNewInsuranceCompany => new RequestContext() { Action = "Create", Controller = "InsuranceCompany" };
        public RequestContext UpdatePostalCodes => new RequestContext() { Action = "UpdatePostalCodes", Controller = "Location", Selector = "#updatePostalCodes" };
        public RequestContext SetLocation => new RequestContext() { Action = "Create", Controller = "Location", RequestType = RequestType.Post, Selector = ".location-address" };
        public RequestContext SetCustomer => new RequestContext() { Action = "Create", Controller = "Customer", RequestType = RequestType.Post, Selector = "#customers" };
        public RequestContext SetInsurance => new RequestContext() { Action = "Create", Controller = "Insurance", RequestType = RequestType.Post, Selector = "#insurances" };
        public RequestContext CreateNewInsurance => new RequestContext() { Action = "Create", Controller = "Insurance" };
        public RequestContext UpdateUserRole(string userId) => new RequestContext() { Action = "UpdateRole", Controller = "User", QueryString = new Dictionary<object, object>() { { "id", userId } } };
    }
}
