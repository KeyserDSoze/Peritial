using Expertiser.Database.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Expertiser
{
    public interface IImporter
    {
        ImportedExperty Import(string file);
    }
    public class ImportedExperty
    {
        public TableExpertise Expertise { get; set; }
        public TableLocation Location { get; set; }
        public TableAccident Accident { get; set; }
        public TableInsurance Insurance { get; set; }
        public List<ImportedFile> Files { get; set; }
    }
    public class ImportedFile
    {
        public string Name { get; set; }
        public byte[] Content { get; set; }
    }
}