using Expertiser.Base.Constant;
using Expertiser.Database.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Expertiser
{
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
                var startCurrentYear = new DateTime(tableAccident.UploadDate.Year, 1, 1);
                using (var locked = await TrafficLight.CreateAsync())
                {
                    var previousAccident = await Manager.Context.TableAccident
                        .Where(x => x.UploadDate >= startCurrentYear)
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
            .Include(x => x.Member)
            .Include(x => x.Accident)
                .ThenInclude(t => t.InsuranceAgency)
                    .ThenInclude(t => t.InsuranceCompany);
        public IQueryable<TableExpertise> ExpertisesWithAll => Manager.Context.TableExpertise
            .Include(x => x.TableBill)
            .Include(x => x.TableMessage)
                .ThenInclude(x => x.User)
            .Include(x => x.Branch)
                .ThenInclude(x => x.BranchType)
            .Include(x => x.Contributor)
            .Include(x => x.Member)
            .Include(x => x.Accident)
                .ThenInclude(x => x.TableDamaged)
                    .ThenInclude(x => x.Customer)
            .Include(x => x.Accident)
                .ThenInclude(x => x.Address)
            .Include(x => x.Accident)
                .ThenInclude(t => t.InsuranceAgency)
                    .ThenInclude(t => t.InsuranceCompany)
            .Include(x => x.Accident)
                .ThenInclude(t => t.Insurance)
                    .ThenInclude(t => t.Contractor)
            .Include(x => x.Accident)
                .ThenInclude(t => t.InsuranceAdjuster)
            .Include(x => x.Accident)
                .ThenInclude(t => t.Inspectorate);
        public IQueryable<TableExpertise> ExpertiseBills => Manager.Context.TableExpertise
            .Include(x => x.TableBill);
        public IQueryable<TableExpertise> Bills => Manager.Context.TableExpertise
            .Include(x => x.TableBill)
            .Include(x => x.Accident)
                .ThenInclude(x => x.InsuranceAgency)
                    .ThenInclude(x => x.InsuranceCompany)
            .Include(x => x.Contributor)
            .Include(x => x.Member);
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
}