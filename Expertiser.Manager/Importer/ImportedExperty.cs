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
        public TableInsuranceAdjuster InsuranceAdjuster { get; set; }
        public IEnumerable<ImportedFile> Files { get; set; }
    }
}