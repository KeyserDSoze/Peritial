using Expertiser.Database.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace Expertiser
{
    public class ImportedExperty
    {
        public TableExpertise Expertise { get; set; }
        public TableLocation Location { get; set; }
        public TableAccident Accident { get; set; }
        public TableInsurance Insurance { get; set; }
        public TableCustomer Customer { get; set; }
        public TableCustomer Contractor { get; set; }
        public TableInsuranceAdjuster InsuranceAdjuster { get; set; }
        public TableInsuranceCompany InsuranceCompany { get; set; }
        public TableInspectorate Inspectorate { get; set; }
        public TableInsuranceAgency InsuranceAgency { get; set; }
        public IEnumerable<ImportedFile> Files { get; set; }
        public IEnumerable<TableCustomer> Damageds { get; set; }
    }
}