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
    public class ClassicXml : IImporter
    {
        private IBaseXml GetBaseXml(Stream content)
        {
            try
            {
                return (BaseXml)new XmlSerializer(typeof(BaseXml)).Deserialize(content);
            }
            catch
            {
                content.Position = 0;
                return (BaseBaseXml)new XmlSerializer(typeof(BaseBaseXml)).Deserialize(content);
            }
        }
        private DateTime ParseTime(string value)
        {
            try
            {
                return DateTime.ParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch
            {
                return DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }
        public ImportedExperty Import(Stream content, Manager manager)
        {
            var classicXml = GetBaseXml(content);
            string companyName = classicXml.Customer.Company.CompanyName.Split(' ').FirstOrDefault().ToLower();
            var tableInsuranceCompany = manager.Context.TableInsuranceCompany.FirstOrDefault(x => x.Label.ToLower().Contains(companyName));
            var importedExperty = new ImportedExperty()
            {
                Accident = new TableAccident()
                {
                    AccidentDate = ParseTime(classicXml.AccidentDate),
                    DenunciationDate = ParseTime(classicXml.DenunciationDate),
                    ExternalId = classicXml.Id
                },
                Expertise = new TableExpertise()
                {
                    Status = -1,
                    BranchId = 1
                },
                InsuranceAdjuster = new TableInsuranceAdjuster()
                {
                    Name = string.Join(" ", classicXml.InsuranceAdjuster.Split('-').SelectMany(x => x.Trim().Split(' ')).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Substring(0, 1).ToUpper() + x.Substring(1).ToLower()).Reverse())
                },
                Files = classicXml.Files?.Select(x => new ImportedFile() { Name = x.Name, Content = Convert.FromBase64String(x.Value), ContentType = "application/pdf" }) ?? new List<ImportedFile>(),
                Insurance = new TableInsurance()
                {
                    InsuranceCompanyId = tableInsuranceCompany.Id,
                    ExternalId = classicXml.Customer.Insurance.Id,
                },
                Location = new TableLocation()
                {
                },
                Customer = new TableCustomer()
                {
                    Name = $"{classicXml.Customer.Name} {classicXml.Customer.Surname}".Replace("-", string.Empty).Trim(),
                    FiscalCode = !string.IsNullOrWhiteSpace(classicXml.Customer.FiscalCode) ? classicXml.Customer.FiscalCode : classicXml.Customer.PIVA,
                    Address = new TableLocation()
                    {
                        Address = $"{classicXml.Customer.Address.Address} {classicXml.Customer.Address.CivicNumber}",
                        PostalCode = classicXml.Customer.Address.PostalCode,
                    }
                },
                Contractor = new TableCustomer()
                {
                    Name = $"{classicXml.Customer.Name} {classicXml.Customer.Surname}".Replace("-", string.Empty).Trim(),
                    FiscalCode = !string.IsNullOrWhiteSpace(classicXml.Customer.FiscalCode) ? classicXml.Customer.FiscalCode : classicXml.Customer.PIVA,
                    Address = new TableLocation()
                    {
                        Address = $"{classicXml.Customer.Address.Address} {classicXml.Customer.Address.CivicNumber}",
                        PostalCode = classicXml.Customer.Address.PostalCode,
                    }
                },
                Damageds = classicXml.Damageds.Select(x => new TableCustomer()
                {
                    Name = $"{x.Name} {x.Surname}".Replace("-", string.Empty).Trim(),
                    FiscalCode = !string.IsNullOrWhiteSpace(x.FiscalCode) ? x.FiscalCode : x.PIVA,
                    Address = new TableLocation()
                    {
                        Address = $"{x.Residence?.Address} {x.Residence?.CivicNumber}".Trim(),
                        PostalCode = x.Residence?.PostalCode,
                    }
                }),
                Inspectorate = new TableInspectorate()
                {
                    ExternalId = $"{classicXml.Customer.Company.CompanyName} - {classicXml.Customer.Company.InsuranceInspectorateCode}"
                },
                InsuranceCompany = tableInsuranceCompany,
                InsuranceAgency = new TableInsuranceAgency
                {
                    ExternalId = $"{classicXml.Customer.Company.CompanyName} - {classicXml.Customer.Company.InsuranceAgencyCode}"
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
        public interface IBaseXml
        {
            string Id { get; set; }
            string AccidentDate { get; set; }
            string DenunciationDate { get; set; }
            string InsuranceAdjuster { get; set; }
            ClassicCustomer Customer { get; set; }
            ClassicFile[] Files { get; set; }
            ClassicCustomer[] Damageds { get; set; }
        }
        [Serializable, XmlRoot("it.autosoftws.ReIncaricoBaseRecord")]
        public class BaseBaseXml : IBaseXml
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
            public ClassicCustomer Customer { get; set; }
            [XmlArray("allegati")]
            [XmlArrayItem("it.autosoftws.AllegatoRecord", typeof(ClassicFile))]
            public ClassicFile[] Files { get; set; }
            [XmlArray("Danneggiati")]
            [XmlArrayItem("it.autosoftws.DatiDanneggitoRecord", typeof(ClassicCustomer))]
            public ClassicCustomer[] Damageds { get; set; }
        }
        [Serializable, XmlRoot("it.autosoftws.ReIncaricoRecord")]
        public class BaseXml : IBaseXml
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
            public ClassicCustomer Customer { get; set; }
            [XmlArray("allegati")]
            [XmlArrayItem("it.autosoftws.AllegatoRecord", typeof(ClassicFile))]
            public ClassicFile[] Files { get; set; }
            [XmlArray("Danneggiati")]
            [XmlArrayItem("it.autosoftws.DatiDanneggitoRecord", typeof(ClassicCustomer))]
            public ClassicCustomer[] Damageds { get; set; }
        }
        public class ClassicCustomer
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
            public ClassicAddress Address { get; set; }
            [XmlElement(ElementName = "Compagnia")]
            public ClassicCompany Company { get; set; }
            [XmlElement(ElementName = "residenza")]
            public ClassicAddress Residence { get; set; }
            [XmlElement(ElementName = "Polizza")]
            public ClassicInsurance Insurance { get; set; }
        }
        public class ClassicCompany
        {
            [XmlElement(ElementName = "CodiceCompagnia")]
            public string InsuranceAgencyCode { get; set; }
            [XmlElement(ElementName = "CodiceIspettorato")]
            public string InsuranceInspectorateCode { get; set; }
            [XmlElement(ElementName = "NomeCompagnia")]
            public string CompanyName { get; set; }
        }
        public class ClassicInsurance
        {
            [XmlElement(ElementName = "Numero")]
            public string Id { get; set; }
        }
        public class ClassicAddress
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
        public class ClassicFile
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