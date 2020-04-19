using Expertiser.Database.Models;
using Rystem.Interfaces.Utility.Tester;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Expertiser.Importer
{
    public class ImportInsuranceCompany : IImporter, ITest
    {
        public bool DoWork(Action<object> action, params string[] args)
        {
            using (ContextExpertiser context = new ContextExpertiser())
            {
                foreach (var t in new ImportInsuranceCompanyReader().Get())
                    context.TableInsuranceCompany.Add(t);
                context.SaveChanges();
            }
            return true;
        }
    }
    public class ImportInsuranceCompanyReader
    {
        private static Regex Regex = new Regex("<option[^<]*<");
        public List<TableInsuranceCompany> Get()
        {
            List<TableInsuranceCompany> insuranceCompanies = new List<TableInsuranceCompany>();
            using StreamReader reader = new StreamReader(@"C:\Users\aless\OneDrive\Desktop\assicurazione.html");
            foreach (var t in Regex.Matches(reader.ReadToEnd()))
            {
                string value = t.ToString();
                string externalId = value.Split('"')[1];
                string name = value.Split('>')[1].Trim('<');
                insuranceCompanies.Add(new TableInsuranceCompany()
                {
                    ExternalId = externalId,
                    Label = name,
                    IsActive = true,
                });
            }
            return insuranceCompanies;
        }
    }
}
