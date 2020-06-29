using Microsoft.AspNetCore.Http;
using Rystem.UnitTest;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Expertiser.Functionality
{
    public class DefaultImportAmissima : IUnitTest
    {
        private const string FilePath = @"C:\Users\aless\Downloads\incarico.xml";
        public async Task DoWorkAsync(Action<object> action, UnitTestMetrics metrics, params string[] args)
        {
            try
            {
                var importer = new ImporterFactory().Create(ImporterType.Amissima);
                string sdj = System.IO.File.ReadAllText(FilePath);
                XDocument xml = XDocument.Parse(sdj);
                var t = importer.Import(new MemoryStream(Encoding.UTF8.GetBytes(sdj)));
                System.IO.File.WriteAllBytes(@"C:\Users\aless\Downloads\incarico.pdf", t.Files.FirstOrDefault().Content);
            }
            catch (Exception e)
            {
                string alo = e.ToString();
            }
        }
    }
}
