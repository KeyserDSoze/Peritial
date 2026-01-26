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
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using Rystem.Web;

namespace Expertiser.Portal.Controllers
{
    [Authorize(Roles = (Roles.All))]
    public class FileController : Controller
    {
        private readonly Manager Manager;
        private readonly IStringLocalizer<SharedResource> StringLocalizer;
        private readonly IWebHostEnvironment WebHostEnvironment;

        public FileController(Manager manager, IStringLocalizer<SharedResource> stringLocalizer, IWebHostEnvironment webHostEnvironment)
        {
            this.Manager = manager;
            this.StringLocalizer = stringLocalizer;
            this.WebHostEnvironment = webHostEnvironment;
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
                    ? "REPERTI FOTOGRAFICI" 
                    : title.ToUpper();

                // Scarica tutte le immagini con deduplicazione per hash
                using var httpClient = new HttpClient();
                var seenHashes = new HashSet<string>();
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
                            var hashString = Convert.ToHexString(hash);
                            
                            // Se l'hash non è mai stato visto, aggiungi l'immagine
                            if (seenHashes.Add(hashString))
                            {
                                images.Add((imageBytes, imageFile.Name));
                            }
                            else
                            {
                                // Hash duplicato - scarta l'immagine
                                duplicateCount++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Errore nel download: {imageFile.Name}");
                    }
                }

                if (!images.Any())
                    return BadRequest("No valid images found");

