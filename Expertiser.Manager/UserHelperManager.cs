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
}