using Expertiser.Database.Models;
using Microsoft.EntityFrameworkCore;
using Rystem.Web;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace Expertiser
{
    public class Manager
    {
        private ContextExpertiser context;
        public ContextExpertiser Context => context ??= new ContextExpertiser(this.ConnectionString);
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
        public IQueryable<TableLocation> Locations => Manager.Context.TableLocation.Include(x => x.City);
        public IQueryable<TableInsurance> InsuranceAgencies => Manager.Context.TableInsurance
                    .Include(x => x.InsuranceCompany)
                    .ThenInclude(x => x.Address);
    }
    public class DropdownManager
    {
        private readonly Manager Manager;
        public DropdownManager(Manager manager)
            => this.Manager = manager;
        public IEnumerable<DropdownItem> GetLocations(int locationId = 0)
        {
            return this.Manager.Context.TableLocation.Include(x => x.City)
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
    }
    public class ModalManager
    {
        private readonly Manager Manager;
        public ModalManager(Manager manager)
            => this.Manager = manager;
        public RequestContext CreateNewLocation => new RequestContext() { Action = "Create", Controller = "Location" };
        public RequestContext UpdatePostalCodes => new RequestContext() { Action = "GetPostalCodes", Controller = "Location", Selector = "#updatePostalCodes", QueryString = new Dictionary<object, object>() { { "cityId", 1 } } };
        public RequestContext GetLocations => new RequestContext() { Action = "Create", Controller = "Location", RequestType = RequestType.Post, Selector = "#accidentLocation" };
    }
}
