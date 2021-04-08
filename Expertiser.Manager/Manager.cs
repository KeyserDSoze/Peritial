using Expertiser.Base.Constant;
using Expertiser.Database.Models;
using Expertiser.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Azure.Documents;
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
using System.Web;

namespace Expertiser
{
    public class Manager
    {
        public string Version => "1.0.2";
        private ContextExpertiser context;
        public ContextExpertiser Context => context ??= new ContextExpertiser(this.ConnectionString);
        public IHttpContextAccessor HttpContextAccessor { get; }
        public string BaseUri => $"{HttpContextAccessor.HttpContext.Request.Scheme}://{HttpContextAccessor.HttpContext.Request.Host}";
#if DEBUG
        public string BlobBaseUri => "pirati.blob.core.windows.net";
#else
        public string BlobBaseUri => "experty.blob.core.windows.net";
#endif
        public UserManager<IdentityUser> UserManager { get; }
        public RoleManager<IdentityRole> RoleManager { get; }
        public SignInManager<IdentityUser> SignInManager { get; }
        public Manager() { }
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
        private TelemetryManager telemetryManager;
        public TelemetryManager TelemetryManager => telemetryManager ??= new TelemetryManager(this);
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
                    .AddItem("All Assignments", null, new NavigationUrl("Accident", parameters: new Dictionary<string, string> { { "Status", "-3" } }), Roles.All)
                .AddGroup("Assignments By Status", "fa-cog", null, Roles.All)
                    .AddItem("Registered Assignments", null, new NavigationUrl("Accident", parameters: new Dictionary<string, string> { { "Status", "-1" } }), Roles.All)
                    .AddItem("Assigned Assignments", null, new NavigationUrl("Accident", parameters: new Dictionary<string, string> { { "Status", "0" } }), Roles.All)
                    .AddItem("ToBeRevised Assignments", null, new NavigationUrl("Accident", parameters: new Dictionary<string, string> { { "Status", "1" } }), Roles.All)
                    .AddItem("Revised Assignments", null, new NavigationUrl("Accident", parameters: new Dictionary<string, string> { { "Status", "2" } }), Roles.All)
                    .AddItem("ToSend Assignments", null, new NavigationUrl("Accident", parameters: new Dictionary<string, string> { { "Status", "3" } }), Roles.All)
                    .AddItem("Sent Assignments", null, new NavigationUrl("Accident", parameters: new Dictionary<string, string> { { "Status", "4" } }), Roles.All)
                    .AddItem("Billed Assignments", null, new NavigationUrl("Accident", parameters: new Dictionary<string, string> { { "Status", "5" } }), Roles.All)
                    .AddItem("Duplicated Assignments", null, new NavigationUrl("Accident", parameters: new Dictionary<string, string> { { "Status", "6" } }), Roles.All)
                    .AddItem("Cancelled Assignments", null, new NavigationUrl("Accident", parameters: new Dictionary<string, string> { { "Status", "7" } }), Roles.All)
            .AddSuperGroup("Bill", null, null, Roles.All)
                .AddGroup("Bill", "fa-cog", null, Roles.All)
                    .AddItem("Bill", null, new NavigationUrl("Bill"), Roles.All)
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
}