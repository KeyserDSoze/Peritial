using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Expertiser.Base.Constant;
using Expertiser.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using QuestPDF.Fluent;
using Rystem.Web;

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

        [HttpGet]
        public async Task<IActionResult> GeneratePhotographicReport([FromQuery] string fileId, [FromQuery] string title = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileId))
                    return BadRequest("fileId is required");

                var files = await this.Manager.FileManager.GetExpertiseFiles(fileId);
                var imageFiles = files.Where(f => f.IsImage()).ToList();

                if (!imageFiles.Any())
                    return BadRequest("No images found");

                // Titolo documento
                var reportTitle = string.IsNullOrWhiteSpace(title) 
                    ? "RAPPORTO FOTOGRAFICO" 
                    : title.ToUpper();

                // Scarica tutte le immagini con deduplicazione per hash
                using var httpClient = new HttpClient();
                var seenHashes = new HashSet<string>(); // Memorizza gli hash per rilevare duplicati
                var images = new List<(byte[] Data, string Name)>();
                var duplicateCount = 0;
                
                foreach (var imageFile in imageFiles)
                {
                    try
                    {
                        var imageBytes = await httpClient.GetByteArrayAsync(imageFile.FullName);
                        
                        // Calcola SHA256 dell'immagine
                        using (var sha256 = SHA256.Create())
                        {
                            var hash = sha256.ComputeHash(imageBytes);
                            var hashString = Convert.ToHexString(hash); // .NET 5+ oppure BitConverter.ToString(hash).Replace("-", "")
                            
                            // Se l'hash non è mai stato visto, aggiungi l'immagine
                            if (seenHashes.Add(hashString))
                            {
                                images.Add((imageBytes, imageFile.Name));
                            }
                            else
                            {
                                // Hash duplicato - scarta l'immagine
                                duplicateCount++;
                                Console.WriteLine($"⚠️ Immagine duplicata scartata: {imageFile.Name} (hash: {hashString})");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Salta le immagini che non riescono a scaricare
                        Console.WriteLine($"❌ Errore nel download: {imageFile.Name} - {ex.Message}");
                    }
                }

                if (!images.Any())
                    return BadRequest("No valid images found after deduplication");

                // Genera il PDF usando QuestPDF
                var pdf = QuestPDF.Fluent.Document.Create(container =>
                {
                    container
                        .Page(page =>
                        {
                            page.Margin(20);
                            
                            page.Content()
                                .PaddingVertical(10)
                                .Column(column =>
                                {
                                    // Titolo
                                    column
                                        .Item()
                                        .Text(reportTitle)
                                        .FontSize(24)
                                        .Bold()
                                        .AlignCenter();

                                    // Data
                                    column
                                        .Item()
                                        .PaddingVertical(5)
                                        .Text($"Data: {DateTime.Now:dd/MM/yyyy HH:mm}")
                                        .FontSize(10)
                                        .AlignCenter();

                                    // Info duplicati (se ce ne sono)
                                    if (duplicateCount > 0)
                                    {
                                        column
                                            .Item()
                                            .PaddingVertical(5)
                                            .Text($"⚠️ {duplicateCount} immagini duplicate rimosse")
                                            .FontSize(9)
                                            .AlignCenter();
                                    }

                                    // Contatore foto
                                    column
                                        .Item()
                                        .PaddingVertical(5)
                                        .Text($"Foto nel report: {images.Count}")
                                        .FontSize(9)
                                        .AlignCenter();

                                    // Spazio
                                    column.Item().Height(10);

                                    // Immagini
                                    foreach (var (imageData, imageName) in images)
                                    {
                                        column
                                            .Item()
                                            .PaddingVertical(10)
                                            .Image(imageData);

                                        column
                                            .Item()
                                            .Text(imageName)
                                            .FontSize(9)
                                            .Italic();

                                        column.Item().Height(5);
                                    }
                                });
                        });
                })
                .GeneratePdf();

                var fileName = $"Rapporto_Fotografico_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
                return File(pdf, "application/pdf", fileName);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error: {ex.Message}");
            }
        }
    }
}