using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Expertiser.Base.Constant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Expertiser.Portal.Controllers
{
    [Authorize(Roles = (Roles.All))]
    public class FileController : Controller
    {
        private readonly Manager Manager;
        public FileController(Manager manager) 
            => this.Manager = manager;
        [HttpPost]
        public async Task<IActionResult> Upload(int id, IFormFile file)
        {
            await this.Manager.FileManager.SaveFile(id, file, Request.Query["name"]);
            return Ok();
        }
    }
}