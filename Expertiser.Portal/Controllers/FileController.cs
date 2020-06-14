using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Expertiser.Base.Constant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.WindowsAzure.Storage.Blob;
using Rystem.Azure.Data;

namespace Expertiser.Portal.Controllers
{
    [Authorize(Roles = (Roles.All))]
    public class FileController : Controller
    {
        [HttpPost]
        public async Task<IActionResult> Upload(int id, IFormFile file)
        {
            MemoryStream ms = new MemoryStream();
            file.OpenReadStream().CopyTo(ms);
            ms.Position = 0;
            string name = Request.Query["name"];
            await new File()
            {
                Name = $"{id}/{(name ?? Guid.NewGuid().ToString("N"))}.{file.FileName.Split('.').Last()}",
                Properties = new BlobDataProperties()
                {
                    ContentType = file.ContentType
                },
                Stream = ms
            }.WriteAsync();
            return View();
        }
    }
}