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

namespace Expertiser.Portal.Controllers
{
    public abstract class BaseController : Controller
    {
        private protected readonly Manager Manager;
        public BaseController(Manager manager) 
            => this.Manager = manager;
    }
}
