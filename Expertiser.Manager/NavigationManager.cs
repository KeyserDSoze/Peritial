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
                .Include(x => x.ExternalId)
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
                .Include(x => x.TableExpertise)
                    .ThenInclude(x => x.AssignmentDate, new PropertyOptions() { Format = "dd-MM-yyyy" })
                .Include(x => x.TableExpertise)
                    .ThenInclude(x => x.StatusType, new PropertyOptions { IsLocalized = true })
                .Include(x => x.TableExpertise)
                    .ThenInclude(x => x.Contributor)
                        .ThenInclude(x => x.TotalName);
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
}