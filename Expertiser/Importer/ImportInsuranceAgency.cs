using Expertiser.Database.Models;
using Rystem.Interfaces.Utility.Tester;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Expertiser.Importer
{
    public class ImportInsuranceAgency : IImporter, ITest
    {
        public bool DoWork(Action<object> action, params string[] args)
        {
            using (ContextExpertiser context = new ContextExpertiser())
            {
                foreach (var t in new InsuranceAgencyReader().Get(context.TableInsuranceCompany.ToList()))
                    context.TableInsuranceAgency.Add(t);
                context.SaveChanges();
            }
            return true;
        }
    }
    public class InsuranceAgencyReader
    {
        private static Regex Regex = new Regex("<option[^<]*<");
        public List<TableInsuranceAgency> Get(List<TableInsuranceCompany> comapanies)
        {
            List<TableInsuranceAgency> insuranceAgencies = new List<TableInsuranceAgency>();
            using StreamReader reader = new StreamReader(@"C:\Users\aless\OneDrive\Desktop\agenzia.html");
            foreach (var t in Regex.Matches(reader.ReadToEnd()))
            {
                string value = t.ToString();
                string externalId = value.Split('"')[1];
                string name = value.Split('>')[1].Trim('<');
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var realCompany = comapanies.Where(x => name.ToLower().Replace(" ", "").Contains(x.Label.ToLower().Replace(" ", ""))).OrderByDescending(x => x.Label.Length).FirstOrDefault();
                insuranceAgencies.Add(new TableInsuranceAgency()
                {
                    ExternalId = externalId,
                    Label = name,
                    IsActive = true,
                    InsuranceCompanyId = realCompany.Id,
                });
            }
            return insuranceAgencies;
        }
    }
}
