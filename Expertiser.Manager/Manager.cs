using Expertiser.Base.Constant;
using Expertiser.Database.Models;
using Expertiser.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Rystem.Data;
using Rystem.Web;
using Rystem.Web.Backoffice;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Expertiser
{
    public class Manager
    {
        public string Version => "1.0.0";
        private ContextExpertiser context;
        public ContextExpertiser Context => context ??= new ContextExpertiser(this.ConnectionString);
        public IHttpContextAccessor HttpContextAccessor { get; }
        public string BaseUri => $"{HttpContextAccessor.HttpContext.Request.Scheme}://{HttpContextAccessor.HttpContext.Request.Host}";
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
        public string Title { get; } = "Quantum";
        private string ConnectionString => Const.DatabaseConnectionString;
        private QueryManager queryManager;
        public QueryManager QueryManager => queryManager ??= new QueryManager(this);
        private HttpHelperManager httpHelperManager;
        public HttpHelperManager HttpHelperManager => httpHelperManager ??= new HttpHelperManager(this);
        private ChartManager chartManager;
        public ChartManager ChartManager => chartManager ??= new ChartManager(this);
        private ImportManager importManager;
        public ImportManager ImportManager => importManager ??= new ImportManager(this);
        private AjaxRequestManager ajaxRequestManager;
        public AjaxRequestManager AjaxRequestManager => ajaxRequestManager ??= new AjaxRequestManager(this);
        public async Task<ExpertyState> GetExpertyStateAsync(int expertiseId, IStringLocalizer stringLocalizer)
            => await StateFactory.CreateAsync(expertiseId, this, stringLocalizer).NoContext();
        private DropdownManager dropdownManager;
        public DropdownManager DropdownManager => dropdownManager ??= new DropdownManager(this);
        private FileManager fileManager;
        public FileManager FileManager => fileManager ??= new FileManager(this);
        private static readonly NavigationManager navigationManager = new NavigationManager();
        public NavigationManager NavigationManager => navigationManager;
        private static readonly ServiceManager serviceManager = new ServiceManager();
        public ServiceManager ServiceManager => serviceManager;
        private UserHelperManager userHelperManager;
        public UserHelperManager UserHelperManager => userHelperManager ??= new UserHelperManager(this);
        private static DefaultUI defaultUI = new DefaultUI()
        {
            Primary = "rgba(165,16,16,1)",
            Secondary = "rgba(185,10,10,1)",
            Danger = DefaultUI.Default.Danger,
            Dark = DefaultUI.Default.Dark,
            Info = DefaultUI.Default.Info,
            Light = DefaultUI.Default.Light,
            SelectedPrimary = "#CD5C24",
            SelectedSecondary = "#CD1C24",
            Success = DefaultUI.Default.Success,
            Warning = DefaultUI.Default.Warning,
            White = DefaultUI.Default.White
        };
        public DefaultUI DefaultUI => defaultUI;
        private static NavigationBuilder navigationBar;
        public NavigationBuilder NavigationBar => navigationBar ??= new NavigationBar(this.Title, "fa-institution")
            .AddSuperGroup()
                .AddGroup("Home", "fa-business-time", new NavigationUrl())
            .AddSuperGroup("Manage Experty", null, null, Roles.All)
                .AddGroup("Assignments", "fa-cog", null, Roles.All)
                    .AddItem("Import New Accident", null, new NavigationUrl("Accident", "Import"), Roles.AdminContributor)
                    .AddItem("Assignments", null, new NavigationUrl("Accident"), Roles.All)
            .AddSuperGroup("Manage", null, null, Roles.Admin)
                .AddGroup("Manage Members", "fa-user-check")
                    .AddItem("Members", null, new NavigationUrl("User"))
                .AddGroup("Companies", "fa-memory", null)
                    .AddItem("Insurance Adjusters", null, new NavigationUrl("InsuranceAdjuster"))
                    .AddItem("Inspectorates", null, new NavigationUrl("Inspectorate"))
                    .AddItem("Insurance Agencies", null, new NavigationUrl("InsuranceAgency"))
                    .AddItem("Insurance Companies", null, new NavigationUrl("InsuranceCompany"))
            .AddSuperGroup("Customer and Utility", null, null, Roles.AdminContributor)
                .AddGroup("Customers", "fa-user")
                    .AddItem("Insurances", null, new NavigationUrl("Insurance"))
                    .AddItem("Customers", null, new NavigationUrl("Customer"))
                .AddGroup("Companies", "fa-tools")
                    .AddItem("Locations", null, new NavigationUrl("Location"))
            .Build();
    }
    public class HttpHelperManager
    {
        private readonly Manager Manager;
        public HttpHelperManager(Manager manager)
            => this.Manager = manager;
        public const string UserAgentHeader = "User-Agent";
        private static readonly Regex Device = new Regex(@"1207|6310|6590|3gso|4thp|50[1-6]i|770s|802s|a wa|abac|ac(er|oo|s\-)|ai(ko|rn)|al(av|ca|co)|amoi|an(ex|ny|yw)|aptu|ar(ch|go)|as(te|us)|attw|au(di|\-m|r |s )|avan|be(ck|ll|nq)|bi(lb|rd)|bl(ac|az)|br(e|v)w|bumb|bw\-(n|u)|c55\/|capi|ccwa|cdm\-|cell|chtm|cldc|cmd\-|co(mp|nd)|craw|da(it|ll|ng)|dbte|dc\-s|devi|dica|dmob|do(c|p)o|ds(12|\-d)|el(49|ai)|em(l2|ul)|er(ic|k0)|esl8|ez([4-7]0|os|wa|ze)|fetc|fly(\-|_)|g1 u|g560|gene|gf\-5|g\-mo|go(\.w|od)|gr(ad|un)|haie|hcit|hd\-(m|p|t)|hei\-|hi(pt|ta)|hp( i|ip)|hs\-c|ht(c(\-| |_|a|g|p|s|t)|tp)|hu(aw|tc)|i\-(20|go|ma)|i230|iac( |\-|\/)|ibro|idea|ig01|ikom|im1k|inno|ipaq|iris|ja(t|v)a|jbro|jemu|jigs|kddi|keji|kgt( |\/)|klon|kpt |kwc\-|kyo(c|k)|le(no|xi)|lg( g|\/(k|l|u)|50|54|\-[a-w])|libw|lynx|m1\-w|m3ga|m50\/|ma(te|ui|xo)|mc(01|21|ca)|m\-cr|me(rc|ri)|mi(o8|oa|ts)|mmef|mo(01|02|bi|de|do|t(\-| |o|v)|zz)|mt(50|p1|v )|mwbp|mywa|n10[0-2]|n20[2-3]|n30(0|2)|n50(0|2|5)|n7(0(0|1)|10)|ne((c|m)\-|on|tf|wf|wg|wt)|nok(6|i)|nzph|o2im|op(ti|wv)|oran|owg1|p800|pan(a|d|t)|pdxg|pg(13|\-([1-8]|c))|phil|pire|pl(ay|uc)|pn\-2|po(ck|rt|se)|prox|psio|pt\-g|qa\-a|qc(07|12|21|32|60|\-[2-7]|i\-)|qtek|r380|r600|raks|rim9|ro(ve|zo)|s55\/|sa(ge|ma|mm|ms|ny|va)|sc(01|h\-|oo|p\-)|sdk\/|se(c(\-|0|1)|47|mc|nd|ri)|sgh\-|shar|sie(\-|m)|sk\-0|sl(45|id)|sm(al|ar|b3|it|t5)|so(ft|ny)|sp(01|h\-|v\-|v )|sy(01|mb)|t2(18|50)|t6(00|10|18)|ta(gt|lk)|tcl\-|tdg\-|tel(i|m)|tim\-|t\-mo|to(pl|sh)|ts(70|m\-|m3|m5)|tx\-9|up(\.b|g1|si)|utst|v400|v750|veri|vi(rg|te)|vk(40|5[0-3]|\-v)|vm40|voda|vulc|vx(52|53|60|61|70|80|81|83|85|98)|w3c(\-| )|webc|whit|wi(g |nc|nw)|wmlb|wonu|x700|yas\-|your|zeto|zte\-", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex OS = new Regex(@"(android|bb\d+|meego).+mobile|avantgo|bada\/|blackberry|blazer|compal|elaine|fennec|hiptop|iemobile|ip(hone|od)|iris|kindle|lge |maemo|midp|mmp|mobile.+firefox|netfront|opera m(ob|in)i|palm( os)?|phone|p(ixi|re)\/|plucker|pocket|psp|series(4|6)0|symbian|treo|up\.(browser|link)|vodafone|wap|windows ce|xda|xiino", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private PlatformDevice? platformDevice;
        public PlatformDevice PlatformDevice
        {
            get
            {
                if (platformDevice == null)
                {
                    string userAgent = Manager.HttpContextAccessor.HttpContext.Request.Headers[UserAgentHeader];
                    bool result = false;
                    string deviceInfo = string.Empty;
                    if (OS.IsMatch(userAgent))
                        deviceInfo = OS.Match(userAgent).Groups[0].Value;
                    if (Device.IsMatch(userAgent.Substring(0, 4)))
                        deviceInfo += Device.Match(userAgent).Groups[0].Value;
                    if (!string.IsNullOrEmpty(deviceInfo))
                        result = true;
                    platformDevice = result ? PlatformDevice.Mobile : PlatformDevice.Desktop;
                }
                return platformDevice.Value;
            }
        }
        public bool IsMobile => this.PlatformDevice == PlatformDevice.Mobile;
    }
    public class UserHelperManager
    {
        private readonly Manager Manager;
        public UserHelperManager(Manager manager)
            => this.Manager = manager;
        public bool IsManager => Manager.HttpContextAccessor.HttpContext.User.IsInRole(Roles.Admin) || Manager.HttpContextAccessor.HttpContext.User.IsInRole(Roles.Contributor);
        //public bool IsManager => false;
        public bool IsReader => Manager.HttpContextAccessor.HttpContext.User.IsInRole(Roles.Reader);
        //public bool IsReader => true;
        public string Id => Manager.HttpContextAccessor.HttpContext.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        public bool IsSuperAdmin => Manager.HttpContextAccessor.HttpContext.User.Identity.Name == "alessandro.rapiti44@gmail.com";
    }
    public class ChartManager
    {
        private readonly Manager Manager;
        public ChartManager(Manager manager)
            => this.Manager = manager;
        public async Task<DataChart> GetExpertisesStatus(IStringLocalizer localizer, DateTime from = default, DateTime to = default)
        {
            var status = (await ExpertisesByDate(from, to).ToListAsync()).GroupBy(x => x.Status);
            return new MyChart(localizer["Status"], status.ToDictionary(x => localizer[$"{(StatusType)x.Key}"].Value, x => new DataModel() { Data = new List<decimal> { (decimal)x.Count() } })).ToDataChart(ChartType.Pie);
        }
        public async Task<DataChart> GetContributorStatus(IStringLocalizer localizer, string contributorId, DateTime from = default, DateTime to = default)
        {
            var status = (await ExpertisesByDate(from, to).Where(x => x.ContributorId == contributorId).ToListAsync()).GroupBy(x => x.Status);
            var contributor = await Manager.Context.AspNetUsers.FirstOrDefaultAsync(x => x.Id == contributorId);
            return new MyChart($"{localizer["Status"]} {contributor.UserName}", status.ToDictionary(x => localizer[$"{(StatusType)x.Key}"].Value, x => new DataModel() { Data = new List<decimal> { (decimal)x.Count() } })).ToDataChart(ChartType.Pie);
        }
        public async Task<DataChart> GetClosingDay(IStringLocalizer localizer, DateTime from = default, DateTime to = default)
        {
            var status = (await ExpertisesByDate(from, to).ToListAsync()).GroupBy(x => x.Status);
            var dictionary = new Dictionary<int, Dictionary<string, int>>();
            //foreach (var s in status)
            //{
            //    if (!dictionary.ContainsKey(s.Key))
            //        dictionary.Add(s.Key, new Dictionary<string, int>());
            //    foreach (var g in s.Where(y => y.CloseDate.HasValue).GroupBy(y => $"{y.CloseDate.Value.Month}{y.CloseDate.Value.Day}"))
            //    {

            //    }
            //}
            //return new MyChart(localizer["Trend"], status.ToDictionary(x => localizer[$"{(StatusType)x.Key}"].Value,
            //    x => new DataModel() { Data = x.Where(y => y.CloseDate.HasValue).GroupBy(y => $"{y.CloseDate.Value.Month}{y.CloseDate.Value.Day}").Select(y => y.Count()) })).ToDataChart();
            return default;
        }
        //public async Task<DataChart> GetClosingDistance(IStringLocalizer localizer, DateTime from = default, DateTime to = default)
        //{
        //}
        private IQueryable<TableExpertise> ExpertisesByDate(DateTime from, DateTime to)
        {
            if (from == default)
                from = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
            if (to == default)
                to = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(1);
            return Manager.Context.TableExpertise.Where(x => !x.AssignmentDate.HasValue || (x.AssignmentDate.Value >= from && x.AssignmentDate.Value <= to));
        }
        private class MyChart : IChart
        {
            public string X { get; }
            public string Y { get; }
            public string Title { get; }
            public Dictionary<string, DataModel> Datasets { get; }
            public MyChart(string title, Dictionary<string, DataModel> datasets) : this(title, datasets, null, null) { }
            public MyChart(string title, Dictionary<string, DataModel> datasets, string x, string y)
            {
                this.Title = title;
                this.Datasets = datasets;
                this.X = x;
                this.Y = y;
            }
        }
    }
    public class FileManager
    {
        private readonly Manager Manager;
        public FileManager(Manager manager)
            => this.Manager = manager;
        public async Task<IList<string>> GetUriFiles(int id, string name = null)
            => await new File().SearchAsync($"{id}/{name}");
        public async Task<bool> SaveFile(int id, Stream stream, string fileName, string contentType, string name = null)
        {
            if (name == null)
                name = Guid.NewGuid().ToString("N");
            else if (name.EndsWith("/"))
                name = $"{name}{Guid.NewGuid():N}";
            return await new File()
            {
                Name = $"{id}/{name}.{fileName.Split('.').Last()}",
                Properties = new DataProperties()
                {
                    ContentType = contentType
                },
                Stream = stream
            }.WriteAsync();
        }
    }
    public class NavigationManager
    {
        public INavigation<TableAccident> GetAccidentNavigation(string title, IStringLocalizer localizer, Manager manager)
           => INavigation<TableAccident>
               .Create(NavigationOptions.CanDone(manager.UserHelperManager.IsManager, true, manager.UserHelperManager.IsManager, title, localizer, "Edit"))
               .Include(x => x.Id, PropertyOptions.Key)
               .Include(x => x.TableExpertise)
                    .ThenInclude(x => x.ConcreteId)
                .Include(x => x.InsuranceAgency)
                    .ThenInclude(x => x.InsuranceCompany)
                        .ThenInclude(x => x.Label)
                .Include(x => x.Insurance)
                   .ThenInclude(x => x.Customer)
                        .ThenInclude(x => x.Name)
                .Include(x => x.Insurance)
                   .ThenInclude(x => x.Contractor)
                        .ThenInclude(x => x.Name)
               .Include(x => x.TableDamaged)
                    .ThenInclude(x => x.Customer)
                        .ThenInclude(x => x.Name)
                .Include(x => x.Insurance)
                   .ThenInclude(x => x.ExternalId)
                .Include(x => x.AccidentDate, new PropertyOptions() { Format = "dd-MM-yyyy" })
                .Include(x => x.ExternalId)
                .Include(x => x.TableExpertise)
                    .ThenInclude(x => x.StatusType, new PropertyOptions { IsLocalized = true });
        public INavigation<TableCustomer> GetCustomerNavigation(string title, IStringLocalizer localizer)
            => INavigation<TableCustomer>.Create(NavigationOptions.CanDoneAll(title, localizer, "Edit"))
                .Include(x => x.Id, PropertyOptions.Key)
                .Include(x => x.Name)
                .Include(x => x.FiscalCode)
                .Include(x => x.TableInsuranceContractor, PropertyOptions.Count)
                .Include(x => x.TableInsuranceCustomer, PropertyOptions.Count)
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
                .Include(x => x.StatusType, new PropertyOptions() { IsLocalized = true })
                .Include(x => x.Member)
                    .ThenInclude(x => x.UserName)
                .Include(x => x.TotalPercentage)
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
        public INavigation<TableInsuranceAdjuster> GetInsuranceAdjusterNavigation(string title, IStringLocalizer localizer)
           => INavigation<TableInsuranceAdjuster>
               .Create(NavigationOptions.CanDoneAll(title, localizer, "Edit"))
               .Include(x => x.Id, PropertyOptions.Key)
               .Include(x => x.Name);
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
#warning Quando aumenteranno le istanze ricordarsi di metterlo con il blob
        private static readonly TrafficLight TrafficLight = new TrafficLight();
        public async Task InsertAccidentAsync(TableAccident tableAccident)
        {
            if (tableAccident.Id == 0)
            {
                var startCurrentYear = new DateTime(DateTime.UtcNow.Year, 1, 1);
                using (var locked = await TrafficLight.CreateAsync())
                {
                    var previousAccident = await Manager.Context.TableAccident
                        .Where(x => x.AccidentDate >= startCurrentYear)
                        .OrderByDescending(x => x.YearlyId)
                        .FirstOrDefaultAsync();
                    int id = 1;
                    if (previousAccident != null)
                        id = previousAccident.YearlyId + 1;
                    tableAccident.YearlyId = id;
                    Manager.Context.Add(tableAccident);
                    await Manager.Context.SaveChangesAsync();
                }
            }
        }
        public IQueryable<TableAccident> Accidents => Manager.Context.TableAccident
            .Include(t => t.Inspectorate)
            .Include(t => t.Insurance)
                .ThenInclude(t => t.Customer)
            .Include(t => t.Insurance)
                .ThenInclude(t => t.Contractor)
            .Include(t => t.InsuranceAgency)
                .ThenInclude(t => t.InsuranceCompany)
            .Include(t => t.Address)
            .Include(t => t.TableDamaged)
                .ThenInclude(t => t.Customer)
            .Include(t => t.InsuranceAdjuster)
            .Include(t => t.TableExpertise)
                .ThenInclude(t => t.Contributor)
            .Include(t => t.TableExpertise)
                .ThenInclude(t => t.Member);
        public IQueryable<TableExpertise> Expertises => Manager.Context.TableExpertise
            .Include(x => x.TableBill)
            .Include(x => x.TableMessage)
                .ThenInclude(x => x.User)
            .Include(x => x.Branch)
                .ThenInclude(x => x.BranchType)
            .Include(x => x.Contributor)
            .Include(x => x.Member);
        public IQueryable<TableExpertise> ExpertiseBills => Manager.Context.TableExpertise
            .Include(x => x.TableBill);
        public IQueryable<TableLocation> Locations => Manager.Context.TableLocation
            .Include(x => x.City);
        public IQueryable<TableInsuranceAgency> InsuranceAgencies => Manager.Context.TableInsuranceAgency
                    .Include(x => x.InsuranceCompany)
                    .Include(x => x.Address);
        public IQueryable<TableInsuranceAdjuster> InsuranceAdjusters => Manager.Context.TableInsuranceAdjuster;
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
                    .Include(x => x.TableInsuranceCustomer)
                    .Include(x => x.TableInsuranceContractor);
        public IQueryable<AspNetUsers> Users => Manager.Context.AspNetUsers
            .Include(x => x.AspNetUserRoles)
                .ThenInclude(x => x.Role);
        public IQueryable<AspNetUsers> AllContributors => Manager.Context.AspNetUsers
            .Include(x => x.AspNetUserRoles)
                .ThenInclude(x => x.Role)
                    .Where(x => x.AspNetUserRoles.Any(x => x.Role.Name == Roles.Reader));
    }
    public class DropdownManager
    {
        private readonly Manager Manager;
        public DropdownManager(Manager manager)
            => this.Manager = manager;
#warning da mettere la parte di controllo che se quel tizio ha quella insurance company, può avere solo cose inerenti a quella compagnia o strettamente legate
        public IEnumerable<DropdownItem> GetInspectorates(int? inspectorateId)
        {
            return this.Manager.QueryManager.Inspectorates
                .Select(x => new DropdownItem()
                {
                    Label = x.Label,
                    Value = x.Id.ToString(),
                    Group = x.InsuranceCompany != null ? x.InsuranceCompany.Label : string.Empty,
                    Selected = x.Id == inspectorateId
                });
        }
        public IEnumerable<DropdownItem> GetInsuranceAgencies(int? insuranceAgencyId)
        {
            //TableInsurance insurance = await this.Manager.QueryManager.Insurances.FirstOrDefaultAsync(x => x.Id == insuranceId);
            return this.Manager.QueryManager.InsuranceAgencies
                .Select(x => new DropdownItem()
                {
                    Label = x.Label,
                    Value = x.Id.ToString(),
                    Group = x.InsuranceCompany != null ? x.InsuranceCompany.Label : string.Empty,
                    Selected = x.Id == insuranceAgencyId
                });
        }
        public IEnumerable<DropdownItem> GetInsuranceCompanies(int? insuranceCompanyId)
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
        public IEnumerable<DropdownItem> GetInsuranceAdjusters(int? adjusterId)
        {
            return this.Manager.QueryManager.InsuranceAdjusters
                .Select(x => new DropdownItem()
                {
                    Label = x.Name,
                    Value = x.Id.ToString(),
                    Selected = x.Id == adjusterId
                });
        }
        public IEnumerable<DropdownItem> GetLocations(int? locationId)
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
        public IEnumerable<DropdownItem> GetCities(int? selectedCity)
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
        public IEnumerable<DropdownItem> GetCustomers(int? customerId)
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
        public IEnumerable<DropdownItem> GetDamagedCustomers(IEnumerable<int> damageds)
        {
            return Manager.QueryManager.Customers.Select(
                   customer =>
               new DropdownItem()
               {
                   Label = customer.Name,
                   Value = customer.Id.ToString(),
                   Selected = damageds != null && damageds.Contains(customer.Id),
                   Group = customer.Name[0].ToString(),
               });
        }
        public IEnumerable<DropdownItem> GetInsurances(int? insuranceId)
        {
            return this.Manager.QueryManager.Insurances
                .Select(x => new DropdownItem()
                {
                    Value = x.Id.ToString(),
                    Group = x.InsuranceCompany.Label,
                    Label = x.ExternalId,
                    //Label = $"{x.Customer.Name} - {x.ExternalId}",
                    Selected = x.Id == insuranceId
                });
        }
    }
    public class AjaxRequestManager
    {
        private readonly Manager Manager;
        public AjaxRequestManager(Manager manager)
            => this.Manager = manager;
        public RequestContext CreateNewLocation => new RequestContext() { Action = "Create", Controller = "Location" };
        public RequestContext CreateNewCustomer => new RequestContext() { Action = "Create", Controller = "Customer" };
        public RequestContext CreateNewContractor => new RequestContext() { Action = "CreateContractor", Controller = "Customer" };
        public RequestContext ModifyNewBill(int? id) => new RequestContext() { Action = "Bill", Controller = "Experty", FurtherPath = id?.ToString(), RequestType = RequestType.Post, Selector = "#billwrapper" };
        public RequestContext DeleteBill(int id) => new RequestContext() { Action = "DeleteBill", Controller = "Experty", FurtherPath = id.ToString(), RequestType = RequestType.Post, Selector = "#billwrapper" };
        public RequestContext UpdatePostalCodes => new RequestContext() { Action = "UpdatePostalCodes", Controller = "Location", Selector = "#updatePostalCodes" };
        public RequestContext SetLocation => new RequestContext() { Action = "Create", Controller = "Location", RequestType = RequestType.Post, Selector = ".location-address" };
        public RequestContext SetCustomer => new RequestContext() { Action = "Create", Controller = "Customer", RequestType = RequestType.Post, Selector = "#customers" };
        public RequestContext SetContractor => new RequestContext() { Action = "CreateContractor", Controller = "Customer", RequestType = RequestType.Post, Selector = "#contractors" };
        public RequestContext SetDamaged => new RequestContext() { Action = "CreateDamaged", Controller = "Customer", RequestType = RequestType.Post, SuccessCallback = "experty.setDamaged" };
        public RequestContext SetInsurance => new RequestContext() { Action = "Create", Controller = "Insurance", RequestType = RequestType.Post, Selector = "#insurances" };
        public RequestContext SetInsuranceCompany => new RequestContext() { Action = "Create", Controller = "InsuranceCompany", RequestType = RequestType.Post, Selector = "#insurance-companies" };
        public RequestContext SetInsuranceAgency => new RequestContext() { Action = "Create", Controller = "InsuranceAgency", RequestType = RequestType.Post, Selector = "#insurance-agencies" };
        public RequestContext SetInspectorate => new RequestContext() { Action = "Create", Controller = "Inspectorate", RequestType = RequestType.Post, Selector = "#inspectorates" };
        public RequestContext SetInsuranceAdjuster => new RequestContext() { Action = "Create", Controller = "InsuranceAdjuster", RequestType = RequestType.Post, Selector = "#insurance-adjusters" };
        public RequestContext CreateNewInsurance => new RequestContext() { Action = "Create", Controller = "Insurance" };
        public RequestContext CreateNewInsuranceCompany => new RequestContext() { Action = "Create", Controller = "InsuranceCompany" };
        public RequestContext CreateNewInsuranceAgency => new RequestContext() { Action = "Create", Controller = "InsuranceAgency" };
        public RequestContext CreateNewInspectorate => new RequestContext() { Action = "Create", Controller = "Inspectorate" };
        public RequestContext CreateNewDamaged => new RequestContext() { Action = "CreateDamaged", Controller = "Customer" };
        public RequestContext CreateNewInsuranceAdjuster => new RequestContext() { Action = "Create", Controller = "InsuranceAdjuster" };
        public RequestContext UpdateUserRole(string userId) => new RequestContext() { Action = "UpdateRole", Controller = "User", QueryString = new Dictionary<object, object>() { { "id", userId } } };
        public RequestContext ShowStats => new RequestContext() { Action = "Stats", Controller = "Home", RequestType = RequestType.Post, Selector = "#stats-update" };
    }
    public class ServiceManager
    {
        private static readonly EmailSender emailSender = new EmailSender();
        public EmailSender EmailSender => emailSender;
    }
}