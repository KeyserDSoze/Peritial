using Expertiser.Database.Models;
using Microsoft.Azure.Documents.SystemFunctions;
using Microsoft.EntityFrameworkCore;
using Microsoft.OData.Edm;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace Expertiser
{
    public class Amissima : IImporter
    {
        public ImportedExperty Import(Stream content, Manager manager)
        {
            var tableInsuranceCompany = manager.Context.TableInsuranceCompany.FirstOrDefault(x => x.Label.ToLower().Contains("amissima"));
            var amissimaXml = (AmissimaXml)new XmlSerializer(typeof(AmissimaXml)).Deserialize(content);
            var importedExperty = new ImportedExperty()
            {
                Accident = new TableAccident()
                {
                    AccidentDate = DateTime.ParseExact(amissimaXml.AccidentDate, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    DenunciationDate = DateTime.ParseExact(amissimaXml.DenunciationDate, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    ExternalId = amissimaXml.Id
                },
                Expertise = new TableExpertise()
                {
                    Status = -1,
                    BranchId = 1
                },
                InsuranceAdjuster = new TableInsuranceAdjuster()
                {
                    Name = string.Join(" ", amissimaXml.InsuranceAdjuster.Split('-').SelectMany(x => x.Trim().Split(' ')).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Substring(0, 1).ToUpper() + x.Substring(1).ToLower()).Reverse())
                },
                Files = amissimaXml.Files.Select(x => new ImportedFile() { Name = x.Name.Split('.')[0], Content = Convert.FromBase64String(x.Value), ContentType = "application/pdf" }),
                Insurance = new TableInsurance()
                {
                    InsuranceCompanyId = tableInsuranceCompany.Id,
                    ExternalId = amissimaXml.Customer.Insurance.Id,
                },
                Location = new TableLocation()
                {
                },
                Customer = new TableCustomer()
                {
                    Name = $"{amissimaXml.Customer.Name} {amissimaXml.Customer.Surname}".Trim(),
                    FiscalCode = !string.IsNullOrWhiteSpace(amissimaXml.Customer.FiscalCode) ? amissimaXml.Customer.FiscalCode : amissimaXml.Customer.PIVA,
                    Address = new TableLocation()
                    {
                        Address = $"{amissimaXml.Customer.Address.Address} {amissimaXml.Customer.Address.CivicNumber}",
                        PostalCode = amissimaXml.Customer.Address.PostalCode,
                    }
                },
                Contractor = new TableCustomer()
                {
                    Name = $"{amissimaXml.Customer.Name} {amissimaXml.Customer.Surname}".Trim(),
                    FiscalCode = !string.IsNullOrWhiteSpace(amissimaXml.Customer.FiscalCode) ? amissimaXml.Customer.FiscalCode : amissimaXml.Customer.PIVA,
                    Address = new TableLocation()
                    {
                        Address = $"{amissimaXml.Customer.Address.Address} {amissimaXml.Customer.Address.CivicNumber}",
                        PostalCode = amissimaXml.Customer.Address.PostalCode,
                    }
                },
                Damageds = amissimaXml.Damageds.Select(x => new TableCustomer()
                {
                    Name = $"{x.Name} {x.Surname}".Trim(),
                    FiscalCode = !string.IsNullOrWhiteSpace(x.FiscalCode) ? x.FiscalCode : x.PIVA,
                    Address = new TableLocation()
                    {
                        Address = $"{x.Residence.Address} {x.Residence.CivicNumber}",
                        PostalCode = x.Residence.PostalCode,
                    }
                }),
                Inspectorate = new TableInspectorate()
                {
                    ExternalId = $"{amissimaXml.Customer.Company.CompanyName} - {amissimaXml.Customer.Company.InsuranceInspectorateCode}"
                },
                InsuranceCompany = tableInsuranceCompany,
                InsuranceAgency = new TableInsuranceAgency
                {
                    ExternalId = $"{amissimaXml.Customer.Company.CompanyName} - {amissimaXml.Customer.Company.InsuranceAgencyCode}"
                }
            };
            foreach (var damaged in importedExperty.Damageds)
            {
                if (damaged.Name == importedExperty.Customer.Name)
                    importedExperty.Customer.Address = damaged.Address;
                if (damaged.Name == importedExperty.Contractor.Name)
                    importedExperty.Contractor.Address = damaged.Address;
            }
            return importedExperty;
        }

        [Serializable, XmlRoot("it.autosoftws.ReIncaricoRecord")]
        public class AmissimaXml
        {
            [XmlElement(ElementName = "NumeroSinistro")]
            public string Id { get; set; }
            [XmlElement(ElementName = "DataIncarico")]
            public string AccidentDate { get; set; }
            [XmlElement(ElementName = "DataOraSinistro")]
            public string DenunciationDate { get; set; }
            [XmlElement(ElementName = "NomeLiquidatore")]
            public string InsuranceAdjuster { get; set; }
            [XmlElement(ElementName = "Contraente")]
            public AmissimaCustomer Customer { get; set; }
            [XmlArray("allegati")]
            [XmlArrayItem("it.autosoftws.AllegatoRecord", typeof(AmissimaFile))]
            public AmissimaFile[] Files { get; set; }
            [XmlArray("Danneggiati")]
            [XmlArrayItem("it.autosoftws.DatiDanneggitoRecord", typeof(AmissimaCustomer))]
            public AmissimaCustomer[] Damageds { get; set; }
        }
        public class AmissimaCustomer
        {
            [XmlElement(ElementName = "Nome")]
            public string Name { get; set; }
            [XmlElement(ElementName = "Cognome")]
            public string Surname { get; set; }
            [XmlElement(ElementName = "CodiceFiscale")]
            public string FiscalCode { get; set; }
            [XmlElement(ElementName = "PartitaIVA")]
            public string PIVA { get; set; }
            [XmlElement(ElementName = "indirizzo")]
            public AmissimaAddress Address { get; set; }
            [XmlElement(ElementName = "Compagnia")]
            public AmissimaCompany Company { get; set; }
            [XmlElement(ElementName = "residenza")]
            public AmissimaAddress Residence { get; set; }
            [XmlElement(ElementName = "Polizza")]
            public AmissimaInsurance Insurance { get; set; }
        }
        public class AmissimaCompany
        {
            [XmlElement(ElementName = "CodiceCompagnia")]
            public string InsuranceAgencyCode { get; set; }
            [XmlElement(ElementName = "CodiceIspettorato")]
            public string InsuranceInspectorateCode { get; set; }
            [XmlElement(ElementName = "NomeCompagnia")]
            public string CompanyName { get; set; }
        }
        public class AmissimaInsurance
        {
            [XmlElement(ElementName = "Numero")]
            public string Id { get; set; }
        }
        public class AmissimaAddress
        {
            [XmlElement(ElementName = "Cap")]
            public string PostalCode { get; set; }
            [XmlElement(ElementName = "Nazione")]
            public string Country { get; set; }
            [XmlElement(ElementName = "Indirizzo")]
            public string Address { get; set; }
            [XmlElement(ElementName = "NumeroCivico")]
            public string CivicNumber { get; set; }
        }
        public class AmissimaFile
        {
            [XmlElement(ElementName = "Nome")]
            public string Name { get; set; }
            [XmlElement(ElementName = "Descrizione")]
            public string Description { get; set; }
            [XmlElement(ElementName = "IDTipoFile")]
            public string FileType { get; set; }
            [XmlElement(ElementName = "Allegato")]
            public string Value { get; set; }
        }
    }
}