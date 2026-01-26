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
using Microsoft.EntityFrameworkCore;
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
        public IActionResult ShowImage([FromQuery] string url)
            => PartialView("ShowImage", url);
        public async Task<IActionResult> DeleteDocument(int id, [FromQuery] string fileName)
        {
            string finding = new System.Text.RegularExpressions.Regex(@"\/files\/").Split(fileName).Last();
            await Manager.FileManager.DeleteFileAsync(finding);
            return Ok();
        }

        [HttpPost]
        public async Task<IActionResult> Upload(int id, IFormFile file, [FromQuery] string directory, [FromQuery] string fileId)
        {
            try
            {
                // Validazione del file
                if (file == null || file.Length == 0)
                    return BadRequest("File not provided or empty");

                if (string.IsNullOrWhiteSpace(directory) || directory == "root")
                    return BadRequest("Invalid directory");

                // Genera un nome di fallback se il file non ha un nome valido
                var fileName = string.IsNullOrWhiteSpace(file.FileName) 
                    ? $"{Guid.NewGuid().ToString("N")}.jpg" 
                    : file.FileName;

                IEnumerable<string> files = await this.Manager.FileManager.SaveFileAsync(fileId, file.OpenReadStream(), fileName, file.ContentType, directory);
                var state = await Manager.GetExpertyStateAsync(id, this.StringLocalizer);
                state.AddAttachment(files);
                await state.Next(this.Manager.FileManager.GetMinimumStatus(directory));
                
                // Ritorna la lista aggiornata dei file per l'aggiornamento automatico del componente
                var updatedFiles = await this.Manager.FileManager.GetExpertiseFiles(fileId);
                return Ok(updatedFiles);
            }
            catch (Exception ex)
            {
                // Log dell'errore per debugging
                return BadRequest($"Error uploading file: {ex.Message}");
            }
        }

        [HttpGet]
        public async Task<IActionResult> RefreshFileManager([FromQuery] string fileId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileId))
                    return BadRequest("fileId is required");

                var files = await this.Manager.FileManager.GetExpertiseFiles(fileId);
                // Restituisci il componente file manager renderizzato
                return PartialView("_FileManager", new { files = files, fileId = fileId });
            }
            catch (Exception ex)
            {
                return BadRequest($"Error refreshing file manager: {ex.Message}");
            }
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