                // Genera il PDF con griglia 2x2 (4 foto per pagina)
                var pdf = Document.Create(container =>
                {
                    container.Page(page =>
                    {
                        page.Margin(20);
                        
                        // HEADER con ARBEC logo e testo
                        page.Header()
                            .Column(col =>
                            {
                                col
                                    .Item()
                                    .Background("#FFFFFF")
                                    .Padding(10)
                                    .Row(row =>
                                    {
                                        // Logo ARBEC - molto piccolo a sinistra
                                        row.ConstantColumn(28)
                                            .AlignLeft()
                                            .Height(28)
                                            .Image(System.IO.Path.Combine(this.WebHostEnvironment.WebRootPath, "images/logos/arbec-logo.png"));
                                        
                                        // Testo ARBEC - attaccato al logo
                                        row.RelativeColumn()
                                            .PaddingLeft(5)
                                            .Column(textCol =>
                                            {
                                                textCol
                                                    .Item()
                                                    .AlignLeft()
                                                    .Text("ARBEC")
                                                    .FontSize(16)
                                                    .Bold()
                                                    .FontColor("#000000");
                                                textCol
                                                    .Item()
                                                    .AlignLeft()
                                                    .Text("Professional Loss Adjuster")
                                                    .FontSize(8)
                                                    .FontColor("#666666");
                                            });
                                    });
                                col.Item().Height(5);
                            });
                        
                        page.Content()
                            .Column(column =>
                            {
                                // Titolo
                                column
                                    .Item()
                                    .Background("#404040")
                                    .Padding(15)
                                    .Column(col =>
                                    {
                                        col
                                            .Item()
                                            .Text(reportTitle)
                                            .FontSize(24)
                                            .Bold()
                                            .FontColor("#FFFFFF")
                                            .AlignCenter();

                                        col
                                            .Item()
                                            .PaddingTop(5)
                                            .Text($"Data: {DateTime.Now:dd/MM/yyyy HH:mm:ss}")
                                            .FontSize(10)
                                            .FontColor("#FFFFFF")
                                            .AlignCenter();

                                        if (duplicateCount > 0)
                                        {
                                            col
                                                .Item()
                                                .Text($"⚠️ {duplicateCount} foto duplicate rimosse - Totale: {images.Count} foto")
                                                .FontSize(9)
                                                .FontColor("#FFC800")
                                                .AlignCenter();
                                        }
                                        else
                                        {
                                            col
                                                .Item()
                                                .Text($"Totale foto: {images.Count}")
                                                .FontSize(9)
                                                .FontColor("#FFFFFF")
                                                .AlignCenter();
                                        }
                                    });

                                column.Item().Height(20);

                                // Crea pagine con griglia 2x2
                                int imageIndex = 0;
                                while (imageIndex < images.Count)
                                {
                                    // Riga 1 (2 foto)
                                    column
                                        .Item()
                                        .Row(row =>
                                        {
                                            // Foto 1 della riga - SINISTRA (allineata a destra)
                                            if (imageIndex < images.Count)
                                            {
                                                row.RelativeColumn()
                                                    .Padding(5)
                                                    .Column(col =>
                                                    {
                                                        col
                                                            .Item()
                                                            .MaxWidth(250)
                                                            .MaxHeight(250)
                                                            .Image(images[imageIndex].Data);
                                                        col
                                                            .Item()
                                                            .PaddingTop(5)
                                                            .Text(images[imageIndex].Name)
                                                            .FontSize(8)
                                                            .Italic();
                                                    });
                                                imageIndex++;
                                            }

                                            // Foto 2 della riga - DESTRA (allineata a sinistra)
                                            if (imageIndex < images.Count)
                                            {
                                                row.RelativeColumn()
                                                    .Padding(5)
                                                    .Column(col =>
                                                    {
                                                        col
                                                            .Item()
                                                            .MaxWidth(250)
                                                            .MaxHeight(250)
                                                            .Image(images[imageIndex].Data);
                                                        col
                                                            .Item()
                                                            .PaddingTop(5)
                                                            .Text(images[imageIndex].Name)
                                                            .FontSize(8)
                                                            .Italic();
                                                    });
                                                imageIndex++;
                                            }
                                        });

                                    // Riga 2 (2 foto)
                                    if (imageIndex < images.Count)
                                    {
                                        column.Item().Height(10);

                                        column
                                            .Item()
                                            .Row(row =>
                                            {
                                                // Foto 3 della riga - SINISTRA (allineata a destra)
                                                if (imageIndex < images.Count)
                                                {
                                                    row.RelativeColumn()
                                                        .Padding(5)
                                                        .Column(col =>
                                                        {
                                                            col
                                                                .Item()
                                                                .MaxWidth(250)
                                                                .MaxHeight(250)
                                                                .Image(images[imageIndex].Data);
                                                            col
                                                                .Item()
                                                                .PaddingTop(5)
                                                                .Text(images[imageIndex].Name)
                                                                .FontSize(8)
                                                                .Italic();
                                                        });
                                                    imageIndex++;
                                                }

                                                // Foto 4 della riga - DESTRA (allineata a sinistra)
                                                if (imageIndex < images.Count)
                                                {
                                                    row.RelativeColumn()
                                                        .Padding(5)
                                                        .Column(col =>
                                                        {
                                                            col
                                                                .Item()
                                                                .MaxWidth(250)
                                                                .MaxHeight(250)
                                                                .Image(images[imageIndex].Data);
                                                            col
                                                                .Item()
                                                                .PaddingTop(5)
                                                                .Text(images[imageIndex].Name)
                                                                .FontSize(8)
                                                                .Italic();
                                                        });
                                                    imageIndex++;
                                                }
                                        });

                                        // Spacer tra pagine
                                        if (imageIndex < images.Count)
                                        {
                                            column.Item().Height(15);
                                        }
                                    }
                                }
                            });
                        
                        
                        
                        // FOOTER con testo, linea e logo
                        page.Footer()
                            .Column(col =>
                            {
                                

                                // Linea di separazione
                                col.Item().Height(5);
                                col
                                    .Item()
                                    .BorderTop(1)
                                    .BorderColor("#CCCCCC")
                                    .PaddingTop(5);

                                // Testo di contatto (PRIMA della linea)
                                col
                                    .Item()
                                    .Column(textCol =>
                                    {
                                        textCol
                                            .Item()
                                            .Text("Geom. Leonardo Rossi  -  p.iva 01908160565")
                                            .FontSize(8)
                                            .AlignCenter();
                                        
                                        textCol
                                            .Item()
                                            .Text("Sede Legale: Str. Pian del Cerro 13/F – 01100 Viterbo (VT)  |  rec. 333. 8295491  |  e-mail: leonardorossiprof@gmail.com")
                                            .FontSize(7)
                                            .AlignCenter();
                                        
                                        textCol
                                            .Item()
                                            .Text("Sedi operative: Via A. Volta 15 – 01100 Viterbo (VT) / Via Maglianello 9 – 02046 Magliano Sabina (RI) / Via G. Rosaccio 77 – 00156 Roma (RM)")
                                            .FontSize(7)
                                            .AlignCenter();
                                    });

                                col.Item().Height(5);

                                // Logo bar (DOPO la linea)
                                col
                                    .Item()
                                    .AlignCenter()
                                    .Row(row =>
                                    {
                                        // APAI logo
                                        row.RelativeColumn()
                                            .AlignCenter()
                                            .Height(50)
                                            .Image(System.IO.Path.Combine(this.WebHostEnvironment.WebRootPath, "images/logos/apai-logo.png"));
                                        
                                        // ICMQ logo
                                        row.RelativeColumn()
                                            .AlignCenter()
                                            .Height(50)
                                            .Image(System.IO.Path.Combine(this.WebHostEnvironment.WebRootPath, "images/logos/icmq-logo.png"));
                                        
                                        // Certificazione logo
                                        row.RelativeColumn()
                                            .AlignCenter()
                                            .Height(50)
                                            .Image(System.IO.Path.Combine(this.WebHostEnvironment.WebRootPath, "images/logos/certificazione-logo.png"));
                                    });
                            });
                    });
                })
                .GeneratePdf();

                // Sanitizza il titolo: sostituisci spazi e caratteri speciali con underscore
                var sanitizedTitle = System.Text.RegularExpressions.Regex.Replace(
                    title ?? "REPERTI_FOTOGRAFICI", 
                    @"[^\w\-]", 
                    "_"
                );
                
                var fileName = $"{sanitizedTitle}_{fileId}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
                
                // Imposta esplicitamente il Content-Disposition header
                Response.Headers.Add("Content-Disposition", $"attachment; filename=\"{fileName}\"");
                
                return File(pdf, "application/pdf");
            }
            catch (Exception ex)
            {
                return BadRequest($"Error: {ex.Message}");
            }
        }
    }
}