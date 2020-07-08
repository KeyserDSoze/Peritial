using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Expertiser.Portal.Models;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OData.Edm;
using Microsoft.AspNetCore.Diagnostics;

namespace Expertiser.Portal.Controllers
{
    [Authorize]
    public class HomeController : Controller
    {
        private readonly Manager Manager;
        public HomeController(Manager manager)
            => Manager = manager;
        public IActionResult Index()
            => View(new DateStats()
            {
                From = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1),
                To = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(1),
            });

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Stats([FromForm] DateStats dateStats)
            => PartialView("_Stats", dateStats);

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "SCS0027:Open redirect: possibly unvalidated input in {1} argument passed to '{0}'", Justification = "<Pending>")]
        public IActionResult SetLanguage(string culture, string returnUrl)
        {
            Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
                new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1) }
            );
            return Redirect(returnUrl);
        }

        public IActionResult Privacy()
            => View();
        private const string GenericError = "GenericError";
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> Error()
        {
            string message = GenericError;
            try
            {
                IExceptionHandlerFeature context = HttpContext.Features.Get<IExceptionHandlerFeature>();
                await Manager.TelemetryManager.SaveExceptionLog(context.Error).NoContext();
                message = context.Error.Message;
            }
            catch { }
            return BadRequest(message);
        }
    }
}
