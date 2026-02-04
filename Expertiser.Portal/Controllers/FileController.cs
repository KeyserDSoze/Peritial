using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
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
                var pdf = QuestPDF.Fluent.Document.Create(container =>
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
                                                    .AlignRight()
                                                    .MaxWidth(250)
                                                    .MaxHeight(250)
                                                    .Image(images[imageIndex].Data);
                                                imageIndex++;
                                            }

                                            // Foto 2 della riga - DESTRA (allineata a sinistra)
                                            if (imageIndex < images.Count)
                                            {
                                                row.RelativeColumn()
                                                    .Padding(5)
                                                    .AlignLeft()
                                                    .MaxWidth(250)
                                                    .MaxHeight(250)
                                                    .Image(images[imageIndex].Data);
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
                                                        .AlignRight()
                                                        .MaxWidth(250)
                                                        .MaxHeight(250)
                                                        .Image(images[imageIndex].Data);
                                                    imageIndex++;
                                                }

                                                // Foto 4 della riga - DESTRA (allineata a sinistra)
                                                if (imageIndex < images.Count)
                                                {
                                                    row.RelativeColumn()
                                                        .Padding(5)
                                                        .AlignLeft()
                                                        .MaxWidth(250)
                                                        .MaxHeight(250)
                                                        .Image(images[imageIndex].Data);
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

        [HttpGet]
        public async Task<IActionResult> GeneratePhotographicReportWord([FromQuery] string fileId, [FromQuery] string title = null)
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

                // === CARICA TEMPLATE CON HEADER/FOOTER GIÀ CONFIGURATI ===
                var templatePath = Path.Combine(WebHostEnvironment.WebRootPath, "templates", "reportofotografici_template.docx");

                if (!System.IO.File.Exists(templatePath))
                    return BadRequest("Template file not found. Please ensure reportofotografici_template.docx exists in wwwroot/templates/");

                // Carica template in memoria
                using var memoryStream = new MemoryStream();
                using (var templateStream = System.IO.File.OpenRead(templatePath))
                {
                    await templateStream.CopyToAsync(memoryStream);
                }

                memoryStream.Position = 0;

                // Apri template e modifica il contenuto
                using (var wordDocument = WordprocessingDocument.Open(memoryStream, true))
                {
                    var mainPart = wordDocument.MainDocumentPart;
                    var body = mainPart.Document.Body;

                    // Trova e sostituisci il titolo "REPERTI FOTOGRAFICI" con quello personalizzato
                    var titleParagraph = body.Descendants<Paragraph>()
                        .FirstOrDefault(p => p.InnerText.Contains("REPERTI FOTOGRAFICI"));

                    if (titleParagraph != null)
                    {
                        // Sostituisci il testo del titolo esistente
                        var titleText = titleParagraph.Descendants<Text>().FirstOrDefault();
                        if (titleText != null)
                        {
                            titleText.Text = reportTitle;
                        }

                        // Spacer dopo il titolo
                        var spacerPara = new Paragraph();
                        titleParagraph.InsertAfterSelf(spacerPara);
                    }

                    // GRIGLIA FOTO 2x2 (inserisce dopo il titolo/data)
                    int imageIndex = 0;
                    while (imageIndex < images.Count)
                    {
                        // Crea tabella 2x2
                        var table = new Table();
                        var tableProps = table.AppendChild(new TableProperties());
                        tableProps.AppendChild(new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct });
                        tableProps.AppendChild(new TableJustification { Val = TableRowAlignmentValues.Center });

                        // Riga 1
                        var row1 = table.AppendChild(new TableRow());

                        // Foto 1
                        if (imageIndex < images.Count)
                        {
                            var cell1 = row1.AppendChild(new TableCell());
                            var cellPara1 = cell1.AppendChild(new Paragraph());
                            var cellParaProps1 = cellPara1.AppendChild(new ParagraphProperties());
                            cellParaProps1.AppendChild(new Justification { Val = JustificationValues.Right });
                            var cellRun1 = cellPara1.AppendChild(new Run());
                            AddImageToBody(mainPart, cellRun1, images[imageIndex].Data, 2200000, 2200000);
                            imageIndex++;
                        }

                        // Foto 2
                        if (imageIndex < images.Count)
                        {
                            var cell2 = row1.AppendChild(new TableCell());
                            var cellPara2 = cell2.AppendChild(new Paragraph());
                            var cellParaProps2 = cellPara2.AppendChild(new ParagraphProperties());
                            cellParaProps2.AppendChild(new Justification { Val = JustificationValues.Left });
                            var cellRun2 = cellPara2.AppendChild(new Run());
                            AddImageToBody(mainPart, cellRun2, images[imageIndex].Data, 2200000, 2200000);
                            imageIndex++;
                        }

                        // Riga 2
                        if (imageIndex < images.Count)
                        {
                            var row2 = table.AppendChild(new TableRow());

                            // Foto 3
                            if (imageIndex < images.Count)
                            {
                                var cell3 = row2.AppendChild(new TableCell());
                                var cellPara3 = cell3.AppendChild(new Paragraph());
                                var cellParaProps3 = cellPara3.AppendChild(new ParagraphProperties());
                                cellParaProps3.AppendChild(new Justification { Val = JustificationValues.Right });
                                var cellRun3 = cellPara3.AppendChild(new Run());
                                AddImageToBody(mainPart, cellRun3, images[imageIndex].Data, 2200000, 2200000);
                                imageIndex++;
                            }

                            // Foto 4
                            if (imageIndex < images.Count)
                            {
                                var cell4 = row2.AppendChild(new TableCell());
                                var cellPara4 = cell4.AppendChild(new Paragraph());
                                var cellParaProps4 = cellPara4.AppendChild(new ParagraphProperties());
                                cellParaProps4.AppendChild(new Justification { Val = JustificationValues.Left });
                                var cellRun4 = cellPara4.AppendChild(new Run());
                                AddImageToBody(mainPart, cellRun4, images[imageIndex].Data, 2200000, 2200000);
                                imageIndex++;
                            }
                        }

                        body.AppendChild(table);

                        // Page Break dopo ogni tabella (4 foto) se ci sono altre foto
                        if (imageIndex < images.Count)
                        {
                            var pageBreakPara = new Paragraph();
                            var pageBreakRun = pageBreakPara.AppendChild(new Run());
                            pageBreakRun.AppendChild(new Break { Type = BreakValues.Page });
                            body.AppendChild(pageBreakPara);
                        }
                    }

                    mainPart.Document.Save();
                }

                memoryStream.Seek(0, SeekOrigin.Begin);

                // Sanitizza il titolo per il nome file
                var sanitizedTitle = System.Text.RegularExpressions.Regex.Replace(
                    title ?? "REPERTI_FOTOGRAFICI", 
                    @"[^\w\-]", 
                    "_"
                );

                var fileName = $"{sanitizedTitle}_{fileId}_{DateTime.Now:yyyyMMdd_HHmmss}.docx";

                Response.Headers.Add("Content-Disposition", $"attachment; filename=\"{fileName}\"");

                return File(memoryStream.ToArray(), "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
            }
            catch (Exception ex)
            {
                return BadRequest($"Error: {ex.Message}");
            }
        }

        private void AddImageToBody(MainDocumentPart mainPart, Run run, string imagePath, long width, long height)
        {
            var imageBytes = System.IO.File.ReadAllBytes(imagePath);
            AddImageToBody(mainPart, run, imageBytes, width, height);
        }

        private void AddImageToBody(MainDocumentPart mainPart, Run run, byte[] imageBytes, long width, long height)
        {
            var imagePart = mainPart.AddImagePart(ImagePartType.Png);
            using (var stream = new MemoryStream(imageBytes))
            {
                imagePart.FeedData(stream);
            }

            var element = new Drawing(
                new DW.Inline(
                    new DW.Extent { Cx = width, Cy = height },
                    new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                    new DW.DocProperties { Id = 1U, Name = "Picture" },
                    new DW.NonVisualGraphicFrameDrawingProperties(
                        new A.GraphicFrameLocks { NoChangeAspect = true }),
                    new A.Graphic(
                        new A.GraphicData(
                            new PIC.Picture(
                                new PIC.NonVisualPictureProperties(
                                    new PIC.NonVisualDrawingProperties { Id = 0U, Name = "Image.png" },
                                    new PIC.NonVisualPictureDrawingProperties()),
                                new PIC.BlipFill(
                                    new A.Blip { Embed = mainPart.GetIdOfPart(imagePart) },
                                    new A.Stretch(new A.FillRectangle())),
                                new PIC.ShapeProperties(
                                    new A.Transform2D(
                                        new A.Offset { X = 0L, Y = 0L },
                                        new A.Extents { Cx = width, Cy = height }),
                                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }))
                        ) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })
                ) { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U }
            );

            run.AppendChild(element);
        }

        // Metodo generico per aggiungere immagini a HeaderPart o FooterPart
        private void AddImageToPart(OpenXmlPart part, Run run, string imagePath, long width, long height)
        {
            var imageBytes = System.IO.File.ReadAllBytes(imagePath);

            ImagePart imagePart;
            if (part is HeaderPart headerPart)
            {
                imagePart = headerPart.AddImagePart(ImagePartType.Png);
            }
            else if (part is FooterPart footerPart)
            {
                imagePart = footerPart.AddImagePart(ImagePartType.Png);
            }
            else
            {
                throw new InvalidOperationException("Part must be HeaderPart or FooterPart");
            }

            using (var stream = new MemoryStream(imageBytes))
            {
                imagePart.FeedData(stream);
            }

            var element = new Drawing(
                new DW.Inline(
                    new DW.Extent { Cx = width, Cy = height },
                    new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                    new DW.DocProperties { Id = 1U, Name = "Picture" },
                    new DW.NonVisualGraphicFrameDrawingProperties(
                        new A.GraphicFrameLocks { NoChangeAspect = true }),
                    new A.Graphic(
                        new A.GraphicData(
                            new PIC.Picture(
                                new PIC.NonVisualPictureProperties(
                                    new PIC.NonVisualDrawingProperties { Id = 0U, Name = "Image.png" },
                                    new PIC.NonVisualPictureDrawingProperties()),
                                new PIC.BlipFill(
                                    new A.Blip { Embed = part.GetIdOfPart(imagePart) },
                                    new A.Stretch(new A.FillRectangle())),
                                new PIC.ShapeProperties(
                                    new A.Transform2D(
                                        new A.Offset { X = 0L, Y = 0L },
                                        new A.Extents { Cx = width, Cy = height }),
                                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }))
                        ) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })
                ) { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U }
            );

            run.AppendChild(element);
        }

        // Overload che mantiene l'aspect ratio - specifica solo la larghezza
        private void AddImageToPartWithAspectRatio(OpenXmlPart part, Run run, string imagePath, long maxWidth)
        {
            var imageBytes = System.IO.File.ReadAllBytes(imagePath);

            // Carica l'immagine per ottenere le dimensioni reali
            using var ms = new MemoryStream(imageBytes);
            using var image = System.Drawing.Image.FromStream(ms);

            // Calcola aspect ratio
            double aspectRatio = (double)image.Height / image.Width;

            // Calcola altezza proporzionale
            long calculatedHeight = (long)(maxWidth * aspectRatio);

            // Usa il metodo esistente con dimensioni calcolate
            ImagePart imagePart;
            if (part is HeaderPart headerPart)
            {
                imagePart = headerPart.AddImagePart(ImagePartType.Png);
            }
            else if (part is FooterPart footerPart)
            {
                imagePart = footerPart.AddImagePart(ImagePartType.Png);
            }
            else
            {
                throw new InvalidOperationException("Part must be HeaderPart or FooterPart");
            }

            using (var stream = new MemoryStream(imageBytes))
            {
                imagePart.FeedData(stream);
            }

            var element = new Drawing(
                new DW.Inline(
                    new DW.Extent { Cx = maxWidth, Cy = calculatedHeight },
                    new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                    new DW.DocProperties { Id = 1U, Name = "Picture" },
                    new DW.NonVisualGraphicFrameDrawingProperties(
                        new A.GraphicFrameLocks { NoChangeAspect = true }),
                    new A.Graphic(
                        new A.GraphicData(
                            new PIC.Picture(
                                new PIC.NonVisualPictureProperties(
                                    new PIC.NonVisualDrawingProperties { Id = 0U, Name = "Image.png" },
                                    new PIC.NonVisualPictureDrawingProperties()),
                                new PIC.BlipFill(
                                    new A.Blip { Embed = part.GetIdOfPart(imagePart) },
                                    new A.Stretch(new A.FillRectangle())),
                                new PIC.ShapeProperties(
                                    new A.Transform2D(
                                        new A.Offset { X = 0L, Y = 0L },
                                        new A.Extents { Cx = maxWidth, Cy = calculatedHeight }),
                                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }))
                        ) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })
                ) { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U }
            );

            run.AppendChild(element);
        }
    }
}