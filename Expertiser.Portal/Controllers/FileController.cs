using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Expertiser.Base.Constant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Expertiser.Portal.Controllers
{
    [Authorize(Roles = (Roles.All))]
    public class FileController : Controller
    {
        private readonly Manager Manager;
        private readonly IStringLocalizer<SharedResource> StringLocalizer;
        public FileController(Manager manager, IStringLocalizer<SharedResource> stringLocalizer)
        {
            this.Manager = manager;
            this.StringLocalizer = stringLocalizer;
        }

        [HttpPost]
        public async Task<IActionResult> Upload(int id, IFormFile file)
        {
            await this.Manager.FileManager.SaveFile(id, file.OpenReadStream(), file.FileName, file.ContentType, Request.Query["name"]);
            string minimumStatus = Request.Query["status"];
            var state = await Manager.GetExpertyStateAsync(id, this.StringLocalizer);
            await state.Execute(null);
            await state.Next();
            return Ok();
        }
    }
}