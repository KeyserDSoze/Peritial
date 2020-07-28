using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Expertiser.Base.Constant;
using Expertiser.Interfaces;
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

        public IActionResult ShowDocument([FromQuery] string url)
            => PartialView("ShowDocument", url);

        [HttpPost]
        public async Task<IActionResult> Upload(int id, IFormFile file)
        {
            IEnumerable<string> files = await this.Manager.FileManager.SaveFile(Request.Query["fileId"], file.OpenReadStream(), file.FileName, file.ContentType, Request.Query["name"]);
            int.TryParse(Request.Query["status"], out int status);
            StatusType minimumStatus = (StatusType)status;
            var state = await Manager.GetExpertyStateAsync(id, this.StringLocalizer);
            state.AddAttachment(files);
            await state.Next(minimumStatus);
            return Ok();
        }
        public async Task<IActionResult> Proxy([FromQuery] string name, [FromQuery] IEnumerable<string> urls)
        {
            MemoryStream memoryStream = new MemoryStream();
            using ZipArchive zipArchive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true);
            using WebClient myWebClient = new WebClient();
            // Download the Web resource and save it into the current filesystem folder.
            List<byte[]> downloads = new List<byte[]>();
            foreach (var url in urls)
            {
                byte[] file = await myWebClient.DownloadDataTaskAsync(url);
                var entry = zipArchive.CreateEntry(url.Split('/').Last());
                using var entryStream = entry.Open();
                await entryStream.WriteAsync(file);
            }
            zipArchive.Dispose();
            memoryStream.Seek(0, SeekOrigin.Begin);
            return File(memoryStream.ToArray(), "application/zip", $"{name}.zip");
        }
    }
}