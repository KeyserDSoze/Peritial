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
    public class ImportBranchType : IImporter, ITest
    {
        public bool DoWork(Action<object> action, params string[] args)
        {
            using (ContextExpertiser context = new ContextExpertiser())
            {
                foreach (var t in new BranchTypeReader().Get())
                    context.TableBranchType.Add(t);
                context.SaveChanges();
            }
            return true;
        }
    }
    public class BranchTypeReader
    {
        private static Regex Regex = new Regex("<option[^<]*<");
        public List<TableBranchType> Get()
        {
            List<TableBranchType> branchTypes = new List<TableBranchType>();
            using StreamReader reader = new StreamReader(@"C:\Users\aless\OneDrive\Desktop\tiporamo.html");
            foreach (var t in Regex.Matches(reader.ReadToEnd()))
            {
                string value = t.ToString();
                string externalId = value.Split('"')[1];
                string name = value.Split('>')[1].Trim('<');
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                branchTypes.Add(new TableBranchType()
                {
                    Label = name,
                });
            }
            return branchTypes;
        }
    }
}
