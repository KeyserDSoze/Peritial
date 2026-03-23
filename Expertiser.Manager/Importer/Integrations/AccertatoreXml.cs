using Expertiser.Database.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace Expertiser
{
    public class AccertatoreXml : IImporter
    {
        public const string RootName = "it.autosoftws.IncaricoAccertatoreRecord";

        public ImportedExperty Import(Stream content, Manager manager)
        {
            var xml = (AccertatoreRecord)new XmlSerializer(typeof(AccertatoreRecord)).Deserialize(content);
            var company = xml.Company ?? xml.Contractor?.Company;
            if (company == null)
                throw new InvalidDataException("Il file XML non contiene la compagnia assicurativa.");

            var tableInsuranceCompany = FindInsuranceCompany(manager, company.CompanyName, company.InsuranceAgencyCode);
            if (tableInsuranceCompany == null)
                throw new InvalidDataException($"Compagnia assicurativa non trovata: '{company.CompanyName}'.");
            string resolvedCompanyName = CleanValue(company.CompanyName) ?? tableInsuranceCompany.Label;

            var insured = xml.Customer ?? xml.Contractor;
            if (insured == null)
                throw new InvalidDataException("Il file XML non contiene il nodo assicurato o contraente.");

            var contractor = xml.Contractor ?? insured;
            if (xml.Insurance == null)
                throw new InvalidDataException("Il file XML non contiene il nodo Polizza.");

            var importedExperty = new ImportedExperty()
            {
                Accident = new TableAccident()
                {
                    AccidentDate = ParseRequiredTime(xml.AccidentDate, "DataOraSinistro"),
                    DenunciationDate = ParseNullableTime(xml.DenunciationDate),
                    ExternalId = RequireValue(xml.Id, "NumeroSinistro")
                },
                Expertise = new TableExpertise()
                {
                    Status = -1,
                    BranchId = 1
                },
                InsuranceAdjuster = new TableInsuranceAdjuster()
                {
                    Name = NormalizeAdjusterName(xml.InsuranceAdjuster, xml.InsuranceAdjusterCode)
                },
                Files = BuildFiles(xml.Files),
                Insurance = new TableInsurance()
                {
                    InsuranceCompanyId = tableInsuranceCompany.Id,
                    ExternalId = RequireValue(xml.Insurance.Id, "Polizza/Numero"),
                    AgencyCode = CleanValue(xml.Insurance.AgencyCode),
                    StartCoverage = ParseNullableTime(xml.Insurance.StartCoverage),
                    EndCoverage = ParseNullableTime(xml.Insurance.EndCoverage)
                },
                Location = new TableLocation()
                {
                },
                Customer = BuildCustomer(insured, useResidence: false),
                Contractor = BuildCustomer(contractor, useResidence: false),
                Damageds = (xml.Damageds ?? Array.Empty<ClassicXml.ClassicCustomer>()).Select(x => BuildCustomer(x, useResidence: true)).ToArray(),
                Inspectorate = new TableInspectorate()
                {
                    ExternalId = $"{resolvedCompanyName} - {company.InsuranceInspectorateCode}"
                },
                InsuranceCompany = tableInsuranceCompany,
                InsuranceAgency = new TableInsuranceAgency
                {
                    ExternalId = $"{resolvedCompanyName} - {company.InsuranceAgencyCode}"
                }
            };

            foreach (var damaged in importedExperty.Damageds)
            {
                if (damaged.Name == importedExperty.Customer.Name && HasAddress(damaged.Address))
                    importedExperty.Customer.Address = damaged.Address;
                if (damaged.Name == importedExperty.Contractor.Name && HasAddress(damaged.Address))
                    importedExperty.Contractor.Address = damaged.Address;
            }

            return importedExperty;
        }

        private static IEnumerable<ImportedFile> BuildFiles(IEnumerable<ClassicXml.ClassicFile> files)
            => (files ?? Array.Empty<ClassicXml.ClassicFile>())
                .Where(x => !string.IsNullOrWhiteSpace(x?.Value))
                .Select((x, index) => new ImportedFile()
                {
                    Name = !string.IsNullOrWhiteSpace(x.Name) ? x.Name : $"allegato-{index + 1}.pdf",
                    Content = Convert.FromBase64String(x.Value),
                    ContentType = "application/pdf"
                })
                .ToArray();

        private static TableInsuranceCompany FindInsuranceCompany(Manager manager, string companyName, string companyCode)
        {
            string normalizedCompanyName = CleanValue(companyName)?.ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(normalizedCompanyName))
            {
                var exactMatch = manager.Context.TableInsuranceCompany.FirstOrDefault(x => x.Label.ToLower() == normalizedCompanyName);
                if (exactMatch != null)
                    return exactMatch;

                var containsMatch = manager.Context.TableInsuranceCompany.FirstOrDefault(x => x.Label.ToLower().Contains(normalizedCompanyName));
                if (containsMatch != null)
                    return containsMatch;

                string firstWord = normalizedCompanyName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstWord))
                {
                    var firstWordMatch = manager.Context.TableInsuranceCompany.FirstOrDefault(x => x.Label.ToLower().Contains(firstWord));
                    if (firstWordMatch != null)
                        return firstWordMatch;
                }
            }

            if (!string.IsNullOrWhiteSpace(companyCode))
                return manager.Context.TableInsuranceCompany.FirstOrDefault(x => x.ExternalId == companyCode);

            return null;
        }

        private static TableCustomer BuildCustomer(ClassicXml.ClassicCustomer source, bool useResidence)
        {
            source ??= new ClassicXml.ClassicCustomer();
            var addressSource = useResidence ? source.Residence : source.Address;
            var name = CleanValue($"{source.Name} {source.Surname}".Replace("-", string.Empty));
            if (string.IsNullOrWhiteSpace(name))
                name = "Sconosciuto";

            return new TableCustomer()
            {
                Name = name,
                FiscalCode = CleanValue(!string.IsNullOrWhiteSpace(source.FiscalCode) ? source.FiscalCode : source.PIVA),
                Address = BuildLocation(addressSource)
            };
        }

        private static TableLocation BuildLocation(ClassicXml.ClassicAddress source)
        {
            string address = CleanValue($"{source?.Address} {source?.CivicNumber}");
            return new TableLocation()
            {
                Address = address ?? string.Empty,
                PostalCode = CleanValue(source?.PostalCode) ?? "N/A",
            };
        }

        private static string NormalizeAdjusterName(string fullName, string fallbackCode)
        {
            string value = CleanValue(fullName);
            if (string.IsNullOrWhiteSpace(value))
                value = CleanValue(fallbackCode);
            if (string.IsNullOrWhiteSpace(value))
                return "Liquidatore sconosciuto";

            return string.Join(" ", value.Split('-')
                .SelectMany(x => x.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Substring(0, 1).ToUpperInvariant() + x.Substring(1).ToLowerInvariant())
                .Reverse());
        }

        private static DateTime ParseRequiredTime(string value, string fieldName)
        {
            if (TryParseTime(value, out var result))
                return result;
            throw new InvalidDataException($"Il campo '{fieldName}' non contiene una data valida.");
        }

        private static DateTime? ParseNullableTime(string value)
            => TryParseTime(value, out var result) ? result : null;

        private static bool TryParseTime(string value, out DateTime result)
        {
            value = CleanValue(value);
            if (string.IsNullOrWhiteSpace(value))
            {
                result = default;
                return false;
            }

            if (DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
                return true;
            if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
                return true;

            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
        }

        private static bool HasAddress(TableLocation address)
            => address != null && (!string.IsNullOrWhiteSpace(address.Address) || !string.IsNullOrWhiteSpace(address.PostalCode));

        private static string CleanValue(string value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string RequireValue(string value, string fieldName)
            => CleanValue(value) ?? throw new InvalidDataException($"Il campo '{fieldName}' e obbligatorio.");

        [Serializable, XmlRoot(RootName)]
        public class AccertatoreRecord
        {
            [XmlElement(ElementName = "NumeroSinistro")]
            public string Id { get; set; }

            [XmlElement(ElementName = "DataOraSinistro")]
            public string AccidentDate { get; set; }

            [XmlElement(ElementName = "DataIncarico")]
            public string DenunciationDate { get; set; }

            [XmlElement(ElementName = "CodiceLiquidatore")]
            public string InsuranceAdjusterCode { get; set; }

            [XmlElement(ElementName = "NomeLiquidatore")]
            public string InsuranceAdjuster { get; set; }

            [XmlElement(ElementName = "Compagnia")]
            public ClassicXml.ClassicCompany Company { get; set; }

            [XmlElement(ElementName = "assicurato")]
            public ClassicXml.ClassicCustomer Customer { get; set; }

            [XmlElement(ElementName = "Contraente")]
            public ClassicXml.ClassicCustomer Contractor { get; set; }

            [XmlElement(ElementName = "Polizza")]
            public ClassicXml.ClassicInsurance Insurance { get; set; }

            [XmlArray("allegati")]
            [XmlArrayItem("it.autosoftws.AllegatoRecord", typeof(ClassicXml.ClassicFile))]
            public ClassicXml.ClassicFile[] Files { get; set; }

            [XmlArray("Danneggiati")]
            [XmlArrayItem("it.autosoftws.DatiDanneggitoRecord", typeof(ClassicXml.ClassicCustomer))]
            public ClassicXml.ClassicCustomer[] Damageds { get; set; }
        }
    }
}
