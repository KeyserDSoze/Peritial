using Expertiser.Database.Models;
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
        public ImportedExperty Import(Stream content)
        {
            var amissimaXml = (AmissimaXml)new XmlSerializer(typeof(AmissimaXml)).Deserialize(content);
            return new ImportedExperty()
            {
                Accident = new TableAccident()
                {
                    AccidentDate = DateTime.ParseExact(amissimaXml.AccidentDate, "yyyy-MM-dd hh:mm:ss", CultureInfo.InvariantCulture),
                    DenunciationDate = DateTime.ParseExact(amissimaXml.DenunciationDate, "yyyy-MM-dd hh:mm:ss", CultureInfo.InvariantCulture),
                    ExternalId = amissimaXml.Id
                },
                Expertise = new TableExpertise()
                {
                    Status = -1,
                    BranchId = 1
                },
                InsuranceAdjuster = new TableInsuranceAdjuster()
                {
                    Name = string.Join(" ", amissimaXml.InsuranceAdjuster.Split('-').Select(x => x.Trim().Substring(0, 1).ToUpper() + x.Trim().Substring(1).ToLower()).Reverse())
                },
                Files = amissimaXml.Files.Select(x => new ImportedFile() { Name = x.Name, Content = Convert.FromBase64String(x.Value), ContentType = "application/pdf" }),
                Insurance = new TableInsurance()
                {
                    InsuranceCompanyId = 33,
                    ExternalId = amissimaXml.Customer.Insurance.Id,
                },
                Location = new TableLocation()
                {
                    PostalCode = amissimaXml.Customer.Address.PostalCode,
                    Address = $"{amissimaXml.Customer.Address.Address} {amissimaXml.Customer.Address.CivicNumber}".Trim()
                },
                Customer = new TableCustomer()
                {
                    Name = $"{amissimaXml.Customer.Name} {amissimaXml.Customer.Surname}".Trim(),
                    FiscalCode = !string.IsNullOrWhiteSpace(amissimaXml.Customer.FiscalCode) ? amissimaXml.Customer.FiscalCode : amissimaXml.Customer.PIVA,
                }
            };
        }

        [Serializable, XmlRoot("it.autosoftws.ReIncaricoRecord")]
        public class AmissimaXml
        {
            [XmlElement(ElementName = "IDIncarico")]
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
            [XmlElement(ElementName = "Polizza")]
            public AmissimaInsurance Insurance { get; set; }
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