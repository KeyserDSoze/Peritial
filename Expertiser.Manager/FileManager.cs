using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Expertiser.Database.Models;
using Expertiser.Interfaces;
using Microsoft.AspNetCore.Http;
using Rystem.Data;
using Rystem.Web.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Expertiser
{
    public class FileManager
    {
        private readonly Manager Manager;
        public FileManager(Manager manager)
            => this.Manager = manager;
        public async Task<IList<string>> GetUriFiles(string fileId, string name = null)
            => await new File().SearchAsync($"{fileId}/{name}").NoContext();
        private static readonly IEnumerable<FileModel> StandardFolder =
        new List<FileModel>()
        {
            new FileModel("folder", "/////AccidentDocs/", DateTime.UtcNow),
            new FileModel("folder", "/////Photos/", DateTime.UtcNow),
            new FileModel("folder", "/////RawExpertise/", DateTime.UtcNow),
            new FileModel("folder", "/////CorrectedExpertise/", DateTime.UtcNow),
            new FileModel("folder", "/////CompletedExpertise/", DateTime.UtcNow),
            new FileModel("folder", "/////ReservedNote/", DateTime.UtcNow),
        };
        public StatusType GetMinimumStatus(string directory)
        {
            return directory.ToLower() switch
            {
                "rawexpertise" => StatusType.ToBeRevised,
                "correctedexpertise" => StatusType.Revised,
                "completedexpertise" => StatusType.ToSend,
                _ => StatusType.Registered
            };
        }
        public async Task<IEnumerable<FileModel>> GetExpertiseFiles(string fileId)
        {
            var files = (await GetUriFiles(fileId).NoContext()).Select(x => new FileModel(x, DateTime.UtcNow));
            return StandardFolder.Concat(files);
        }

        public async Task DeleteFileAsync(string name)
        {
            await new File()
            {
                Name = name
            }.DeleteAsync();
        }
        public async Task<IList<string>> SaveFileAsync(string fileId, Stream stream, string fileName, string contentType, string name = null)
        {
            if (name == null)
                name = Guid.NewGuid().ToString("N");
            else if (!name.Contains("."))
                name = $"{name}/{(!string.IsNullOrWhiteSpace(fileName) ? fileName.Split('.').FirstOrDefault() : Guid.NewGuid().ToString("N"))}";
            var upload = await new File()
            {
                Name = $"{fileId}/{name}.{fileName.Split('.').Last()}",
                Properties = new DataProperties()
                {
                    ContentType = contentType
                },
                Stream = stream
            }.WriteAsync();
            return await this.GetUriFiles(fileId);
        }
        public async Task<bool> SaveTemplateAsync(int companyId, string name, string contentType, Stream stream)
        {
            var upload = await new Template()
            {
                Name = $"{companyId}/{name}",
                Properties = new DataProperties()
                {
                    ContentType = contentType
                },
                Stream = stream
            }.WriteAsync();
            return upload;
        }
        public async Task<IList<string>> GetUriForTemplateFiles(int companyId, string name = null)
            => await new Template().SearchAsync($"{companyId}/{name}").NoContext();
        public async Task<IEnumerable<FileModel>> GetTemplateFiles(int companyId)
        {
            var files = (await GetUriForTemplateFiles(companyId).NoContext()).Select(x => new FileModel(x, DateTime.UtcNow));
            return files;
        }
        public async Task DeleteTemplateAsync(string name)
        {
            await new Template()
            {
                Name = name
            }.DeleteAsync();
        }
        public async Task<Stream> CreateFinalDocumentationFromTemplateAsync(TableExpertise expertise, string name)
        {
            var template = await new Template()
            {
                Name = $"{expertise.Accident.InsuranceAgency.InsuranceCompany.Id}/{name}",
            }.FetchAsync();
            using (WordprocessingDocument wordDocument =
                WordprocessingDocument.Open(template.Stream, true))
            {
                foreach (var element in wordDocument.MainDocumentPart.Document.Elements())
                {
                    ChangeElement(element);
                }
                wordDocument.MainDocumentPart.Document.Save();
            }
            template.Stream.Position = 0;
            return template.Stream;

            void ChangeElement(OpenXmlElement openXmlElement)
            {
                if (openXmlElement.HasChildren)
                {
                    foreach (var element in openXmlElement.Elements())
                    {
                        if (element is Text)
                        {
                            string text = (element as Text).Text;
                            foreach (var replacer in Replacer.All.Where(x => text.Contains(x.Variable)))
                            {
                                var value = replacer.ValueRetriever(expertise);
                                if (value != null)
                                    (element as Text).Text = text.Replace(replacer.Variable, value.ToString());
                            }
                        }
                        else
                            ChangeElement(element);
                    }
                }
            }
        }
        private sealed class Replacer
        {
            public string Variable { get; }
            public Func<TableExpertise, object> ValueRetriever { get; }
            private Replacer(string variable, Func<TableExpertise, object> valueRetriever)
            {
                Variable = $"{variable}";
                ValueRetriever = valueRetriever;
            }
            public static readonly List<Replacer> All = new List<Replacer>()
            {
                new Replacer("NumeroSinistro", x => x.Accident.ExternalId),
                new Replacer("DataSinistro", x => x.Accident.AccidentDate),
                new Replacer("Indirizzo", x => $"{x.Accident?.Damageds?.FirstOrDefault()?.Address} {x.Accident?.Address?.City} {x.Accident?.Address?.PostalCode}"),
                new Replacer("Contraente", x => $"{x.Accident?.Insurance?.Contractor?.Name}"),
                new Replacer("CodiceFiscale", x => $"{x.Accident?.Insurance?.Contractor?.FiscalCode}"),
                new Replacer("Danneggiati", x => $"{string.Join(", ", x.Accident?.TableDamaged?.Select(x => x.Customer.Name))}"),
                new Replacer("NumeroPolizza", x => $"{x.Accident?.Insurance?.ExternalId}"),
                new Replacer("DataIncarico", x => $"{x.Accident?.TableExpertise?.LastOrDefault()?.AssignmentDate}"),
                new Replacer("DataChiusura", x => $"{x.Accident?.TableExpertise?.LastOrDefault()?.CloseDate}"),
                new Replacer("Perito", x => $"{x.Accident?.TableExpertise?.LastOrDefault()?.Contributor?.Name}"),
                new Replacer("Liquidatore", x => $"{x.Accident?.InsuranceAdjuster?.Name}"),
                new Replacer("Ispettorato", x => $"{x.Accident?.Inspectorate?.Label}"),
                new Replacer("Ramo", x => $"{x.Accident?.TableExpertise?.LastOrDefault()?.Branch?.Label}"),
                new Replacer("Liquidato", x => $"{x.Accident?.TableExpertise?.LastOrDefault()?.Liquidated}"),
            };
        }
    }
}