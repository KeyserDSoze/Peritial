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
    public class AjaxRequestManager
    {
        private readonly Manager Manager;
        public AjaxRequestManager(Manager manager)
            => this.Manager = manager;
        public RequestContext CreateNewLocation => new RequestContext() { Action = "Create", Controller = "Location" };
        public RequestContext CreateNewCustomer => new RequestContext() { Action = "Create", Controller = "Customer" };
        public RequestContext UpdateContractors => new RequestContext() { Action = "ListContractor", Controller = "Customer", Selector = "#contractors" };
        public RequestContext CreateNewContractor => new RequestContext() { Action = "CreateContractor", Controller = "Customer" };
        public RequestContext EditInModal(int? id) => new RequestContext() { Action = "PartialEdit", Controller = "Experty", FurtherPath = id?.ToString(), RequestType = RequestType.Get };
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
        public RequestContext ShowRevenue => new RequestContext() { Action = "Index", Controller = "Bill", RequestType = RequestType.Post, Selector = "#revenue-update" };
        public RequestContext ShowDocument(string url) => new RequestContext() { Action = "ShowDocument", Controller = "File", QueryString = new Dictionary<object, object> { { "url", HttpUtility.UrlEncode(url) } } };
        public RequestContext ShowImage(string url) => new RequestContext() { Action = "ShowImage", Controller = "File", QueryString = new Dictionary<object, object> { { "url", HttpUtility.UrlEncode(url) } } };
        public RequestContext DeleteDocument(string url, int expertiseId) => new RequestContext() { Action = "DeleteDocument", Controller = "File", FurtherPath = expertiseId.ToString(), QueryString = new Dictionary<object, object> { { "url", HttpUtility.UrlEncode(url) } }, Selector = "#update-all-files" };
    }
}