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
    public class ImportInspectorate : IImporter, ITest
    {
        public bool DoWork(Action<object> action, params string[] args)
        {
            using (ContextExpertiser context = new ContextExpertiser())
            {
                foreach (var t in new InspectorateReader().Get(context.TableInsuranceCompany.ToList()))
                    context.TableInspectorate.Add(t);
                context.SaveChanges();
            }
            return true;
        }
    }
    public class InspectorateReader
    {
        private static Regex Regex = new Regex("<option[^<]*<");
        public List<TableInspectorate> Get(List<TableInsuranceCompany> comapanies)
        {
            List<TableInspectorate> inspectorates = new List<TableInspectorate>();
            using StreamReader reader = new StreamReader(@"C:\Users\aless\OneDrive\Desktop\ispettorato.html");
            foreach (var t in Regex.Matches(reader.ReadToEnd()))
            {
                string value = t.ToString();
                string externalId = value.Split('"')[1];
                string name = value.Split('>')[1].Trim('<');
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var realCompany = comapanies.Where(x => name.ToLower().Replace(" ", "").Contains(x.Label.ToLower().Replace(" ", ""))).OrderByDescending(x => x.Label.Length).FirstOrDefault();
                inspectorates.Add(new TableInspectorate()
                {
                    ExternalId = externalId,
                    Label = name,
                    IsActive = true,
                    InsuranceCompanyId = realCompany.Id,
                });
            }
            return inspectorates;
        }
    }
}
