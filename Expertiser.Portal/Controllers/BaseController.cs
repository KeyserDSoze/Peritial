using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Expertiser.Database.Models;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using Rystem.Web;
using System.Net;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Rystem.Web.Backoffice;

namespace Expertiser.Portal.Controllers
{
    public abstract class BaseController : Controller
    {
        private protected readonly Manager Manager;
        public BaseController(Manager manager)
            => this.Manager = manager;
        private protected IActionResult NotOkMessage(ModelStateDictionary modelState)
            => Conflict(string.Join(",", modelState.Values.SelectMany(x => x.Errors).Select(x => x.ErrorMessage)));
        private protected IActionResult NotOkMessage(string message)
            => Conflict(message);
    }
    public abstract class BaseController<T> : BackOfficeController<T>
        where T : class
    {
        private protected readonly Manager Manager;
        public BaseController(Manager manager)
            => this.Manager = manager;
    }
}
