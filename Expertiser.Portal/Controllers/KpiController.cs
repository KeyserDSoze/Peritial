using Expertiser.Portal.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.OData.Edm;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Expertiser.Portal.Controllers
{
    [Authorize]
    public class KpiController : Controller
    {
        private readonly Manager Manager;
        public KpiController(Manager manager)
            => Manager = manager;
        public IActionResult Index()
        {
            View(new DateStats()
            {
                From = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1),
                To = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(1),
            });
        }
    }
}
