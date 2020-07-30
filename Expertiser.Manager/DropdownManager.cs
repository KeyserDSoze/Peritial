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
                yield return StatusType.Registered;
                yield return StatusType.Assigned;
                yield return StatusType.ToBeRevised;
                yield return StatusType.Revised;
                yield return StatusType.ToSend;
                yield return StatusType.Sent;
                yield return StatusType.Billed;
                yield return StatusType.Duplicated;
                yield return StatusType.Cancelled;
            }
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
}